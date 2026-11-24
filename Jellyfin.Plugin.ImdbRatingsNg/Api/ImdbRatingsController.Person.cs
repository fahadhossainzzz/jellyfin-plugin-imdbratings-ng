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
    /// NoBars API: person pages: profile, career, IMDb ratings (Trakt ids) and 🍅 / 🍿 (MDBList).
    /// </summary>
    public partial class ImdbRatingsController
    {
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
            // a copy saved before profiles were added (no "profile") is fetched again once
            var savedText = saved ? await System.IO.File.ReadAllTextAsync(path).ConfigureAwait(false) : null;
            // a copy saved before profiles (no "profile") or before IMDb ids and 🍅 ("ids": 4) is fetched again once
            if (savedText != null && DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(path) < PersonCacheTime
                && savedText.Contains("\"profile\"", StringComparison.Ordinal) && savedText.Contains("\"ids\":4", StringComparison.Ordinal))
            {
                return Content(await WithImdbRatings(savedText).ConfigureAwait(false), MediaTypeNames.Application.Json);
            }

            var keyFile = Path.Combine(folder, "tmdb.key");
            if (!System.IO.File.Exists(keyFile))
            {
                return savedText != null ? Content(await WithImdbRatings(savedText).ConfigureAwait(false), MediaTypeNames.Application.Json) : NotFound();
            }

            try
            {
                var key = (await System.IO.File.ReadAllTextAsync(keyFile).ConfigureAwait(false)).Trim();
                // one TMDB request: the profile, the whole career and the photos; height from Trakt alongside
                var url = new Uri("https://api.themoviedb.org/3/person/" + tmdbId + "?append_to_response=combined_credits,images&language=en-US&api_key=" + Uri.EscapeDataString(key));
                // Trakt alongside: height, and the IMDb id of every title in the career (kept with the copy)
                var traktTask = TraktPerson(folder, tmdbId);
                using var response = await TmdbHttp.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return savedText != null ? Content(await WithImdbRatings(savedText).ConfigureAwait(false), MediaTypeNames.Application.Json) : NotFound();
                }

                var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                // 🍅 and 🍿 for the whole career from MDBList: one request per 200 films / series
                var scores = await MdbListScores(folder, raw).ConfigureAwait(false);
                var trakt = await traktTask.ConfigureAwait(false);
                var trimmed = TrimCredits(raw, trakt.Height, trakt.Imdb, scores);
                Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                await System.IO.File.WriteAllTextAsync(tmp, trimmed).ConfigureAwait(false);
                System.IO.File.Move(tmp, path, true);
                return Content(await WithImdbRatings(trimmed).ConfigureAwait(false), MediaTypeNames.Application.Json);
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

            return savedText != null ? Content(await WithImdbRatings(savedText).ConfigureAwait(false), MediaTypeNames.Application.Json) : NotFound();
        }

        /// <summary>
        /// A person's height in centimetres from Trakt (TMDB has none), found by their TMDB id. Needs the Trakt app's
        /// public client id in trakt.client in this plugin's data folder (written by the M2 server). Null if unknown.
        /// </summary>
        private async Task<(int? Height, Dictionary<string, int> Imdb)> TraktPerson(string folder, string tmdbId)
        {
            var imdb = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var idFile = Path.Combine(folder, "trakt.client");
                if (!System.IO.File.Exists(idFile))
                {
                    return (null, imdb);
                }

                var client = (await System.IO.File.ReadAllTextAsync(idFile).ConfigureAwait(false)).Trim();
                async Task<JsonNode?> Get(string url)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("trakt-api-version", "2");
                    req.Headers.TryAddWithoutValidation("trakt-api-key", client);
                    req.Headers.TryAddWithoutValidation("User-Agent", "NoBars/1.0");
                    using var res = await TmdbHttp.SendAsync(req).ConfigureAwait(false);
                    return res.IsSuccessStatusCode ? JsonNode.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false)) : null;
                }

                var found = await Get("https://api.trakt.tv/search/tmdb/" + tmdbId + "?type=person").ConfigureAwait(false);
                var slug = found?.AsArray().Count > 0 ? found[0]?["person"]?["ids"]?["slug"]?.GetValue<string>() : null;
                if (string.IsNullOrEmpty(slug))
                {
                    return (null, imdb);
                }

                var personTask = Get("https://api.trakt.tv/people/" + Uri.EscapeDataString(slug) + "?extended=full");
                var moviesTask = Get("https://api.trakt.tv/people/" + Uri.EscapeDataString(slug) + "/movies");
                var showsTask = Get("https://api.trakt.tv/people/" + Uri.EscapeDataString(slug) + "/shows");
                await Task.WhenAll(personTask, moviesTask, showsTask).ConfigureAwait(false);

                // every title of the career (acting and behind the camera): TMDB id -> IMDb id
                void Collect(JsonNode? credits, string field, string prefix)
                {
                    void One(JsonNode? entry)
                    {
                        var ids = entry?[field]?["ids"];
                        var t = ids?["tmdb"];
                        var i = ids?["imdb"]?.GetValue<string>();
                        if (t != null && t.GetValueKind() == JsonValueKind.Number && i != null && i.StartsWith("tt", StringComparison.Ordinal)
                            && int.TryParse(i.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        {
                            imdb[prefix + t.GetValue<int>().ToString(CultureInfo.InvariantCulture)] = n;
                        }
                    }

                    foreach (var c in credits?["cast"]?.AsArray() ?? new JsonArray())
                    {
                        One(c);
                    }

                    if (credits?["crew"] is JsonObject crew)
                    {
                        foreach (var dept in crew)
                        {
                            foreach (var c in dept.Value?.AsArray() ?? new JsonArray())
                            {
                                One(c);
                            }
                        }
                    }
                }

                Collect(await moviesTask.ConfigureAwait(false), "movie", "movie:");
                Collect(await showsTask.ConfigureAwait(false), "show", "tv:");

                var h = (await personTask.ConfigureAwait(false))?["height"];
                if (h is null)
                {
                    return (null, imdb);
                }

                var value = h.GetValueKind() == JsonValueKind.Number ? h.GetValue<double>() : double.TryParse(h.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
                var cm = value < 3 ? value * 100 : value; // metres or centimetres
                return (cm >= 50 && cm <= 260 ? (int)Math.Round(cm) : null, imdb);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Trakt person for {Id} unavailable", tmdbId);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogDebug(ex, "Trakt person for {Id} timed out", tmdbId);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Trakt person for {Id} unreadable", tmdbId);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(ex, "Trakt person for {Id} unexpected", tmdbId);
            }

            return (null, imdb);
        }

        /// <summary>
        /// Adds today's IMDb rating and votes ("ir", "iv") to every title of a career that has an IMDb id, from the
        /// plugin's own daily IMDb database (local, about a millisecond for a whole career).
        /// </summary>
        private async Task<string> WithImdbRatings(string json)
        {
            try
            {
                if (JsonNode.Parse(json) is not JsonObject doc)
                {
                    return json;
                }

                var all = new List<JsonObject>();
                foreach (var part in new[] { "cast", "crew" })
                {
                    foreach (var n in doc[part]?.AsArray() ?? new JsonArray())
                    {
                        if (n is JsonObject o && o["imdb"] is JsonValue)
                        {
                            all.Add(o);
                        }
                    }
                }

                if (all.Count == 0)
                {
                    return json;
                }

                var requests = new List<VoteRequest>();
                foreach (var o in all)
                {
                    var id = o["imdb"]!.GetValue<int>();
                    requests.Add(new VoteRequest { Key = id.ToString(CultureInfo.InvariantCulture), ImdbId = id });
                }

                using var manager = new IMDbRatingsManager(_logger);
                var votes = await manager.GetVotesAsync(requests).ConfigureAwait(false);
                foreach (var o in all)
                {
                    if (votes.TryGetValue(o["imdb"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture), out var v))
                    {
                        o["ir"] = Math.Round(v.Rating, 1);
                        o["iv"] = v.Votes;
                    }
                }

                return doc.ToJsonString();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Person ratings not added");
                return json;
            }
        }

        /// <summary>
        /// 🍅 (critics) and 🍿 (audience) for every film and series of a career, from MDBList in batches (about one
        /// request per 200 titles; the free plan allows 1,000 a day). Each score found is also noted in
        /// mdblist-scores.jsonl, so the server's poster facts show it everywhere else too.
        /// </summary>
        private async Task<Dictionary<string, (int? Rt, int? Pop)>> MdbListScores(string folder, string raw)
        {
            var result = new Dictionary<string, (int? Rt, int? Pop)>(StringComparer.Ordinal);
            try
            {
                var keyFile = Path.Combine(folder, "mdblist.key");
                if (!System.IO.File.Exists(keyFile))
                {
                    return result;
                }

                var key = (await System.IO.File.ReadAllTextAsync(keyFile).ConfigureAwait(false)).Trim();
                var person = JsonNode.Parse(raw);
                var root = person?["combined_credits"] ?? person;
                var movies = new HashSet<int>();
                var shows = new HashSet<int>();
                foreach (var part in new[] { "cast", "crew" })
                {
                    foreach (var c in root?[part]?.AsArray() ?? new JsonArray())
                    {
                        var id = c?["id"]?.GetValue<int>() ?? 0;
                        if (id == 0 || c?["poster_path"] is null)
                        {
                            continue;
                        }

                        if (string.Equals(c["media_type"]?.GetValue<string>(), "tv", StringComparison.Ordinal))
                        {
                            shows.Add(id);
                        }
                        else
                        {
                            movies.Add(id);
                        }
                    }
                }

                var found = new List<string>();
                async Task Batch(string kind, List<int> ids, string prefix)
                {
                    var body = new JsonObject { ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()) };
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.mdblist.com/tmdb/" + kind + "?apikey=" + Uri.EscapeDataString(key))
                    {
                        Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
                    };
                    req.Headers.TryAddWithoutValidation("User-Agent", "NoBarsMediaServer/1.0");
                    using var res = await TmdbHttp.SendAsync(req).ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode || JsonNode.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false)) is not JsonArray list)
                    {
                        return;
                    }

                    foreach (var m in list)
                    {
                        var tmdb = m?["ids"]?["tmdb"];
                        if (tmdb is null || tmdb.GetValueKind() != JsonValueKind.Number)
                        {
                            continue;
                        }

                        int? rt = null;
                        int? pop = null;
                        foreach (var r in m?["ratings"]?.AsArray() ?? new JsonArray())
                        {
                            var v = r?["value"];
                            if (v is null || v.GetValueKind() != JsonValueKind.Number)
                            {
                                continue;
                            }

                            var source = r?["source"]?.GetValue<string>();
                            if (string.Equals(source, "tomatoes", StringComparison.Ordinal))
                            {
                                rt = (int)Math.Round(v.GetValue<double>());
                            }
                            else if (string.Equals(source, "popcorn", StringComparison.Ordinal))
                            {
                                pop = (int)Math.Round(v.GetValue<double>());
                            }
                        }

                        if (rt == null && pop == null)
                        {
                            continue;
                        }

                        lock (result)
                        {
                            result[prefix + tmdb.GetValue<int>().ToString(CultureInfo.InvariantCulture)] = (rt, pop);
                            var imdbId = m?["ids"]?["imdb"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(imdbId))
                            {
                                found.Add(new JsonObject { ["imdb"] = imdbId, ["rt"] = rt, ["pop"] = pop }.ToJsonString());
                            }
                        }
                    }
                }

                var tasks = new List<Task>();
                foreach (var chunk in movies.Chunk(200))
                {
                    tasks.Add(Batch("movie", chunk.ToList(), "movie:"));
                }

                foreach (var chunk in shows.Chunk(200))
                {
                    tasks.Add(Batch("show", chunk.ToList(), "tv:"));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                if (found.Count > 0)
                {
                    await ScoresSlot.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await System.IO.File.AppendAllLinesAsync(Path.Combine(folder, "mdblist-scores.jsonl"), found).ConfigureAwait(false);
                    }
                    finally
                    {
                        ScoresSlot.Release();
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException || ex is IOException || ex is InvalidOperationException)
            {
                _logger.LogDebug(ex, "MDBList scores unavailable");
            }

            return result;
        }

        private static string TrimCredits(string raw, int? heightCm = null, Dictionary<string, int>? imdb = null, Dictionary<string, (int? Rt, int? Pop)>? scores = null)
        {
            void Imdb(JsonObject o)
            {
                var k = (o["tv"]?.GetValue<bool>() == true ? "tv:" : "movie:") + o["id"]?.ToString();
                if (imdb != null && imdb.TryGetValue(k, out var n))
                {
                    o["imdb"] = n;
                }

                if (scores != null && scores.TryGetValue(k, out var sc))
                {
                    if (sc.Rt is int rt)
                    {
                        o["rt"] = rt;
                    }

                    if (sc.Pop is int pop)
                    {
                        // "pc": 🍿 audience score ("pop" is TMDB's popularity)
                        o["pc"] = pop;
                    }
                }
            }

            var person = JsonNode.Parse(raw) ?? new JsonObject();
            // the career sits under "combined_credits" when the profile is asked for too
            var root = person["combined_credits"] ?? person;
            var cast = new JsonArray();
            foreach (var c in root["cast"]?.AsArray() ?? new JsonArray())
            {
                if (c is null)
                {
                    continue;
                }

                var o = Entry(c);
                Imdb(o);
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
                Imdb(o);
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
                ["ids"] = 4,
                ["cast"] = cast,
                ["crew"] = crewArray,
            };
            if (person["name"] is not null)
            {
                var photos = new JsonArray();
                foreach (var img in person["images"]?["profiles"]?.AsArray() ?? new JsonArray())
                {
                    var file = img?["file_path"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(file) && photos.Count < 12)
                    {
                        photos.Add(file);
                    }
                }

                // other names worth showing: the ones in another script (हिन्दी, বাংলা, 日本語 ...), at most three
                var aka = new JsonArray();
                foreach (var n in person["also_known_as"]?.AsArray() ?? new JsonArray())
                {
                    var name = n?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name) && aka.Count < 3 && Regex.IsMatch(name, "[^\\u0000-\\u024F]"))
                    {
                        aka.Add(name);
                    }
                }

                result["profile"] = new JsonObject
                {
                    ["name"] = person["name"]?.GetValue<string>(),
                    ["bio"] = person["biography"]?.GetValue<string>(),
                    ["born"] = person["birthday"]?.GetValue<string>(),
                    ["died"] = person["deathday"]?.GetValue<string>(),
                    ["place"] = person["place_of_birth"]?.GetValue<string>(),
                    ["known"] = person["known_for_department"]?.GetValue<string>(),
                    ["photo"] = person["profile_path"]?.GetValue<string>(),
                    ["imdb"] = person["imdb_id"]?.GetValue<string>(),
                    ["height"] = heightCm,
                    ["aka"] = aka,
                    ["photos"] = photos,
                };
            }

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
    }
}
