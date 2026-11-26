using System;
using System.Collections.Concurrent;
using System.Globalization;
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
    /// NoBars API: the Server report (what every job on the M2 Mac did on a day), shown on the dashboard's
    /// "Server report" page.
    /// </summary>
    public partial class ImdbRatingsController
    {
        private static readonly ConcurrentDictionary<string, Task<string?>> ReportBuilds = new ConcurrentDictionary<string, Task<string?>>(StringComparer.Ordinal);

        /// <summary>
        /// Gets one line per day (verdict, imports, viewings, stalls, errors, room on each drive) for the last 90 days.
        /// Rebuilt from the logs when it is more than 10 minutes old.
        /// </summary>
        /// <response code="200">{"built", "days": [...]}.</response>
        /// <response code="404">Not built yet.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("Report/days")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetReportDays()
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            var path = Path.Combine(folder, "reports", "index.json");
            await RefreshReport(path, null, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            return System.IO.File.Exists(path) ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
        }

        /// <summary>
        /// Gets the report of one day (Bangladesh time). Today's is rebuilt when more than 10 minutes old; another
        /// day's is built when it is missing.
        /// </summary>
        /// <param name="day">"2026-11-26".</param>
        /// <param name="fresh">True: read the logs again now.</param>
        /// <response code="200">The day's report.</response>
        /// <response code="404">Not available.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("Report/{day}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetReportDay([FromRoute] string day, [FromQuery] bool fresh = false)
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(day) || !Regex.IsMatch(day, "^20[0-9]{2}-[01][0-9]-[0-3][0-9]$"))
            {
                return NotFound();
            }

            var path = Path.Combine(folder, "reports", day + ".json");
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var maxAge = fresh ? TimeSpan.Zero : string.Equals(day, today, StringComparison.Ordinal) ? TimeSpan.FromMinutes(10) : TimeSpan.MaxValue;
            await RefreshReport(path, day, maxAge).ConfigureAwait(false);
            return System.IO.File.Exists(path) ? PhysicalFile(path, MediaTypeNames.Application.Json) : NotFound();
        }

        /// <summary>
        /// Rebuilds a report file (daily/server_report.py, a few seconds) when it is missing or older than [maxAge];
        /// one build per day at a time.
        /// </summary>
        private async Task RefreshReport(string path, string? day, TimeSpan maxAge)
        {
            var exists = System.IO.File.Exists(path);
            if (exists && (maxAge == TimeSpan.MaxValue || DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(path) < maxAge))
            {
                return;
            }

            var key = day ?? "recent";
            var args = day == null ? new[] { "daily/server_report.py" } : new[] { "daily/server_report.py", "--day", day };
            var run = ReportBuilds.GetOrAdd(key, k => Task.Run(async () =>
            {
                try
                {
                    return await RunM2Script(args, TimeSpan.FromSeconds(90), "Report " + k).ConfigureAwait(false);
                }
                finally
                {
                    ReportBuilds.TryRemove(k, out _);
                }
            }));
            await run.ConfigureAwait(false);
        }
    }
}
