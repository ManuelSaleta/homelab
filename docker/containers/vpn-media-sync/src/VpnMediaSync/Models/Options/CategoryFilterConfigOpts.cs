namespace VpnMediaSync.Models.Options;

public sealed class CategoryFilterConfigOpts
{
    /// <summary>
    /// List of indexer category IDs matching this media classification.
    /// </summary>
    public List<int> CategoryIds { get; set; } = [];

    /// <summary>
    /// Minimum allowable payload size in megabytes for candidate torrent releases.
    /// </summary>
    public long MinSizeMb { get; set; }

    /// <summary>
    /// Maximum allowable payload size in megabytes for candidate torrent releases.
    /// </summary>
    public long MaxSizeMb { get; set; }

    /// <summary>
    /// List of regex patterns or tokens that candidate release titles must match.
    /// </summary>
    public List<string> AllowedPatterns { get; set; } = [];

    /// <summary>
    /// List of regex patterns or tokens that will disqualify candidate release titles if matched.
    /// </summary>
    public List<string> BlockedPatterns { get; set; } = [];

    /// <summary>
    /// Flag indicating whether multi-episode season packs or complete series releases should be prioritized over solo episodes.
    /// </summary>
    public bool PreferSeasonPacks { get; set; }
}
