using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
        private static readonly HashSet<string> CrewJobs = new HashSet<string>(StringComparer.Ordinal)
        {
            "Director", "Writer", "Screenplay", "Creator", "Novel", "Story", "Characters", "Executive Producer", "Producer"
        };
        private readonly ILogger<ImdbRatingsController> _logger;
        private readonly ILibraryManager _libraryManager;

        private static Dictionary<string, ImdbVotes>? _votesCache;
        private static DateTime _votesCachedAtUtc;

        /// <summary>
        /// Initializes a new instance of the <see cref="ImdbRatingsController"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="libraryManager">The library manager.</param>
        public ImdbRatingsController(ILogger<ImdbRatingsController> logger, ILibraryManager libraryManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
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
            };
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
