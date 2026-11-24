using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatingsNg.Api
{
    /// <summary>
    /// API controller for IMDb ratings plugin management and status.
    /// </summary>
    [ApiController]
    [Route("Plugins/ImdbRatings")]
    [Produces(MediaTypeNames.Application.Json)]
    public partial class ImdbRatingsController : ControllerBase
    {
        private static readonly BaseItemKind[] VotedKinds = { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode };
        private static readonly TimeSpan VotesCacheTime = TimeSpan.FromHours(1);
        private static readonly object VotesLock = new object();
        private static readonly SemaphoreSlim ScoresSlot = new SemaphoreSlim(1, 1);
        private static readonly HttpClient TmdbHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly TimeSpan PersonCacheTime = TimeSpan.FromDays(30);
        private static readonly TimeSpan TitleCacheTime = TimeSpan.FromHours(12);
        private static readonly ConcurrentDictionary<string, Task<string?>> TitleBuilds = new ConcurrentDictionary<string, Task<string?>>(StringComparer.Ordinal);
        private static readonly SemaphoreSlim TitleSlots = new SemaphoreSlim(3, 3);
        private static readonly ConcurrentDictionary<string, (DateTime At, string Json)> FactsCache = new ConcurrentDictionary<string, (DateTime At, string Json)>(StringComparer.Ordinal);
        private static readonly TimeSpan FactsCacheTime = TimeSpan.FromHours(6);
        private static readonly HashSet<string> CrewJobs = new HashSet<string>(StringComparer.Ordinal)
        {
            "Director", "Writer", "Screenplay", "Creator", "Novel", "Story", "Characters", "Executive Producer", "Producer"
        };

        private readonly ILogger<ImdbRatingsController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly ISessionManager _sessionManager;

        private static Dictionary<string, ImdbVotes>? _votesCache;
        private static DateTime _votesCachedAtUtc;
        private static Task<string?>? _historyCheck;
        private static (DateTime At, List<JsonObject> Items)? _shelf;

        /// <summary>
        /// Initializes a new instance of the <see cref="ImdbRatingsController"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="mediaSourceManager">The media source manager.</param>
        /// <param name="sessionManager">The session manager.</param>
        public ImdbRatingsController(ILogger<ImdbRatingsController> logger, ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ISessionManager sessionManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _sessionManager = sessionManager;
        }

        /// <summary>
        /// Gets the current status of the IMDb ratings database.
        /// </summary>
        /// <response code="200">Database status returned.</response>
        /// <returns>A <see cref="DatabaseStatus"/> object.</returns>
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<DatabaseStatus>> GetStatus()
        {
            using var manager = new IMDbRatingsManager(_logger);
            var status = await manager.GetStatusAsync().ConfigureAwait(false);
            return Ok(status);
        }

        /// <summary>
        /// Gets the IMDb rating and number of votes for every film, show and episode in the library,
        /// keyed by Jellyfin item id. Small enough for a client to fetch once at start.
        /// </summary>
        /// <response code="200">Ratings and votes returned.</response>
        /// <returns>A dictionary of item id to rating and votes.</returns>
        [HttpGet("Votes")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<Dictionary<string, ImdbVotes>>> GetVotes()
        {
            lock (VotesLock)
            {
                if (_votesCache != null && DateTime.UtcNow - _votesCachedAtUtc < VotesCacheTime)
                {
                    return Ok(_votesCache);
                }
            }

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = VotedKinds,
                IsVirtualItem = false
            };
            var items = _libraryManager.GetItemList(query);
            var requests = new List<VoteRequest>(items.Count);
            foreach (var item in items)
            {
                var request = new VoteRequest
                {
                    Key = item.Id.ToString("N", CultureInfo.InvariantCulture),
                    ImdbId = NumericImdbId(item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb)),
                };

                if (request.ImdbId == null && item is Episode episode)
                {
                    var series = episode.Series ?? (episode.SeriesId != Guid.Empty ? _libraryManager.GetItemById(episode.SeriesId) as Series : null);
                    request.SeriesImdbId = NumericImdbId(series?.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb));
                    request.Season = episode.ParentIndexNumber;
                    request.Episode = episode.IndexNumber;
                }

                if (request.ImdbId != null || request.SeriesImdbId != null)
                {
                    requests.Add(request);
                }
            }

            using var manager = new IMDbRatingsManager(_logger);
            var votes = await manager.GetVotesAsync(requests).ConfigureAwait(false);
            if (votes.Count > 0)
            {
                lock (VotesLock)
                {
                    _votesCache = votes;
                    _votesCachedAtUtc = DateTime.UtcNow;
                }
            }

            return Ok(votes);
        }

        /// <summary>Runs one of the M2 server's Python scripts (in ~/MediaServer/m2-server, with nice); its last line of output.</summary>
        private async Task<string?> RunM2Script(string[] args, TimeSpan timeout, string what)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(home, "MediaServer", "m2-server");
            if (!Directory.Exists(Path.Combine(dir, "daily")))
            {
                return null;
            }

            var key = what;
            await TitleSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                // nice: a title lookup never takes CPU from playback
                var psi = new ProcessStartInfo("/usr/bin/nice")
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var a in new[] { "-n", "10", "/usr/bin/python3" })
                {
                    psi.ArgumentList.Add(a);
                }

                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return null;
                }

                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(timeout);
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    _logger.LogWarning("{What} took too long", key);
                    return null;
                }

                var text = (await output.ConfigureAwait(false)).Trim();
                var err = (await errors.ConfigureAwait(false)).Trim();
                if (process.ExitCode != 0)
                {
                    _logger.LogWarning("{What} failed ({Code}): {Error}", key, process.ExitCode, err.Length > 500 ? err[^500..] : err);
                    return null;
                }

                if (err.Length > 0)
                {
                    _logger.LogInformation("{Line}", err.Length > 200 ? err[^200..] : err);
                }

                var lines = text.Split('\n');
                return lines[^1].Trim();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogWarning(ex, "{What} could not start", key);
                return null;
            }
            finally
            {
                TitleSlots.Release();
            }
        }

        private ContentResult Answer(bool ok, string message)
        {
            return Content(new JsonObject { ["ok"] = ok, ["message"] = message }.ToJsonString(), MediaTypeNames.Application.Json);
        }

        private static int? NumericImdbId(string? imdbId)
        {
            if (string.IsNullOrWhiteSpace(imdbId) || !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return int.TryParse(imdbId.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : null;
        }
    }
}
