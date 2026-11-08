namespace VpnMediaSync.Models.Options;

public sealed class IndexerConfigOpts
{
    public const string SectionName = "Indexer";

    /// <summary>
    /// Indexer client service type (e.g. 'prowlarr').
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Base HTTP URL for the indexer service API endpoint.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Authentication API token for communicating with the indexer service.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Minimum required seeder count for a release candidate to be considered viable.
    /// </summary>
    public int MinSeeders { get; set; }

    /// <summary>
    /// Maximum number of candidate releases to grab and dispatch per orchestrator execution cycle.
    /// </summary>
    public int MaxGrabPerRun { get; set; }
}
