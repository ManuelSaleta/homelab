namespace VpnMediaSync.Models.Options;

public sealed class TrendingConfigOpts
{
    public const string SectionName = "Trending";

    /// <summary>
    /// Flag determining whether trending movie and television feeds are queried during synchronization cycles.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// HTTP endpoint delivering trending popular movie title feeds in JSON format.
    /// </summary>
    public string PopularMoviesUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional list of additional HTTP endpoints delivering movie title feeds in JSON format.
    /// </summary>
    public List<string> MovieFeedUrls { get; set; } = [];

    /// <summary>
    /// Optional HTTP endpoint delivering trending popular television title feeds in JSON format.
    /// </summary>
    public string? PopularTvUrl { get; set; }

    /// <summary>
    /// Optional list of additional HTTP endpoints delivering television title feeds in JSON format.
    /// </summary>
    public List<string> TvFeedUrls { get; set; } = [];

    /// <summary>
    /// Optional list of user-defined search queries to query indexers for alongside trending feeds.
    /// </summary>
    public List<string> CustomQueries { get; set; } = [];

    /// <summary>
    /// Optional list of user-defined television search queries to query indexers for alongside trending feeds.
    /// </summary>
    public List<string> CustomTvQueries { get; set; } = [];
}
