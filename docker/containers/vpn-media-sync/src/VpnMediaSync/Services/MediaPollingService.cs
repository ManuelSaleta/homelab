using Microsoft.Extensions.Logging;
using Plex.Core.Http;
using Plex.Core.Http.Common;
using Plex.Core.Http.Models;

namespace VpnMediaSync.Services;

public interface IMediaPollingService
{
    Task<bool> ForceScanLibraryAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<PlexMetadataItem>?> GetCollectionAsync(string id, CancellationToken ct = default);
}

public sealed class MediaPollingService(IPlexClient client, ILogger<MediaPollingService> logger) : IMediaPollingService
{
    private readonly IPlexClient _client = client;
    private readonly ILogger<MediaPollingService> _logger = logger;

    public async Task<bool> ForceScanLibraryAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var sectionId = await ResolveSectionIdAsync(id, ct);
            await _client.RefreshSectionAsync(sectionId, ct);
            _logger.LogInformation("Triggered library refresh for section {SectionId} ('{Id}')", sectionId, id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger library refresh for section '{Id}'", id);
            return false;
        }
    }

    public async Task<IReadOnlyList<PlexMetadataItem>?> GetCollectionAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var sectionId = await ResolveSectionIdAsync(id, ct);
            var items = await _client.GetSectionItemsAsync(sectionId, null, ct);
            if (items is null || items.Count == 0)
            {
                _logger.LogDebug("No metadata items found in section {SectionId} ('{Id}')", sectionId, id);
            }
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching media items for section '{Id}'", id);
            return null;
        }
    }

    private async Task<int> ResolveSectionIdAsync(string id, CancellationToken ct)
    {
        if (int.TryParse(id, out var parsedId))
        {
            return parsedId;
        }

        try
        {
            var sections = await _client.GetSectionsAsync(ct);
            var match = sections?.FirstOrDefault(s =>
                string.Equals(s.Title, id, StringComparison.OrdinalIgnoreCase) ||
                s.Title.Contains(id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Type, id, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve section id dynamically for '{Id}', falling back to section 1", id);
        }

        return 1;
    }
}
