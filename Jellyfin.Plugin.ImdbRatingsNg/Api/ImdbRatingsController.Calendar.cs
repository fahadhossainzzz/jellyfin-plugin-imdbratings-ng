using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ImdbRatingsNg.Api
{
    /// <summary>
    /// NoBars API: the Calendar, one month at a time.
    /// </summary>
    public partial class ImdbRatingsController
    {
        private static readonly ConcurrentDictionary<string, Task<string?>> CalendarBuilds = new ConcurrentDictionary<string, Task<string?>>(StringComparer.Ordinal);

        /// <summary>
        /// Gets one month of the Calendar: per day, what is yours (new episodes, Watchlist films), what you watched and
        /// what the world has (cinema, digital, streaming premieres). Built by the M2 server (daily/calendar_page.py) and
        /// kept in calendar/{month}.json. A copy is rebuilt when it is older than 3 hours (a day for a month that is
        /// over) or older than your Trakt history; an old copy is answered at once while the new one is built; with no
        /// copy the request waits for the build.
        /// </summary>
        /// <param name="month">"2026-11".</param>
        /// <response code="200">{"month", "built", "days": {"2026-11-25": [items]}}.</response>
        /// <response code="404">Not available (bad month, or the build failed).</response>
        /// <returns>The JSON.</returns>
        [HttpGet("Calendar/{month}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetCalendarMonth([FromRoute] string month)
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(month) || !Regex.IsMatch(month, "^20[0-9]{2}-(0[1-9]|1[0-2])$"))
            {
                return NotFound();
            }

            var path = Path.Combine(folder, "calendar", month + ".json");
            var run = BuildCalendarIfStale(folder, month);
            if (run != null && !System.IO.File.Exists(path))
            {
                await run.ConfigureAwait(false);
            }

            // the next month too, in the background, so moving to it is instant
            var next = DateTime.ParseExact(month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddMonths(1);
            _ = BuildCalendarIfStale(folder, next.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));

            return System.IO.File.Exists(path) ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
        }

        /// <summary>
        /// Starts building a month of the Calendar when its copy is missing, older than 3 hours (a day for a month that
        /// is over) or older than your Trakt history; one build per month at a time.
        /// </summary>
        private Task<string?>? BuildCalendarIfStale(string folder, string month)
        {
            var path = Path.Combine(folder, "calendar", month + ".json");
            var history = Path.Combine(folder, "trakt-history.json");
            var exists = System.IO.File.Exists(path);
            var built = exists ? System.IO.File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            var past = string.CompareOrdinal(month, DateTime.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)) < 0;
            var stale = !exists
                || DateTime.UtcNow - built > (past ? TimeSpan.FromDays(1) : TimeSpan.FromHours(3))
                || (System.IO.File.Exists(history) && System.IO.File.GetLastWriteTimeUtc(history) > built);
            if (!stale)
            {
                return null;
            }

            return CalendarBuilds.GetOrAdd(month, m => Task.Run(async () =>
            {
                try
                {
                    return await RunM2Script(new[] { "maintenance/trakt_sync.py", "--calendar", m }, TimeSpan.FromSeconds(240), "Calendar " + m).ConfigureAwait(false);
                }
                finally
                {
                    CalendarBuilds.TryRemove(m, out _);
                }
            }));
        }
    }
}
