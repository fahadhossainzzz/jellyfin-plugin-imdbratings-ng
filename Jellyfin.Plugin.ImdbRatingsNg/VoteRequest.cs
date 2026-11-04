namespace Jellyfin.Plugin.ImdbRatingsNg
{
    /// <summary>
    /// A library item whose IMDb votes are wanted: either by its own IMDb id, or, for an episode
    /// without one, by its series' IMDb id plus season and episode number.
    /// </summary>
    public class VoteRequest
    {
        /// <summary>
        /// Gets or sets the key the answer is returned under (the Jellyfin item id).
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the numeric part of the item's IMDb id, if it has one.
        /// </summary>
        public int? ImdbId { get; set; }

        /// <summary>
        /// Gets or sets the numeric part of the series' IMDb id (episodes without their own id).
        /// </summary>
        public int? SeriesImdbId { get; set; }

        /// <summary>
        /// Gets or sets the season number (episodes without their own id).
        /// </summary>
        public int? Season { get; set; }

        /// <summary>
        /// Gets or sets the episode number (episodes without their own id).
        /// </summary>
        public int? Episode { get; set; }
    }
}
