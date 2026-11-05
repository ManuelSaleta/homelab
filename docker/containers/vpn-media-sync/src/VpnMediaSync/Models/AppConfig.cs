using System.Text.Json.Serialization;

namespace VpnMediaSync.Models;

public sealed class AppConfig
{
    /// <summary>
    /// Configuration settings for run intervals, cron scheduling, file locking, and sync state persistence.
    /// </summary>
    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>
    /// Configuration settings for VPN provider tunnel requirements and fail-safe disconnect behavior.
    /// </summary>
    public VpnConfig Vpn { get; set; } = new();

    /// <summary>
    /// Configuration settings for external trending movie feeds and custom query lists.
    /// </summary>
    public TrendingConfig Trending { get; set; } = new();

    /// <summary>
    /// Configuration settings for the torrent indexer client (e.g. Prowlarr) and grab limits.
    /// </summary>
    public IndexerConfig Indexer { get; set; } = new();

    /// <summary>
    /// Release filtering rules and thresholds for movies and television categories.
    /// </summary>
    public FiltersConfig Filters { get; set; } = new();

    /// <summary>
    /// Configuration settings for the download client (e.g. qBittorrent), staging paths, and seeding rules.
    /// </summary>
    public DownloaderConfig Downloader { get; set; } = new();

    /// <summary>
    /// Configuration settings for remote SSH rsync synchronization to the NAS media storage.
    /// </summary>
    public RsyncConfig Rsync { get; set; } = new();
}

public class ScheduleConfig
{
    public const string SectionName = "Schedule";

    /// <summary>
    /// Periodic execution interval in minutes for the background sync daemon loop.
    /// </summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Standard 5-field cron schedule expression for orchestrating periodic synchronization runs.
    /// The default value <c>"*/30 * * * *"</c> runs every 30 minutes (at minute :00 and :30 of every hour, every day).
    /// </summary>
    public string CronExpression { get; set; } = "*/30 * * * *";

    /// <summary>
    /// File system path to the exclusive lock file preventing concurrent orchestrator executions.
    /// </summary>
    public string LockFile { get; set; } = "/tmp/vpn-media-sync.lock";

    /// <summary>
    /// Port listened on by <c>SyncSchedulerWorker</c> when running as a hosted service in daemon mode for on-demand sync triggers and health probes (default: 5050).
    /// </summary>
    public int WebhookPort { get; set; } = 5050;

    /// <summary>
    /// Optional shared secret token required in X-Webhook-Token or Bearer Authorization header to trigger sync runs.
    /// </summary>
    public string? WebhookSecretToken { get; set; }

    /// <summary>
    /// Local file system path to the persistent JSON state database storing acquired release history.
    /// </summary>
    public string StateFilePath { get; set; } = "/state/state.json";
}

public sealed class VpnConfig
{
    /// <summary>
    /// VPN implementation provider or management method (e.g., 'nordlynx', 'nordvpn-host', or 'wireguard').
    /// </summary>
    public string Provider { get; set; } = "nordlynx";

    /// <summary>
    /// The default virtual network interface created by the WireGuard VPN protocol.
    /// </summary>
    public string RequiredInterface { get; set; } = "wg0";

    /// <summary>
    /// Flag indicating whether active torrents should be paused if the VPN tunnel is detected down.
    /// </summary>
    public bool PauseTorrentsWhenDisconnected { get; set; } = true;
}

public sealed class TrendingConfig
{
    /// <summary>
    /// Flag determining whether trending movie feeds are queried during synchronization cycles.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// HTTP endpoint delivering trending popular movie title feeds in JSON format.
    /// </summary>
    public string PopularMoviesUrl { get; set; } = "https://popular-movies-data.stevenlu.com/movies.json";

    /// <summary>
    /// Optional list of user-defined search queries to query indexers for alongside trending feeds.
    /// </summary>
    public List<string>? CustomQueries { get; set; } = null;
}

public sealed class IndexerConfig
{
    /// <summary>
    /// Indexer client service type (default: 'prowlarr').
    /// </summary>
    public string Type { get; set; } = "prowlarr";

    /// <summary>
    /// Base HTTP URL for the indexer service API endpoint.
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:9696";

    /// <summary>
    /// Authentication API token for communicating with the indexer service.
    /// </summary>
    public string ApiKey { get; set; } = "UNCONFIGURED_PROWLARR_API_KEY";

