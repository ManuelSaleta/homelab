using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnMediaSync.Models;

namespace VpnMediaSync.Services;

public sealed class SyncOrchestrator(
    IOptions<AppConfig> options,
    VpnGuardService vpnGuard,
    JsonStateStore stateStore,
    TrendingFeedClient trendingClient,
    ProwlarrClient prowlarrClient,
    QBitClient qBitClient,
    RsyncTransferService rsyncService,
    ILogger<SyncOrchestrator> logger)
{
    private readonly AppConfig _config = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task RunCycleAsync(bool dryRun, CancellationToken ct)
    {
        await using var lockStream = TryAcquireLockFile(_config.Schedule.LockFile);
        if (lockStream is null)
        {
            logger.LogWarning("Another vpn-media-sync cycle is already holding lock '{LockFile}'; skipping.", _config.Schedule.LockFile);
            return;
        }

        await stateStore.InitializeAsync(ct);

        // Step 1: Verify VPN tunnel before any indexer or swarm activity
        var vpnConnected = await vpnGuard.IsVpnConnectedAsync(ct);
        if (!vpnConnected)
        {
            if (_config.Vpn.PauseTorrentsWhenDisconnected && !dryRun)
            {
                await qBitClient.PauseAllAsync(ct);
            }
            return;
        }

        // Step 2: Query trending candidates and search Prowlarr
        var candidates = await trendingClient.GetUnseenCandidatesAsync(ct);
        var grabbedCount = 0;

        foreach (var candidate in candidates)
        {
            if (grabbedCount >= _config.Indexer.MaxGrabPerRun)
            {
                break;
            }

            var filter = string.Equals(candidate.Category, "tv", StringComparison.OrdinalIgnoreCase)
                ? _config.Filters.Tv
                : _config.Filters.Movies;

            var bestRelease = await prowlarrClient.SearchBestReleaseAsync(candidate.SearchQuery, filter, ct);
            if (bestRelease is null)
            {
                continue;
            }

            var infoHash = !string.IsNullOrWhiteSpace(bestRelease.InfoHash)
                ? bestRelease.InfoHash
                : bestRelease.Guid;

            if (stateStore.IsHashProcessed(infoHash))
            {
                continue;
            }

            var downloadUri = bestRelease.GetBestDownloadUri();
            if (string.IsNullOrWhiteSpace(downloadUri))
            {
                continue;
            }

            if (dryRun)
            {
                logger.LogInformation("[DRY-RUN] Would dispatch '{Title}' to qBittorrent.", bestRelease.Title);
                grabbedCount++;
                continue;
            }

            if (await qBitClient.AddTorrentQuarantinedAsync(downloadUri, candidate.Category, ct))
            {
                await stateStore.RecordGrabbedAsync(
                    candidate.ExternalId,
                    infoHash,
                    bestRelease.Title,
                    candidate.Category,
                    ct);
                grabbedCount++;

                // Attempt immediate pre-download security audit if metadata is already populated
                await qBitClient.AuditAndQuarantineTorrentAsync(infoHash, ct);
            }
        }

        // Step 2.5: Audit all in-flight quarantined torrents whose metadata resolved
        await qBitClient.AuditPendingQuarantinedTorrentsAsync(ct);

        // Step 3: Sweep completed torrents, rsync verified media to micro-nas, and manage cleanup
        var completed = await qBitClient.GetCompletedTorrentsAsync(ct);
        foreach (var torrent in completed)
        {
            var isSynced = stateStore.IsHashSynced(torrent.Hash);
            var category = string.IsNullOrWhiteSpace(torrent.Category) ? "movies" : torrent.Category;

            if (!isSynced)
            {
                if (await rsyncService.TransferAsync(torrent, dryRun, ct))
                {
                    if (!dryRun)
                    {
                        await stateStore.MarkSyncedAsync(torrent.Hash, torrent.Name, category, ct);
                    }
                    isSynced = true;
                }
            }

            if (!isSynced)
            {
                continue;
            }

            // Evaluate cleanup criteria for synced torrents
            var seedingTimeMinutes = torrent.SeedingTimeSeconds / 60.0;
            var ratioSatisfied = _config.Downloader.RatioLimit > 0 && torrent.Ratio >= _config.Downloader.RatioLimit;
            var seedingTimeSatisfied = _config.Downloader.SeedingTimeLimitMinutes > 0 && seedingTimeMinutes >= _config.Downloader.SeedingTimeLimitMinutes;
            var shouldCleanup = _config.Rsync.RemoveLocalAfterSync || ratioSatisfied || seedingTimeSatisfied;

            if (shouldCleanup)
            {
                if (dryRun)
                {
                    logger.LogInformation(
                        "[DRY-RUN] Would clean up synced torrent '{Name}' and local staging files (ratio: {Ratio:F2}, seeded: {Minutes:F0}m).",
                        torrent.Name,
                        torrent.Ratio,
                        seedingTimeMinutes);
                }
                else
                {
                    await qBitClient.DeleteTorrentAsync(torrent.Hash, deleteFiles: true, ct);
                    rsyncService.CleanupLocalStaging(torrent);
                    logger.LogInformation(
                        "Cleaned up synced torrent '{Name}' and local staging files (ratio: {Ratio:F2}, seeded: {Minutes:F0}m).",
                        torrent.Name,
                        torrent.Ratio,
                        seedingTimeMinutes);
                }
            }
        }
    }

    private static FileStream? TryAcquireLockFile(string lockPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
