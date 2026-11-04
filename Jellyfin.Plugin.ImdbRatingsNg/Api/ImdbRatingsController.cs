using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Mime;
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
                    ImdbId = NumericImdbId(item.GetProviderId(MetadataProvider.Imdb)),
                };

                if (request.ImdbId == null && item is Episode episode)
                {
                    var series = episode.Series ?? (episode.SeriesId != Guid.Empty ? _libraryManager.GetItemById(episode.SeriesId) as Series : null);
                    request.SeriesImdbId = NumericImdbId(series?.GetProviderId(MetadataProvider.Imdb));
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
