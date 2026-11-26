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
    /// NoBars API: the Netflix / Prime / Theatre pages, title cards, trailers and poster facts.
    /// </summary>
    public partial class ImdbRatingsController
    {
        private static readonly object SlimLock = new object();
        private static int _watchlistSyncRunning;

        /// <summary>
        /// Gets one of the streaming lists ("library" or "catalog") that the media server writes into
        /// this plugin's data folder once a day.
        /// </summary>
        /// <param name="name">Which list: library or catalog.</param>
        /// <param name="service">Catalog only: one page's rows ("netflix", "prime", "cinema", "list"), without the
        /// titles' journeys (a title's own card has them): a fraction of the whole file, which the TV reads at once.</param>
        /// <response code="200">The list as JSON.</response>
        /// <response code="404">The list has not been written yet.</response>
        /// <returns>The JSON file.</returns>
        [HttpGet("Streaming/{name}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetStreamingList([FromRoute] string name, [FromQuery] string? service = null)
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

            if (string.Equals(name, "catalog", StringComparison.Ordinal) && !string.IsNullOrEmpty(service))
            {
                if (!Regex.IsMatch(service, "^[a-z]{2,12}$"))
                {
                    return NotFound();
                }

                var slim = SlimCatalog(folder, path, service);
                return slim == null ? NotFound() : PhysicalFile(slim, MediaTypeNames.Application.Json);
            }

            return PhysicalFile(path, MediaTypeNames.Application.Json);
        }

        /// <summary>
        /// One service's rows from the daily catalogue, without journeys, kept as streaming-catalog-{service}.json and
        /// made again whenever the daily catalogue is newer.
        /// </summary>
        private static string? SlimCatalog(string folder, string source, string service)
        {
            var slim = Path.Combine(folder, "streaming-catalog-" + service + ".json");
            lock (SlimLock)
            {
                if (System.IO.File.Exists(slim) && System.IO.File.GetLastWriteTimeUtc(slim) >= System.IO.File.GetLastWriteTimeUtc(source))
                {
                    return slim;
                }

                try
                {
                    var all = JsonNode.Parse(System.IO.File.ReadAllText(source)) as JsonObject;
                    var rows = (all?["services"] as JsonObject)?[service]?.DeepClone() as JsonObject;
                    if (rows == null)
                    {
                        return null;
                    }

                    foreach (var row in rows["rows"] as JsonArray ?? new JsonArray())
                    {
                        foreach (var item in (row?["items"] as JsonArray) ?? new JsonArray())
                        {
                            (item as JsonObject)?.Remove("journey");
                        }
                    }

                    var outDoc = new JsonObject
                    {
                        ["logos"] = all?["logos"]?.DeepClone(),
                        ["services"] = new JsonObject { [service] = rows },
                    };
                    var tmp = slim + ".tmp";
                    System.IO.File.WriteAllText(tmp, outDoc.ToJsonString());
                    System.IO.File.Move(tmp, slim, true);
                    return slim;
                }
                catch (IOException)
                {
                    return null;
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// The TV changed its Watchlist: Trakt is told now instead of at the Mac's next two-minute check.
        /// </summary>
        /// <response code="202">The sync was started (or one is already running).</response>
        /// <returns>Nothing.</returns>
        [HttpPost("Watchlist/Changed")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        public ActionResult WatchlistChanged()
        {
            if (Interlocked.CompareExchange(ref _watchlistSyncRunning, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1500).ConfigureAwait(false); // the TV's save lands first
                        await RunM2Script(new[] { "maintenance/trakt_sync.py" }, TimeSpan.FromSeconds(120), "Trakt sync").ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _watchlistSyncRunning, 0);
                    }
                });
            }

            return Accepted();
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
            if (done == build && await build.ConfigureAwait(false) is string json && json.Length > 0)
            {
                return Content(json, MediaTypeNames.Application.Json);
            }

            return System.IO.File.Exists(path) ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
        }

        /// <summary>
        /// Gets the facts for the bottom band of posters (IMDb rating and votes, box office, budget, 🍅 when known) for
        /// up to 300 titles at once: a person's career, "More like this". Built by the M2 server (daily/poster_facts.py):
        /// IMDb numbers from the plugin's own database, box office from TMDB (kept a week).
        /// </summary>
        /// <param name="keys">Comma-separated "movie-123" / "tv-456" (TMDB ids).</param>
        /// <response code="200">{"movie-123": {"r": 7.4, "v": 512345, "g": 343471816, "b": 185000000, "rt": 86}}.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("Facts")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult> GetFacts([FromQuery] string keys)
        {
            var list = (keys ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var wanted = new List<string>();
            foreach (var k in list)
            {
                if (Regex.IsMatch(k, "^(movie|tv)-[0-9]{1,9}$") && wanted.Count < 300 && !wanted.Contains(k))
                {
                    wanted.Add(k);
                }
            }

            // answers kept here for 6 hours: a row asked again (or a pop-up) is answered at once, without starting the
            // server's program; only titles not seen yet are looked up
            var now = DateTime.UtcNow;
            var missing = new List<string> { "-m", "daily.poster_facts" };
            foreach (var k in wanted)
            {
                if (!FactsCache.TryGetValue(k, out var hit) || now - hit.At > FactsCacheTime)
                {
                    missing.Add(k);
                }
            }

            if (missing.Count > 2)
            {
                var json = await RunM2Script(missing.ToArray(), TimeSpan.FromSeconds(60), "Poster facts").ConfigureAwait(false);
                JsonObject? got = null;
                try
                {
                    got = string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json) as JsonObject;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Poster facts unreadable");
                }

                if (got != null)
                {
                    for (var i = 2; i < missing.Count; i++)
                    {
                        // a title with no facts is remembered too, so it isn't looked up again and again
                        FactsCache[missing[i]] = (now, got[missing[i]]?.ToJsonString() ?? string.Empty);
                    }
                }
            }

            var result = new JsonObject();
            foreach (var k in wanted)
            {
                if (FactsCache.TryGetValue(k, out var hit) && hit.Json.Length > 0)
                {
                    result[k] = JsonNode.Parse(hit.Json);
                }
            }

            return Content(result.ToJsonString(), MediaTypeNames.Application.Json);
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

        private Task<string?> RunTitleService(string key)
        {
            return RunM2Script(new[] { "-m", "daily.title_service", key }, TimeSpan.FromSeconds(90), "Title service for " + key);
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
    }
}
