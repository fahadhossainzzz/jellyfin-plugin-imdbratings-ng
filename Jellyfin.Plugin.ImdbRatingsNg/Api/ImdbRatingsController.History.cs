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
    /// NoBars API: your Trakt watch history: the copy, the History page (shelf) and marking from the TV.
    /// </summary>
    public partial class ImdbRatingsController
    {
        /// <summary>
        /// Gets your watch history (films and shows you've watched, by TMDB id, with the last date and how many times).
        /// Your history lives on Trakt; the M2 server keeps a copy that is only ever replaced from Trakt. Asking this
        /// checks Trakt live first ("has it changed?", one small request, at most every 30 seconds) and refreshes the
        /// copy if so, so the answer always matches Trakt.
        /// </summary>
        /// <response code="200">{"items": {"movie:123": ["2019-03-12", 2], "tv:456": [...]}}.</response>
        /// <response code="404">No copy yet (Trakt not connected).</response>
        /// <returns>The JSON.</returns>
        [HttpGet("History")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetHistory()
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            var path = Path.Combine(folder, "trakt-history.json");
            await CheckHistory(folder).ConfigureAwait(false);
            return System.IO.File.Exists(path) ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
        }

        /// <summary>
        /// Gets the History page from your Trakt history (history-shelf.json, built by the M2 server with posters and
        /// IMDb ratings). Without a row: the page's rows (Recently watched, then one per year), 24 titles each, with
        /// each row's total. With a row: one page of it for its "See all" grid, sorted and filtered.
        /// </summary>
        /// <param name="row">"recent", a year ("2025"), or "unknown" (no date).</param>
        /// <param name="offset">First title of the page.</param>
        /// <param name="limit">Titles in the page (at most 200).</param>
        /// <param name="sort">watched (default), rating, title, year.</param>
        /// <param name="kind">all (default), movie, tv.</param>
        /// <param name="genre">Only these genres (comma-separated; any of them).</param>
        /// <param name="reverse">The other way round.</param>
        /// <response code="200">{"rows": [{"id", "name", "total", "items"}]} or {"total", "items"}.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("History/Shelf")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult> GetHistoryShelf(
            [FromQuery] string? row,
            [FromQuery] int offset = 0,
            [FromQuery] int limit = 60,
            [FromQuery] string? sort = null,
            [FromQuery] string? kind = null,
            [FromQuery] string? genre = null,
            [FromQuery] bool reverse = false)
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            await CheckHistory(folder).ConfigureAwait(false);
            var all = ReadShelf(Path.Combine(folder, "history-shelf.json"));
            static string Year(JsonObject o)
            {
                var d = o["d"]?.GetValue<string>() ?? string.Empty;
                return d.Length >= 4 ? d.Substring(0, 4) : "unknown";
            }

            if (string.IsNullOrEmpty(row))
            {
                var rows = new JsonArray();
                void Add(string id, string name, List<JsonObject> items)
                {
                    if (items.Count == 0)
                    {
                        return;
                    }

                    var arr = new JsonArray();
                    foreach (var o in items.GetRange(0, Math.Min(24, items.Count)))
                    {
                        arr.Add(o.DeepClone());
                    }

                    rows.Add(new JsonObject { ["id"] = id, ["name"] = name, ["total"] = items.Count, ["items"] = arr });
                }

                Add("recent", "Recently watched", all.FindAll(o => !string.IsNullOrEmpty(o["d"]?.GetValue<string>())));
                var years = new List<string>();
                foreach (var o in all)
                {
                    var y = Year(o);
                    if (!years.Contains(y))
                    {
                        years.Add(y);
                    }
                }

                foreach (var y in years)
                {
                    if (y != "unknown")
                    {
                        Add(y, y, all.FindAll(o => Year(o) == y));
                    }
                }

                Add("unknown", "Watched, date unknown", all.FindAll(o => Year(o) == "unknown"));
                return Content(new JsonObject { ["rows"] = rows }.ToJsonString(), MediaTypeNames.Application.Json);
            }

            var list = row == "recent" ? all.FindAll(o => Year(o) != "unknown") : all.FindAll(o => Year(o) == row);
            if (kind == "movie" || kind == "tv")
            {
                list = list.FindAll(o => (o["k"]?.GetValue<string>() ?? string.Empty).StartsWith(kind + ":", StringComparison.Ordinal));
            }

            // every genre in the row, for the grid's Genre filter
            var genres = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in list)
            {
                if (o["g"] is JsonArray ga)
                {
                    foreach (var x in ga)
                    {
                        var name = x?.GetValue<string>();
                        if (!string.IsNullOrEmpty(name))
                        {
                            genres.Add(name);
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(genre))
            {
                var wanted = genre.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                list = list.FindAll(o => o["g"] is JsonArray g && g.Any(x => wanted.Contains(x?.GetValue<string>() ?? string.Empty, StringComparer.OrdinalIgnoreCase)));
            }

            static double Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
            list = sort switch
            {
                // titles with under 1,000 IMDb votes go last
                "rating" => list.OrderByDescending(o => Num(o, "v") >= 1000).ThenByDescending(o => Num(o, "r")).ToList(),
                "title" => list.OrderBy(o => o["t"]?.GetValue<string>() ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList(),
                "year" => list.OrderByDescending(o => Num(o, "y")).ToList(),
                _ => list,
            };
            if (reverse)
            {
                list = Enumerable.Reverse(list).ToList();
            }

            var page = new JsonArray();
            var start = Math.Clamp(offset, 0, list.Count);
            foreach (var o in list.Skip(start).Take(Math.Clamp(limit, 1, 200)))
            {
                page.Add(o.DeepClone());
            }

            var genreList = new JsonArray();
            foreach (var g in genres)
            {
                genreList.Add(g);
            }

            return Content(new JsonObject { ["total"] = list.Count, ["items"] = page, ["genres"] = genreList }.ToJsonString(), MediaTypeNames.Application.Json);
        }

        /// <summary>
        /// Asks Trakt (through the M2 server) whether your history changed, at most every 30 seconds, and waits up to
        /// 8 seconds for the copy to be refreshed.
        /// </summary>
        private async Task CheckHistory(string folder)
        {
            var checkedFile = Path.Combine(folder, "trakt-history.checked");
            var recent = System.IO.File.Exists(checkedFile) && DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(checkedFile) < TimeSpan.FromSeconds(30);
            if (!recent)
            {
                var run = _historyCheck ??= Task.Run(async () =>
                {
                    try
                    {
                        return await RunM2Script(new[] { "maintenance/trakt_sync.py", "--history" }, TimeSpan.FromSeconds(90), "History check").ConfigureAwait(false);
                    }
                    finally
                    {
                        _historyCheck = null;
                    }
                });
                await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The shelf's titles, read once and kept until the file changes.
        /// </summary>
        private List<JsonObject> ReadShelf(string path)
        {
            if (!System.IO.File.Exists(path))
            {
                return new List<JsonObject>();
            }

            var at = System.IO.File.GetLastWriteTimeUtc(path);
            var cached = _shelf;
            if (cached.HasValue && cached.Value.At == at)
            {
                return cached.Value.Items;
            }

            var items = new List<JsonObject>();
            try
            {
                if (JsonNode.Parse(System.IO.File.ReadAllText(path))?["items"] is JsonArray arr)
                {
                    foreach (var n in arr)
                    {
                        if (n is JsonObject o)
                        {
                            items.Add(o);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException)
            {
                _logger.LogWarning(ex, "History shelf unreadable");
                return cached?.Items ?? items;
            }

            _shelf = (at, items);
            return items;
        }

        /// <summary>
        /// Adds a viewing to your Trakt watch history from the TV (Trakt is where your history lives): a film, a whole
        /// series, one season or one episode, watched just now, on its release date, on a given day or on an unknown date.
        /// The server's copy of your history is refreshed at once.
        /// </summary>
        /// <param name="key">"movie-123" or "tv-456" (TMDB ids).</param>
        /// <param name="when">now, released, unknown, or a day (yyyy-MM-dd).</param>
        /// <param name="season">For a season or an episode of a series.</param>
        /// <param name="episode">For one episode.</param>
        /// <response code="200">{"ok": true, "added": 1}.</response>
        /// <response code="400">Bad request, or Trakt refused it.</response>
        /// <returns>The JSON.</returns>
        [HttpPost("History/Mark")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult> MarkWatched([FromQuery] string key, [FromQuery] string when, [FromQuery] int? season, [FromQuery] int? episode)
        {
            if (string.IsNullOrEmpty(key) || !Regex.IsMatch(key, "^(movie|tv)-[0-9]{1,9}$")
                || string.IsNullOrEmpty(when) || !Regex.IsMatch(when, "^(now|released|unknown|[0-9]{4}-[0-9]{2}-[0-9]{2})$"))
            {
                return BadRequest();
            }

            var args = new List<string> { "maintenance/trakt_sync.py", "--mark", key, when, season?.ToString(CultureInfo.InvariantCulture) ?? "-", episode?.ToString(CultureInfo.InvariantCulture) ?? "-" };
            var json = await RunM2Script(args.ToArray(), TimeSpan.FromSeconds(60), "Mark watched").ConfigureAwait(false);
            return string.IsNullOrEmpty(json) ? BadRequest() : Content(json, MediaTypeNames.Application.Json);
        }

        /// <summary>
        /// Removes every viewing of a film, a series, a season or an episode from your Trakt watch history (from the TV).
        /// </summary>
        /// <param name="key">"movie-123" or "tv-456" (TMDB ids).</param>
        /// <param name="season">For a season or an episode of a series.</param>
        /// <param name="episode">For one episode.</param>
        /// <response code="200">{"ok": true, "removed": 2}.</response>
        /// <response code="400">Bad request, or Trakt refused it.</response>
        /// <returns>The JSON.</returns>
        [HttpPost("History/Unmark")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult> UnmarkWatched([FromQuery] string key, [FromQuery] int? season, [FromQuery] int? episode)
        {
            if (string.IsNullOrEmpty(key) || !Regex.IsMatch(key, "^(movie|tv)-[0-9]{1,9}$"))
            {
                return BadRequest();
            }

            var args = new List<string> { "maintenance/trakt_sync.py", "--unmark", key, season?.ToString(CultureInfo.InvariantCulture) ?? "-", episode?.ToString(CultureInfo.InvariantCulture) ?? "-" };
            var json = await RunM2Script(args.ToArray(), TimeSpan.FromSeconds(60), "Remove from history").ConfigureAwait(false);
            return string.IsNullOrEmpty(json) ? BadRequest() : Content(json, MediaTypeNames.Application.Json);
        }
    }
}
