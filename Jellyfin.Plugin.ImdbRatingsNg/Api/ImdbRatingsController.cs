using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    public class ImdbRatingsController : ControllerBase
    {
        private static readonly BaseItemKind[] VotedKinds = { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode };
        private static readonly TimeSpan VotesCacheTime = TimeSpan.FromHours(1);
        private static readonly object VotesLock = new object();
        private static readonly HttpClient TmdbHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly TimeSpan PersonCacheTime = TimeSpan.FromDays(30);
        private static readonly TimeSpan TitleCacheTime = TimeSpan.FromHours(12);
        private static readonly ConcurrentDictionary<string, Task<string?>> TitleBuilds = new ConcurrentDictionary<string, Task<string?>>(StringComparer.Ordinal);
        private static readonly SemaphoreSlim TitleSlots = new SemaphoreSlim(3, 3);
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

        /// <summary>
        /// Gets one of the streaming lists ("library" or "catalog") that the media server writes into
        /// this plugin's data folder once a day.
        /// </summary>
        /// <param name="name">Which list: library or catalog.</param>
        /// <response code="200">The list as JSON.</response>
        /// <response code="404">The list has not been written yet.</response>
        /// <returns>The JSON file.</returns>
        [HttpGet("Streaming/{name}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetStreamingList([FromRoute] string name)
        {
            if (!string.Equals(name, "library", StringComparison.Ordinal) && !string.Equals(name, "catalog", StringComparison.Ordinal))
            {
                return NotFound();
            }

            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            var path = Path.Combine(folder, "streaming-" + name + ".json");
            if (!System.IO.File.Exists(path))
            {
                return NotFound();
            }

            return PhysicalFile(path, MediaTypeNames.Application.Json);
        }

        /// <summary>
        /// Gets one title's page (cast, crew, trailers, titles like it, about) for the TV app's title page. The daily
        /// run writes one small file per Netflix / Prime Video / Theatre / Watchlist title into "titles" in this
        /// plugin's data folder.
        /// </summary>
        /// <param name="key">"movie-123" or "tv-456" (TMDB ids).</param>
        /// <response code="200">The page as JSON.</response>
        /// <response code="404">No page for that title.</response>
        /// <returns>The JSON file.</returns>
        [HttpGet("Streaming/title/{key}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetTitlePage([FromRoute] string key)
        {
            if (string.IsNullOrEmpty(key) || !System.Text.RegularExpressions.Regex.IsMatch(key, "^(movie|tv)-[0-9]{1,9}$"))
            {
                return NotFound();
            }

            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            var path = Path.Combine(folder, "titles", key + ".json");
            if (!System.IO.File.Exists(path))
            {
                return NotFound();
            }

            return PhysicalFile(path, MediaTypeNames.Application.Json);
        }

        /// <summary>
        /// Gets the status that the M2 server scripts write into this plugin's data folder: "importer" (the media
        /// importer, every 2 minutes), "daily" (the daily data run) or "backup" (the weekly Jellyfin backup). Shown on this plugin's settings page.
        /// </summary>
        /// <param name="name">Which status: importer or daily.</param>
        /// <response code="200">The status as JSON.</response>
        /// <response code="404">Not written yet.</response>
        /// <returns>The JSON file.</returns>
        [HttpGet("ServerStatus/{name}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetServerStatus([FromRoute] string name)
        {
            if (!string.Equals(name, "importer", StringComparison.Ordinal) && !string.Equals(name, "daily", StringComparison.Ordinal)
                && !string.Equals(name, "backup", StringComparison.Ordinal))
            {
                return NotFound();
            }

            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            var path = Path.Combine(folder, "status-" + name + ".json");
            if (!System.IO.File.Exists(path))
            {
                return NotFound();
            }

            return PhysicalFile(path, MediaTypeNames.Application.Json);
        }

        /// <summary>
        /// Gets one person's whole career (acting, directing, writing, producing) from TMDB, trimmed to what the TV's
        /// person panel shows, and kept for 30 days in this plugin's data folder (people/{id}.json). The TMDB key is
        /// read from tmdb.key in the same folder (written by the M2 server; never returned).
        /// </summary>
        /// <param name="tmdbId">The person's TMDB id.</param>
        /// <response code="200">The credits as JSON.</response>
        /// <response code="404">Unknown person, or TMDB can't be reached and nothing is saved yet.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("Person/{tmdbId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetPersonCredits([FromRoute] string tmdbId)
        {
            if (string.IsNullOrEmpty(tmdbId) || !Regex.IsMatch(tmdbId, "^[0-9]{1,9}$"))
            {
                return NotFound();
            }

            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            var dir = Path.Combine(folder, "people");
            var path = Path.Combine(dir, tmdbId + ".json");
            bool saved = System.IO.File.Exists(path);
            if (saved && DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(path) < PersonCacheTime)
            {
                return PhysicalFile(path, MediaTypeNames.Application.Json);
            }

            var keyFile = Path.Combine(folder, "tmdb.key");
            if (!System.IO.File.Exists(keyFile))
            {
                return saved ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
            }

            try
            {
                var key = (await System.IO.File.ReadAllTextAsync(keyFile).ConfigureAwait(false)).Trim();
                var url = new Uri("https://api.themoviedb.org/3/person/" + tmdbId + "/combined_credits?language=en-US&api_key=" + Uri.EscapeDataString(key));
                using var response = await TmdbHttp.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return saved ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
                }

                var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var trimmed = TrimCredits(raw);
                Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                await System.IO.File.WriteAllTextAsync(tmp, trimmed).ConfigureAwait(false);
                System.IO.File.Move(tmp, path, true);
                return Content(trimmed, MediaTypeNames.Application.Json);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Person credits for {Id} unavailable", tmdbId);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Person credits for {Id} timed out", tmdbId);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Person credits for {Id} unreadable", tmdbId);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Person credits for {Id} not saved", tmdbId);
            }

            return saved ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
        }

        /// <summary>
        /// Gets everything about one title for the TV (card, ratings, where it streams in India and worldwide, the
        /// Netflix link, its journey and its My list status), built on demand by the M2 server's title service
        /// (~/MediaServer/m2-server, "python3 -m daily.title_service movie-123") and kept in cards/{key}.json.
        /// A copy younger than 12 hours is returned at once; an older one is returned at once too while a fresh one is
        /// built in the background; with no copy at all the request waits for the build (about a second).
        /// </summary>
        /// <param name="key">"movie-123" or "tv-456" (TMDB ids).</param>
        /// <param name="fresh">True to rebuild now even if a recent copy exists.</param>
        /// <response code="200">The card as JSON.</response>
        /// <response code="404">Bad request, or the title can't be built and nothing is saved.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("Title/{key}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetTitle([FromRoute] string key, [FromQuery] bool fresh = false)
        {
            if (string.IsNullOrEmpty(key) || !Regex.IsMatch(key, "^(movie|tv)-[0-9]{1,9}$"))
            {
                return NotFound();
            }

            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            var path = Path.Combine(folder, "cards", key + ".json");
            bool saved = System.IO.File.Exists(path);
            if (saved && !fresh)
            {
                if (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(path) >= TitleCacheTime)
                {
                    _ = BuildTitle(key); // refresh in the background; this answer doesn't wait
                }

                return PhysicalFile(path, MediaTypeNames.Application.Json);
            }

            var build = BuildTitle(key);
            var done = await Task.WhenAny(build, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            if (done == build && await build.ConfigureAwait(false) is string json)
            {
                return Content(json, MediaTypeNames.Application.Json);
            }

            return System.IO.File.Exists(path) ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
        }

        private Task<string?> BuildTitle(string key)
        {
            return TitleBuilds.GetOrAdd(key, k => Task.Run(async () =>
            {
                try
                {
                    return await RunTitleService(k).ConfigureAwait(false);
                }
                finally
                {
                    TitleBuilds.TryRemove(k, out _);
                }
            }));
        }

        private async Task<string?> RunTitleService(string key)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(home, "MediaServer", "m2-server");
            if (!Directory.Exists(Path.Combine(dir, "daily")))
            {
                return null;
            }

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
                foreach (var a in new[] { "-n", "10", "/usr/bin/python3", "-m", "daily.title_service", key })
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
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

                    _logger.LogWarning("Title service for {Key} took too long", key);
                    return null;
                }

                var text = (await output.ConfigureAwait(false)).Trim();
                var err = (await errors.ConfigureAwait(false)).Trim();
                if (process.ExitCode != 0 || text.Length == 0)
                {
                    _logger.LogWarning("Title service for {Key} failed ({Code}): {Error}", key, process.ExitCode, err.Length > 500 ? err[^500..] : err);
                    return null;
                }

                _logger.LogInformation("{Line}", err.Length > 200 ? err[^200..] : err);
                var lines = text.Split('\n');
                return lines[^1].Trim();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogWarning(ex, "Title service for {Key} could not start", key);
                return null;
            }
            finally
            {
                TitleSlots.Release();
            }
        }

        /// <summary>
        /// Gets the YouTube key of a title's trailer from TMDB (English first, then the title's own language), kept
        /// for 30 days in this plugin's data folder (trailers/{key}.json). Used by the TV for titles outside the
        /// daily lists (a person's "All work").
        /// </summary>
        /// <param name="key">"movie-123" or "tv-456" (TMDB ids).</param>
        /// <response code="200">{"key": "YouTube key or null"}.</response>
        /// <response code="404">Bad request or TMDB unreachable with nothing saved.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("Trailer/{key}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetTrailer([FromRoute] string key)
        {
            if (string.IsNullOrEmpty(key) || !Regex.IsMatch(key, "^(movie|tv)-[0-9]{1,9}$"))
            {
                return NotFound();
            }

            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            var dir = Path.Combine(folder, "trailers");
            var path = Path.Combine(dir, key + ".json");
            bool saved = System.IO.File.Exists(path);
            if (saved && DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(path) < PersonCacheTime)
            {
                return PhysicalFile(path, MediaTypeNames.Application.Json);
            }

            var keyFile = Path.Combine(folder, "tmdb.key");
            if (!System.IO.File.Exists(keyFile))
            {
                return saved ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
            }

            try
            {
                var apiKey = Uri.EscapeDataString((await System.IO.File.ReadAllTextAsync(keyFile).ConfigureAwait(false)).Trim());
                var parts = key.Split('-');
                var baseUrl = "https://api.themoviedb.org/3/" + parts[0] + "/" + parts[1];
                var details = JsonNode.Parse(await TmdbHttp.GetStringAsync(new Uri(baseUrl + "?append_to_response=videos&include_video_language=en,null&api_key=" + apiKey)).ConfigureAwait(false));
                var youtube = BestTrailer(details?["videos"]?["results"]?.AsArray());
                var language = details?["original_language"]?.GetValue<string>();
                if (youtube is null && !string.IsNullOrEmpty(language) && !string.Equals(language, "en", StringComparison.Ordinal))
                {
                    var own = JsonNode.Parse(await TmdbHttp.GetStringAsync(new Uri(baseUrl + "/videos?include_video_language=" + Uri.EscapeDataString(language) + ",null&api_key=" + apiKey)).ConfigureAwait(false));
                    youtube = BestTrailer(own?["results"]?.AsArray());
                }

                var json = new JsonObject { ["key"] = youtube }.ToJsonString();
                Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                await System.IO.File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
                System.IO.File.Move(tmp, path, true);
                return Content(json, MediaTypeNames.Application.Json);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Trailer for {Key} unavailable", key);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Trailer for {Key} timed out", key);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Trailer for {Key} unreadable", key);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Trailer for {Key} not saved", key);
            }

            return saved ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
        }

        /// <summary>
        /// Gets the original download's release name of a film or episode file (e.g. "Movie.2019.1080p.BluRay.x264-GRP"),
        /// from origins.json in this plugin's data folder (written by the M2 server's importer). The TV's subtitle search
        /// uses it to mark subtitles made for the same release.
        /// </summary>
        /// <param name="itemId">The film or episode.</param>
        /// <response code="200">{"name": "release name"}.</response>
        /// <response code="404">Unknown item, or no name recorded for its file.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("Origin/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetOrigin([FromRoute] Guid itemId)
        {
            var folder = Plugin.Instance?.DataFolderPath;
            var item = _libraryManager.GetItemById(itemId);
            if (string.IsNullOrEmpty(folder) || item is null || string.IsNullOrEmpty(item.Path))
            {
                return NotFound();
            }

            var file = Path.Combine(folder, "origins.json");
            if (!System.IO.File.Exists(file))
            {
                return NotFound();
            }

            try
            {
                var map = JsonNode.Parse(System.IO.File.ReadAllText(file))?.AsObject();
                var stem = Path.Combine(Path.GetDirectoryName(item.Path) ?? string.Empty, Path.GetFileNameWithoutExtension(item.Path));
                var name = map?[stem]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    return NotFound();
                }

                return Content(new JsonObject { ["name"] = name }.ToJsonString(), MediaTypeNames.Application.Json);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "origins.json unreadable");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "origins.json unreadable");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "origins.json has an unexpected shape");
            }

            return NotFound();
        }

        /// <summary>
        /// Asks the M2 server to check, and if needed fix, the timing of the subtitle just downloaded for this item
        /// (subtitle-checks/{id}.json in this plugin's data folder; the importer picks it up within 2 minutes).
        /// </summary>
        /// <param name="itemId">The film or episode.</param>
        /// <response code="200">{"status": "waiting"}.</response>
        /// <response code="404">Unknown item.</response>
        /// <returns>The JSON.</returns>
        [HttpPost("CheckSubtitle/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult RequestSubtitleCheck([FromRoute] Guid itemId)
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder) || _libraryManager.GetItemById(itemId) is null)
            {
                return NotFound();
            }

            var dir = Path.Combine(folder, "subtitle-checks");
            var id = itemId.ToString("N", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(dir);
            var request = new JsonObject
            {
                ["id"] = id,
                ["requested"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            }.ToJsonString();
            var path = Path.Combine(dir, id + ".json");
            System.IO.File.WriteAllText(path + ".tmp", request);
            System.IO.File.Move(path + ".tmp", path, true);
            System.IO.File.Delete(Path.Combine(dir, id + ".result.json"));
            return Content(new JsonObject { ["status"] = "waiting" }.ToJsonString(), MediaTypeNames.Application.Json);
        }

        /// <summary>
        /// Gets how the subtitle timing check for this item went: "waiting", "checking", "ok" (in sync), "fixed"
        /// (re-timed to the film's speech) or "flagged" (doesn't fit this file), with a short message.
        /// </summary>
        /// <param name="itemId">The film or episode.</param>
        /// <response code="200">The status as JSON.</response>
        /// <response code="404">No check asked for.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("CheckSubtitle/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetSubtitleCheck([FromRoute] Guid itemId)
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            var dir = Path.Combine(folder, "subtitle-checks");
            var id = itemId.ToString("N", CultureInfo.InvariantCulture);
            if (System.IO.File.Exists(Path.Combine(dir, id + ".json")))
            {
                return Content(new JsonObject { ["status"] = "waiting" }.ToJsonString(), MediaTypeNames.Application.Json);
            }

            var result = Path.Combine(dir, id + ".result.json");
            return System.IO.File.Exists(result) ? PhysicalFile(result, MediaTypeNames.Application.Json) : NotFound();
        }

        /// <summary>
        /// Deletes one version (one file) of a film or episode that has two or more, leaving the others and every
        /// subtitle file in the folder alone (they may belong to another version). Refuses when it is the only version,
        /// when the file is outside the libraries' folders, or while the title is playing. Then starts a library scan,
        /// so the title shows the remaining version(s).
        /// </summary>
        /// <param name="itemId">The film or episode.</param>
        /// <param name="sourceId">The version (media source id) to delete.</param>
        /// <response code="200">{"ok": true|false, "message": "..."}.</response>
        /// <response code="404">Unknown item.</response>
        /// <returns>The JSON.</returns>
        [HttpPost("DeleteVersion/{itemId}/{sourceId}")]
        [Authorize(Policy = "RequiresElevation")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult DeleteVersion([FromRoute] Guid itemId, [FromRoute] string sourceId)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null || !Guid.TryParse(sourceId, out var sourceGuid))
            {
                return NotFound();
            }

            var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
            if (sources.Count < 2)
            {
                return Answer(false, "This title has only one version; use Delete instead");
            }

            MediaBrowser.Model.Dto.MediaSourceInfo? source = null;
            foreach (var s in sources)
            {
                if (Guid.TryParse(s.Id, out var g) && g == sourceGuid)
                {
                    source = s;
                }
            }

            if (source is null || string.IsNullOrEmpty(source.Path) || !System.IO.File.Exists(source.Path))
            {
                return Answer(false, "That version's file wasn't found");
            }

            var path = Path.GetFullPath(source.Path);
            bool inLibrary = false;
            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                foreach (var location in folder.Locations ?? Array.Empty<string>())
                {
                    var root = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (path.StartsWith(root, StringComparison.Ordinal))
                    {
                        inLibrary = true;
                    }
                }
            }

            if (!inLibrary)
            {
                return Answer(false, "That file is outside the library folders");
            }

            foreach (var session in _sessionManager.Sessions)
            {
                var playing = session.NowPlayingItem;
                if (playing is null)
                {
                    continue;
                }

                if (playing.Id == itemId || playing.Id == sourceGuid
                    || (Guid.TryParse(session.PlayState?.MediaSourceId, out var playingSource) && playingSource == sourceGuid))
                {
                    return Answer(false, "This title is playing; stop it first");
                }
            }

            try
            {
                System.IO.File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Deleting version {Path} failed", path);
                return Answer(false, "Couldn't delete the file: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Deleting version {Path} failed", path);
                return Answer(false, "Not allowed to delete the file");
            }

            _logger.LogInformation("Deleted one version of {Name}: {Path}", item.Name, path);
            _libraryManager.QueueLibraryScan();
            return Answer(true, "Version deleted (" + Path.GetFileName(path) + "). The library updates in a minute or two.");
        }

        private ContentResult Answer(bool ok, string message)
        {
            return Content(new JsonObject { ["ok"] = ok, ["message"] = message }.ToJsonString(), MediaTypeNames.Application.Json);
        }

        private static string? BestTrailer(JsonArray? videos)
        {
            string? best = null;
            int bestRank = int.MaxValue;
            string bestDate = string.Empty;
            foreach (var v in videos ?? new JsonArray())
            {
                if (v is null || !string.Equals(v["site"]?.GetValue<string>(), "YouTube", StringComparison.Ordinal))
                {
                    continue;
                }

                var type = v["type"]?.GetValue<string>();
                bool official = v["official"]?.GetValue<bool>() ?? false;
                int rank = type switch
                {
                    "Trailer" => official ? 0 : 1,
                    "Teaser" => official ? 2 : 3,
                    _ => -1,
                };
                if (rank < 0)
                {
                    continue;
                }

                var date = v["published_at"]?.GetValue<string>() ?? string.Empty;
                if (rank < bestRank || (rank == bestRank && string.CompareOrdinal(date, bestDate) < 0))
                {
                    best = v["key"]?.GetValue<string>();
                    bestRank = rank;
                    bestDate = date;
                }
            }

            return best;
        }

        private static string TrimCredits(string raw)
        {
            var root = JsonNode.Parse(raw) ?? new JsonObject();
            var cast = new JsonArray();
            foreach (var c in root["cast"]?.AsArray() ?? new JsonArray())
            {
                if (c is null)
                {
                    continue;
                }

                var o = Entry(c);
                o["role"] = c["character"]?.GetValue<string>() ?? string.Empty;
                if (c["episode_count"] is JsonNode n)
                {
                    o["eps"] = n.GetValue<int>();
                }

                cast.Add(o);
            }

            var crew = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var c in root["crew"]?.AsArray() ?? new JsonArray())
            {
                var job = c?["job"]?.GetValue<string>();
                if (c is null || job is null || !CrewJobs.Contains(job))
                {
                    continue;
                }

                var key = (c["media_type"]?.GetValue<string>() ?? string.Empty) + c["id"]?.ToString();
                if (crew.TryGetValue(key, out var existing))
                {
                    existing["role"] = existing["role"]?.GetValue<string>() + ", " + job;
                    continue;
                }

                var o = Entry(c);
                o["role"] = job;
                crew[key] = o;
            }

            var crewArray = new JsonArray();
            foreach (var o in crew.Values)
            {
                crewArray.Add(o);
            }

            var result = new JsonObject
            {
                ["at"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["cast"] = cast,
                ["crew"] = crewArray,
            };
            return result.ToJsonString();
        }

        private static JsonObject Entry(JsonNode c)
        {
            var type = c["media_type"]?.GetValue<string>() ?? "movie";
            var date = c["release_date"]?.GetValue<string>() ?? c["first_air_date"]?.GetValue<string>() ?? string.Empty;
            return new JsonObject
            {
                ["id"] = c["id"]?.GetValue<int>() ?? 0,
                ["tv"] = string.Equals(type, "tv", StringComparison.Ordinal),
                ["title"] = c["title"]?.GetValue<string>() ?? c["name"]?.GetValue<string>() ?? string.Empty,
                ["year"] = date.Length >= 4 ? date[..4] : string.Empty,
                ["date"] = date,
                ["poster"] = c["poster_path"]?.GetValue<string>(),
                ["rating"] = Math.Round(c["vote_average"]?.GetValue<double>() ?? 0, 1),
                ["votes"] = c["vote_count"]?.GetValue<int>() ?? 0,
                ["pop"] = Math.Round(c["popularity"]?.GetValue<double>() ?? 0, 1),
                ["about"] = Shorten(c["overview"]?.GetValue<string>() ?? string.Empty, 400),
            };
        }

        private static string Shorten(string text, int max)
        {
            if (text.Length <= max)
            {
                return text;
            }

            var cut = text.LastIndexOf(' ', max);
            return string.Concat(text.AsSpan(0, cut > 0 ? cut : max), "…");
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
