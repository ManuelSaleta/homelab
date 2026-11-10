using System.Diagnostics;
using System.IO.Pipelines;
using Plex.Core.Http;
using Plex.Core.Http.Models;
namespace VpnMediaSync.Services;

public interface IMediaPollingService
{
    Task<bool> ForceScanLibraryAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<PlexMetadataItem>?> GetCollectionAsync(string id, CancellationToken ct = default);
}

public sealed class MediaPollingService(IPlexClient client, ILogger<IMediaPollingService> logger) : IMediaPollingService
{
    private readonly IPlexClient _client = client;
    private readonly ILogger<IMediaPollingService> _logger = logger;
    public async Task<bool> ForceScanLibraryAsync(string id, CancellationToken ct = default)
    {
        return await Task.FromResult(true);
    }

    public async Task<IReadOnlyList<PlexMetadataItem>?> GetCollectionAsync(string id, CancellationToken ct = default)
    {
        var items = await _client.GetCollectionItemsAsync(1, ct); // replace with actual id.
        if (items.Count is <= 0 && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Failed to fetch collection items using id: {id}", id);
        }
        return items ?? null;
    }
}
