using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnMediaSync.Models;
using VpnMediaSync.Models.Options;

namespace VpnMediaSync.Services;

public sealed class QBitClient(
    HttpClient httpClient,
    IOptions<DownloaderConfigOpts> downloaderOptions,
    JsonSerializerOptions jsonOptions,
    ILogger<QBitClient> logger)
{
    private readonly DownloaderConfigOpts _config = downloaderOptions?.Value ?? throw new ArgumentNullException(nameof(downloaderOptions));
    private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    private bool _authenticated;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.Password) &&
        !_config.Password.StartsWith("UNCONFIGURED", StringComparison.OrdinalIgnoreCase);

    private string BaseUrl => _config.BaseUrl.TrimEnd('/');

    public async Task<bool> EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            logger.LogWarning("qBittorrent password is unconfigured; skipping qBittorrent API calls.");
            return false;
        }

        if (_authenticated)
        {
            return true;
        }

        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("username", _config.Username),
            new KeyValuePair<string, string>("password", _config.Password)
        ]);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v2/auth/login")
        {
            Content = form
        };
        req.Headers.Referrer = new Uri(BaseUrl);

        using var resp = await httpClient.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.IsSuccessStatusCode &&
            (resp.StatusCode == HttpStatusCode.NoContent ||
             body.Contains("Ok", StringComparison.OrdinalIgnoreCase) ||
             string.IsNullOrWhiteSpace(body)))
        {
            _authenticated = true;
            return true;
        }

        logger.LogError("qBittorrent authentication failed with status {Status}: {Body}", resp.StatusCode, body);
        return false;
    }

    public static readonly string[] BlacklistedDangerousExtensions =
    [
        // Executables & script droppers
        ".exe", ".scr", ".bat", ".cmd", ".ps1", ".vbs", ".sh", ".jar",
        ".iso", ".dmg", ".lnk", ".pif", ".cpl", ".msi", ".dll", ".com",
        ".hta", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msc", ".gadget",
        ".bin", ".app", ".apk", ".deb", ".rpm",
        // Compressed archives (prevent decompression bombs and nested trojans)
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".zst",
        ".cab", ".arj", ".lzh", ".ace", ".uue", ".bz", ".tgz"
    ];

    public static readonly string[] AllowedMediaExtensions =
    [
        ".mkv", ".mp4", ".mov", ".avi", ".webm"
    ];

    public static readonly string[] AllowedSubtitleExtensions =
    [
        ".srt"
    ];

    public async Task<bool> AddTorrentAsync(string downloadUri, string category, CancellationToken ct)
    {
        // Enforce Quarantine Pattern: Always add torrents in paused state
        return await AddTorrentQuarantinedAsync(downloadUri, category, ct);
    }

    public async Task<bool> AddTorrentQuarantinedAsync(string downloadUri, string category, CancellationToken ct)
    {
        if (!await EnsureAuthenticatedAsync(ct))
        {
            return false;
        }

        var clientBaseDir = !string.IsNullOrWhiteSpace(_config.RemotePathPrefix)
            ? _config.RemotePathPrefix
            : _config.StagingDir;
        var savePath = Path.Combine(clientBaseDir, category);
        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("urls", downloadUri),
            new KeyValuePair<string, string>("category", category),
            new KeyValuePair<string, string>("savepath", savePath),
            new KeyValuePair<string, string>("paused", "true"),
            new KeyValuePair<string, string>("ratioLimit", _config.RatioLimit.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("seedingTimeLimit", _config.SeedingTimeLimitMinutes.ToString(CultureInfo.InvariantCulture))
        ]);

        using var resp = await httpClient.PostAsync($"{BaseUrl}/api/v2/torrents/add", form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Failed to add quarantined torrent to qBittorrent (HTTP {Status}).", resp.StatusCode);
            return false;
        }

        logger.LogInformation("Added torrent to qBittorrent in QUARANTINED (paused) state for pre-download security audit.");
        return true;
    }

    public async Task<IReadOnlyList<QBitTorrentFileItem>> GetTorrentFilesAsync(string hash, CancellationToken ct)
    {
        if (!await EnsureAuthenticatedAsync(ct))
        {
            return [];
        }

        try
        {
            var files = await httpClient.GetFromJsonAsync<List<QBitTorrentFileItem>>(
                $"{BaseUrl}/api/v2/torrents/files?hash={hash}",
                _jsonOptions,
                ct);

            return files ?? [];
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Could not fetch file list for torrent '{Hash}'.", hash);
            return [];
        }
    }

    public async Task<bool> SetFilePrioritiesAsync(string hash, IEnumerable<int> fileIndices, int priority, CancellationToken ct)
    {
        if (!await EnsureAuthenticatedAsync(ct))
        {
            return false;
        }

        var idList = string.Join("|", fileIndices);
        if (string.IsNullOrWhiteSpace(idList))
        {
            return true;
        }

        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("hash", hash),
            new KeyValuePair<string, string>("id", idList),
            new KeyValuePair<string, string>("priority", priority.ToString(CultureInfo.InvariantCulture))
        ]);

        using var resp = await httpClient.PostAsync($"{BaseUrl}/api/v2/torrents/filePrio", form, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> ResumeTorrentAsync(string hash, CancellationToken ct)
    {
        if (!await EnsureAuthenticatedAsync(ct))
        {
            return false;
        }

        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("hashes", hash)
        ]);

        // qBittorrent v5.0+ uses /api/v2/torrents/start; v4.x uses /api/v2/torrents/resume
        using var startResp = await httpClient.PostAsync($"{BaseUrl}/api/v2/torrents/start", form, ct);
        if (startResp.StatusCode == HttpStatusCode.NotFound)
        {
            using var resumeForm = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("hashes", hash)
            ]);
            using var resumeResp = await httpClient.PostAsync($"{BaseUrl}/api/v2/torrents/resume", resumeForm, ct);
            return resumeResp.IsSuccessStatusCode;
        }

        return startResp.IsSuccessStatusCode;
    }

    public async Task<bool> AuditAndQuarantineTorrentAsync(string hash, CancellationToken ct)
    {
        var files = await GetTorrentFilesAsync(hash, ct);
        if (files.Count == 0)
        {
            logger.LogDebug("Torrent '{Hash}' metadata not yet resolved by swarm; keeping in quarantine.", hash);
            return false;
        }

        var nonMediaIndices = new List<int>();
        var hasValidMedia = false;

        foreach (var file in files)
        {
            var normalizedName = file.Name.Replace('\\', '/');
            var ext = Path.GetExtension(normalizedName);

            // Security Check 1: Reject path traversal tokens
            if (normalizedName.Contains("../", StringComparison.Ordinal) ||
                normalizedName.StartsWith('/') ||
                normalizedName.Contains("/../", StringComparison.Ordinal))
            {
                logger.LogError("CRITICAL SECURITY VIOLATION: Torrent '{Hash}' contains path traversal token in '{Name}'. Aborting and purging from swarm.", hash, file.Name);
                await DeleteTorrentAsync(hash, deleteFiles: true, ct);
                return false;
            }

            // Security Check 2: Reject blacklisted dangerous file types
            if (BlacklistedDangerousExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                logger.LogError("CRITICAL SECURITY VIOLATION: Torrent '{Hash}' contains blacklisted dangerous file '{Name}'. Aborting and purging from swarm.", hash, file.Name);
                await DeleteTorrentAsync(hash, deleteFiles: true, ct);
                return false;
            }

            // Security Check 3: Check if valid media file
            if (AllowedMediaExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                hasValidMedia = true;
            }
            else if (AllowedSubtitleExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                // Subtitles permitted if within reasonable size ceiling (<= 2MB)
                if (file.Size > 2 * 1024 * 1024)
                {
                    logger.LogWarning("Deselecting oversize subtitle file '{Name}' ({Size} bytes) in torrent '{Hash}'.", file.Name, file.Size, hash);
                    nonMediaIndices.Add(file.Index);
                }
            }
            else
            {
                // DEFAULT DENY: Any non-media file type (nfo, txt, url, samples, unknown) is excluded
                nonMediaIndices.Add(file.Index);
            }
        }

        // Security Check 4: Must contain at least one valid video file
        if (!hasValidMedia)
        {
            logger.LogWarning("SECURITY REJECTION: Torrent '{Hash}' contains no recognized video files. Deleting from client.", hash);
            await DeleteTorrentAsync(hash, deleteFiles: true, ct);
            return false;
        }

        // Exclude all non-media files by setting priority to 0 (Do Not Download)
        if (nonMediaIndices.Count > 0)
        {
            logger.LogInformation(
                "Zero-Trust Policy: Deselecting {Count} non-media files (priority=0) for torrent '{Hash}'.",
                nonMediaIndices.Count,
                hash);
            await SetFilePrioritiesAsync(hash, nonMediaIndices, priority: 0, ct);
        }

        // Torrent passed security audit: safe to resume
        await ResumeTorrentAsync(hash, ct);
        logger.LogInformation("Torrent '{Hash}' PASSED pre-download security audit and was unpaused for media acquisition.", hash);
        return true;
    }

    public async Task AuditPendingQuarantinedTorrentsAsync(CancellationToken ct)
    {
        if (!await EnsureAuthenticatedAsync(ct))
        {
            return;
        }

        try
        {
            // Inspect torrents currently in paused/stopped state
            var pausedTorrents = await httpClient.GetFromJsonAsync<List<QBitTorrentItem>>(
                $"{BaseUrl}/api/v2/torrents/info?filter=paused",
                _jsonOptions,
                ct) ?? [];

            foreach (var torrent in pausedTorrents)
            {
                // If torrent is not 100% complete, it is an in-flight quarantined download
                if (torrent.Progress < 1.0)
                {
                    await AuditAndQuarantineTorrentAsync(torrent.Hash, ct);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to inspect paused torrents for security audit.");
        }
    }

    public async Task<IReadOnlyList<QBitTorrentItem>> GetCompletedTorrentsAsync(CancellationToken ct)
    {
        if (!await EnsureAuthenticatedAsync(ct))
        {
            return [];
        }

        var items = await httpClient.GetFromJsonAsync<List<QBitTorrentItem>>(
            $"{BaseUrl}/api/v2/torrents/info?filter=completed",
            _jsonOptions,
            ct);

        return items ?? [];
    }

    public async Task<bool> DeleteTorrentAsync(string hash, bool deleteFiles, CancellationToken ct)
    {
        if (!await EnsureAuthenticatedAsync(ct))
        {
            return false;
        }

        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("hashes", hash),
            new KeyValuePair<string, string>("deleteFiles", deleteFiles ? "true" : "false")
        ]);

        using var resp = await httpClient.PostAsync($"{BaseUrl}/api/v2/torrents/delete", form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogError("Failed to delete torrent '{Hash}' from qBittorrent (HTTP {Status}).", hash, resp.StatusCode);
            return false;
        }

        return true;
    }

    public async Task PauseAllAsync(CancellationToken ct)
    {
        if (!await EnsureAuthenticatedAsync(ct))
        {
            return;
        }

        using var stopForm = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("hashes", "all")
        ]);

        // qBittorrent v5.0+ uses /api/v2/torrents/stop; v4.x uses /api/v2/torrents/pause
        using var stopResp = await httpClient.PostAsync($"{BaseUrl}/api/v2/torrents/stop", stopForm, ct);
        if (stopResp.StatusCode == HttpStatusCode.NotFound)
        {
            using var pauseForm = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("hashes", "all")
            ]);
            using var pauseResp = await httpClient.PostAsync($"{BaseUrl}/api/v2/torrents/pause", pauseForm, ct);
            if (pauseResp.IsSuccessStatusCode)
            {
                logger.LogWarning("Paused all active qBittorrent transfers due to VPN disconnection.");
            }
            return;
        }

        if (stopResp.IsSuccessStatusCode)
        {
            logger.LogWarning("Stopped all active qBittorrent transfers due to VPN disconnection.");
        }
    }
}
