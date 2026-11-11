using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnMediaSync.Models;
using VpnMediaSync.Models.Options;

namespace VpnMediaSync.Services;

public sealed class JsonStateStore(
    IOptions<SchedulingConfigOpts> scheduleOptions,
    JsonSerializerOptions jsonOptions,
    ILogger<JsonStateStore> logger)
{
    private readonly SchedulingConfigOpts _scheduleConfig = scheduleOptions?.Value ?? throw new ArgumentNullException(nameof(scheduleOptions));
    private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));

    private const int InitialRequestCount = 1;
    private const int MaxRequestCount = 1;
    private readonly SemaphoreSlim _fileLock = new(InitialRequestCount, MaxRequestCount);
    private readonly Lock _stateLock = new();
    private SyncStateDocument _state = new([]);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var path = _scheduleConfig.StateFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            SyncStateDocument loaded;
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                loaded = await JsonSerializer.DeserializeAsync<SyncStateDocument>(
                    stream,
                    _jsonOptions,
                    ct) ?? new([]);
            }
            else
            {
                loaded = new([]);
                await SaveInternalAsync(loaded, ct);
            }

            lock (_stateLock)
            {
                _state = loaded;
            }

            logger.LogDebug(
                "Initialized JSON state store at {StateFilePath} ({Count} entries).",
                path,
                loaded.Items.Count);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public bool IsExternalIdProcessed(string externalId)
    {
        lock (_stateLock)
        {
            return _state.Items.Any(i =>
                string.Equals(i.ExternalId, externalId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool IsHashProcessed(string infoHash)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
        {
            return false;
        }

        lock (_stateLock)
        {
            return _state.Items.Any(i =>
                string.Equals(i.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool IsHashSynced(string infoHash)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
        {
            return false;
        }

        lock (_stateLock)
        {
            return _state.Items.Any(i =>
                string.Equals(i.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.Status, "synced", StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task RecordGrabbedAsync(
        string? externalId,
        string? infoHash,
        string title,
        string category,
        CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            lock (_stateLock)
            {
                if (!string.IsNullOrWhiteSpace(infoHash) &&
                    _state.Items.Any(i => string.Equals(i.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(infoHash) &&
                    !string.IsNullOrWhiteSpace(externalId) &&
                    _state.Items.Any(i => string.Equals(i.ExternalId, externalId, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                _state.Items.Add(new MediaHistoryEntry(
                    ExternalId: externalId,
                    InfoHash: infoHash?.ToLowerInvariant() ?? string.Empty,
                    Title: title,
                    Category: category,
                    Status: "grabbed",
                    CreatedAt: DateTimeOffset.UtcNow,
                    SyncedAt: null));
            }

            await SaveInternalAsync(_state, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task MarkSyncedAsync(
        string infoHash,
        string title,
        string category,
        CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            lock (_stateLock)
            {
                var idx = _state.Items.FindIndex(i =>
                    string.Equals(i.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));

                if (idx >= 0)
                {
                    var existing = _state.Items[idx];
                    _state.Items[idx] = existing with
                    {
                        Status = "synced",
                        SyncedAt = now
                    };
                }
                else
                {
                    _state.Items.Add(new MediaHistoryEntry(
                        ExternalId: null,
                        InfoHash: infoHash.ToLowerInvariant(),
                        Title: title,
                        Category: category,
                        Status: "synced",
                        CreatedAt: now,
                        SyncedAt: now));
                }
            }

            await SaveInternalAsync(_state, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task SaveInternalAsync(SyncStateDocument state, CancellationToken ct)
    {
        var path = _scheduleConfig.StateFilePath;
        var tempPath = $"{path}.tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                state,
                _jsonOptions,
                ct);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
