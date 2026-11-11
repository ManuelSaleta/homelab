using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnMediaSync.Models;
using VpnMediaSync.Models.Options;

namespace VpnMediaSync.Services;

public sealed class SyncOrchestrator(
    IOptions<SchedulingConfigOpts> scheduleOptions,
    IOptions<VpnConfigOpts> vpnOptions,
    IOptions<IndexerConfigOpts> indexerOptions,
    IOptions<FiltersConfigOpts> filtersOptions,
    IOptions<DownloaderConfigOpts> downloaderOptions,
    IOptions<RsyncConfigOpts> rsyncOptions,
    VpnGuardService vpnGuard,
    JsonStateStore stateStore,
    TrendingFeedClient trendingClient,
    ProwlarrClient prowlarrClient,
    QBitClient qBitClient,
    MediaTransferService rsyncService,
    IMediaPollingService plexPollingService,
    ILogger<SyncOrchestrator> logger)
{
    private readonly SchedulingConfigOpts _scheduleConfig = scheduleOptions?.Value ?? throw new ArgumentNullException(nameof(scheduleOptions));
    private readonly VpnConfigOpts _vpnConfig = vpnOptions?.Value ?? throw new ArgumentNullException(nameof(vpnOptions));
    private readonly IndexerConfigOpts _indexerConfig = indexerOptions?.Value ?? throw new ArgumentNullException(nameof(indexerOptions));
    private readonly FiltersConfigOpts _filtersConfig = filtersOptions?.Value ?? throw new ArgumentNullException(nameof(filtersOptions));
    private readonly DownloaderConfigOpts _downloaderConfig = downloaderOptions?.Value ?? throw new ArgumentNullException(nameof(downloaderOptions));
    private readonly RsyncConfigOpts _rsyncConfig = rsyncOptions?.Value ?? throw new ArgumentNullException(nameof(rsyncOptions));

    public async Task RunCycleAsync(CancellationToken ct)
    {
        await using var lockStream = TryAcquireLockFile(_scheduleConfig.LockFile);
        if (lockStream is null)
        {
            logger.LogWarning("Another vpn-media-sync cycle is already holding lock '{LockFile}'; skipping.", _scheduleConfig.LockFile);
            return;
        }

        await stateStore.InitializeAsync(ct);

        // Step 1: Verify VPN tunnel before any indexer or swarm activity
        var vpnConnected = await vpnGuard.IsVpnConnectedAsync(ct);
        if (!vpnConnected)
        {
            if (_vpnConfig.PauseTorrentsWhenDisconnected)
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
            if (grabbedCount >= _indexerConfig.MaxGrabPerRun)
            {
                break;
            }

            // Early check: Skip if candidate title already exists in Plex library
            var c = new MediaCandidate
            {
                Id = candidate.ExternalId,
                Title = candidate.SearchQuery,
                SearchQuery = candidate.SearchQuery,
                Category = candidate.Category,
            };

            if (await IsMediaAlreadyPresent(c, ct))
            {
                continue;
            }

            var filter = string.Equals(candidate.Category, "tv", StringComparison.OrdinalIgnoreCase)
                ? _filtersConfig.Tv
                : _filtersConfig.Movies;

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

            c.Title = bestRelease.Title;
            c.InfoHash = infoHash;
            // Target release check: Verify matched release does not already exist in Plex before quarantining
            if (await IsMediaAlreadyPresent(c, ct))
            {
                continue;
            }

            var downloadUri = bestRelease.GetBestDownloadUri();
            if (string.IsNullOrWhiteSpace(downloadUri))
            {
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
                if (await rsyncService.TransferAsync(torrent, ct))
                {
                    await stateStore.MarkSyncedAsync(torrent.Hash, torrent.Name, category, ct);
                    isSynced = true;

                    // Trigger Plex library scan for the updated category
                    _ = plexPollingService.ForceScanLibraryAsync(category, ct);
                }
            }

            if (!isSynced)
            {
                continue;
            }

            // Evaluate cleanup criteria for synced torrents
            var seedingTimeMinutes = torrent.SeedingTimeSeconds / 60.0;
            var ratioSatisfied = _downloaderConfig.RatioLimit > 0 && torrent.Ratio >= _downloaderConfig.RatioLimit;
            var seedingTimeSatisfied = _downloaderConfig.SeedingTimeLimitMinutes > 0 && seedingTimeMinutes >= _downloaderConfig.SeedingTimeLimitMinutes;
            var shouldCleanup = _rsyncConfig.RemoveLocalAfterSync || ratioSatisfied || seedingTimeSatisfied;

            if (shouldCleanup)
            {
                await qBitClient.DeleteTorrentAsync(torrent.Hash, deleteFiles: true, ct);
                await rsyncService.CleanupLocalStagingAsync(torrent, ct);
                logger.LogInformation(
                    "Cleaned up synced torrent '{Name}' and local staging files (ratio: {Ratio:F2}, seeded: {Minutes:F0}m).",
                    torrent.Name,
                    torrent.Ratio,
                    seedingTimeMinutes);
            }
        }
    }

    public class MediaCandidate
    {
        public string? Id { get; set; } = null;
        public string? InfoHash { get; set; } = null;
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string SearchQuery { get; set; } = string.Empty;

    }
    public async Task<bool> IsMediaAlreadyPresent(MediaCandidate candidate, CancellationToken ct)
    {
        // Target release check: Verify matched release does not already exist in Plex before quarantining
        if (await plexPollingService.IsMediaAlreadyPresentAsync(candidate.Title, candidate.Category, ct: ct))
        {
            logger.LogInformation(
                "Skipping release '{ReleaseTitle}' for candidate '{Query}' - target media already exists in Plex server library.",
                candidate.Title,
                candidate.SearchQuery);

            await stateStore.RecordGrabbedAsync(
                candidate.Id,
                candidate.InfoHash,
                candidate.Title,
                candidate.Category,
                ct);

            return true;
        }

        return false;
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
