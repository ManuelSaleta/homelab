namespace VpnMediaSync.Models.Options;

public sealed class DownloaderConfigOpts
{
    public const string SectionName = "Downloader";

    /// <summary>
    /// Torrent downloader client type (default: 'qbittorrent').
    /// </summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>
    /// Base HTTP URL for the download client WebUI API.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// WebUI authentication username for the download client.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// WebUI authentication password for the download client.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Download staging directory path where completed torrent payloads are stored.
    /// </summary>
    public string StagingDir { get; set; } = string.Empty;

    /// <summary>
    /// Staging directory path prefix as reported by the download client for path mapping.
    /// </summary>
    public string RemotePathPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Upload ratio threshold at which seeding is considered complete.
    /// </summary>
    public double RatioLimit { get; set; }

    /// <summary>
    /// Maximum seeding duration in minutes before torrents are considered eligible for cleanup/sync.
    /// </summary>
    public int SeedingTimeLimitMinutes { get; set; }
}
