namespace Jellyfin.Plugin.ImdbRatingsNg
{
    /// <summary>
    /// The IMDb rating of a title together with the number of people who voted.
    /// </summary>
    public class ImdbVotes
    {
        /// <summary>
        /// Gets or sets the average IMDb rating (1 to 10).
        /// </summary>
        public float Rating { get; set; }

        /// <summary>
        /// Gets or sets the number of IMDb users who rated the title.
        /// </summary>
        public int Votes { get; set; }
    }
}