    /// <summary>
    /// Minimum required seeder count for a release candidate to be considered viable.
    /// </summary>
    public int MinSeeders { get; set; } = 50;

    /// <summary>
    /// Maximum number of candidate releases to grab and dispatch per orchestrator execution cycle.
    /// </summary>
    public int MaxGrabPerRun { get; set; } = 3;
}

public sealed class FiltersConfig
{
    /// <summary>
    /// Category filtering parameters and size thresholds applied to movie torrent candidates.
    /// </summary>
    public CategoryFilterConfig Movies { get; set; } = new();

    /// <summary>
    /// Category filtering parameters and size thresholds applied to television torrent candidates.
    /// </summary>
    public CategoryFilterConfig Tv { get; set; } = new();
}

public sealed class CategoryFilterConfig
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
}

public sealed class DownloaderConfig
{
    /// <summary>
    /// Torrent downloader client type (default: 'qbittorrent').
    /// </summary>
    public string Client { get; set; } = "qbittorrent";

    /// <summary>
    /// Base HTTP URL for the download client WebUI API.
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8080";

    /// <summary>
    /// WebUI authentication username for the download client.
    /// </summary>
    public string Username { get; set; } = "admin";

    /// <summary>
    /// WebUI authentication password for the download client.
    /// </summary>
    public string Password { get; set; } = "UNCONFIGURED_QBIT_PASSWORD";

    /// <summary>
    /// Download staging directory path where completed torrent payloads are stored.
    /// </summary>
    public string StagingDir { get; set; } = "/downloads";

    /// <summary>
    /// Staging directory path prefix as reported by the download client for path mapping.
    /// </summary>
    public string RemotePathPrefix { get; set; } = "/downloads";

    /// <summary>
    /// Upload ratio threshold at which seeding is considered complete.
    /// </summary>
    public double RatioLimit { get; set; } = 1.0;

    /// <summary>
    /// Maximum seeding duration in minutes before torrents are considered eligible for cleanup/sync.
    /// </summary>
    public int SeedingTimeLimitMinutes { get; set; } = 1440;
}

public sealed class RsyncConfig
{
    /// <summary>
    /// Flag determining whether rsync synchronization to the remote target host is performed.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Flag determining whether ffprobe container format validation is executed before transfer.
    /// </summary>
    public bool VerifyWithFfprobe { get; set; } = true;

    /// <summary>
    /// Remote SSH username used for rrsync authentication on the target host.
    /// </summary>
    public string SshUser { get; set; } = "gman";

    /// <summary>
    /// Remote host IP address or hostname running the restricted rsync destination.
    /// </summary>
    public string SshHost { get; set; } = "192.168.1.250";

    /// <summary>
    /// Local file system path to the private SSH key used for rrsync authentication.
    /// </summary>
    public string SshKey { get; set; } = "/home/appuser/.ssh/id_ed25519_mediasync";

    /// <summary>
    /// Remote destination directory path for synchronized movie media files.
    /// </summary>
    public string DestMoviesDir { get; set; } = "/mnt/export/storage/plex/media/Movies";

    /// <summary>
    /// Remote destination directory path for synchronized television media files.
    /// </summary>
    public string DestTvDir { get; set; } = "/mnt/export/storage/plex/media/TV Shows";

    /// <summary>
    /// POSIX user and group ID ownership (e.g. '1000:1000') to apply to transferred files.
    /// </summary>
    public string Chown { get; set; } = "1000:1000";

    /// <summary>
    /// Flag indicating whether local staging files should be deleted after successful remote transfer.
    /// </summary>
    public bool RemoveLocalAfterSync { get; set; } = false;
}

public sealed record MediaHistoryEntry(
    string? ExternalId,
    string InfoHash,
    string Title,
    string Category,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SyncedAt
);

public sealed record SyncStateDocument(
    List<MediaHistoryEntry> Items
);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(SyncStateDocument))]
[JsonSerializable(typeof(List<ProwlarrRelease>))]
[JsonSerializable(typeof(List<TrendingMovieItem>))]
[JsonSerializable(typeof(List<QBitTorrentItem>))]
[JsonSerializable(typeof(List<QBitTorrentFileItem>))]
[JsonSerializable(typeof(QBitTorrentFileItem))]
internal partial class AppJsonSerializerContext : JsonSerializerContext;
