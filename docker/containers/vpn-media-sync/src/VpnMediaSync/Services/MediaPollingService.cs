using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Plex.Core.Http;
using Plex.Core.Http.Common;
using Plex.Core.Http.Models;

namespace VpnMediaSync.Services;

public interface IMediaPollingService
{
    Task<bool> ForceScanLibraryAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<PlexMetadataItem>?> GetCollectionAsync(string id, CancellationToken ct = default);
    Task<bool> IsMediaAlreadyPresentAsync(string title, string category, int? year = null, CancellationToken ct = default);
    Task<PlexMetadataItem?> FindExistingMediaAsync(string title, string category, int? year = null, CancellationToken ct = default);
    Task<IReadOnlyList<PlexMetadataItem>> SearchLibraryAsync(string query, string category, CancellationToken ct = default);
}

public sealed class MediaPollingService(IPlexClient client, ILogger<MediaPollingService> logger) : IMediaPollingService
{
    private readonly IPlexClient _client = client;
    private readonly ILogger<MediaPollingService> _logger = logger;
    private readonly ConcurrentDictionary<int, (DateTimeOffset CachedAt, IReadOnlyList<PlexMetadataItem> Items)> _sectionCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<bool> ForceScanLibraryAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var sectionId = await ResolveSectionIdAsync(id, ct);
            _sectionCache.TryRemove(sectionId, out _);
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
            var now = DateTimeOffset.UtcNow;
            if (_sectionCache.TryGetValue(sectionId, out var cached) && now - cached.CachedAt < CacheTtl)
            {
                return cached.Items;
            }

            var items = await _client.GetSectionItemsAsync(sectionId, null, ct);
            var result = items ?? (IReadOnlyList<PlexMetadataItem>)Array.Empty<PlexMetadataItem>();
            _sectionCache[sectionId] = (now, result);

            if (result.Count == 0)
            {
                _logger.LogDebug("No metadata items found in section {SectionId} ('{Id}')", sectionId, id);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching media items for section '{Id}'", id);
            return null;
        }
    }

    public async Task<bool> IsMediaAlreadyPresentAsync(string title, string category, int? year = null, CancellationToken ct = default)
    {
        var match = await FindExistingMediaAsync(title, category, year, ct);
        return match is not null;
    }

    public async Task<PlexMetadataItem?> FindExistingMediaAsync(string title, string category, int? year = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        try
        {
            var sectionKey = MapCategoryToSectionKey(category);
            var existingItems = await GetCollectionAsync(sectionKey, ct);

            if (existingItems is { Count: > 0 })
            {
                foreach (var item in existingItems)
                {
                    if (MediaTitleMatcher.IsMatch(title, category, item, year))
                    {
                        _logger.LogInformation(
                            "Found existing match in Plex library: '{Target}' matched '{MatchedTitle}' ({MatchedYear}) [RatingKey: {Key}]",
                            title,
                            item.Title ?? item.OriginalTitle,
                            item.Year,
                            item.RatingKey);
                        return item;
                    }
                }
            }

            // Step 2: Query Plex Server's /hubs/search endpoint for server-indexed fuzzy search
            var (cleanTitle, extractedYear) = MediaTitleMatcher.ParseReleaseTitle(title);
            var effectiveYear = year ?? extractedYear;
            if (!string.IsNullOrWhiteSpace(cleanTitle))
            {
                var hubResults = await SearchHubsAsync(cleanTitle, category, ct);
                foreach (var item in hubResults)
                {
                    if (MediaTitleMatcher.IsMatch(title, category, item, effectiveYear))
                    {
                        _logger.LogInformation(
                            "Found existing match via Plex Hubs search: '{Target}' matched '{MatchedTitle}' ({MatchedYear}) [RatingKey: {Key}]",
                            title,
                            item.Title ?? item.OriginalTitle,
                            item.Year,
                            item.RatingKey);
                        return item;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to evaluate media existence for '{Title}' ({Category}).", title, category);
        }

        return null;
    }

    public async Task<IReadOnlyList<PlexMetadataItem>> SearchLibraryAsync(string query, string category, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<PlexMetadataItem>();
        }

        return await SearchHubsAsync(query, category, ct);
    }

    private async Task<IReadOnlyList<PlexMetadataItem>> SearchHubsAsync(string query, string category, CancellationToken ct)
    {
        var targetType = string.Equals(category, "tv", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(category, "shows", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(category, "show", StringComparison.OrdinalIgnoreCase)
            ? "show"
            : "movie";

        var endpoint = $"hubs/search?query={Uri.EscapeDataString(query)}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var res = await _client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                return Array.Empty<PlexMetadataItem>();
            }

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc) ||
                !mc.TryGetProperty("Hub", out var hubs) ||
                hubs.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<PlexMetadataItem>();
            }

            var results = new List<PlexMetadataItem>();
            foreach (var hub in hubs.EnumerateArray())
            {
                if (hub.TryGetProperty("type", out var typeProp) &&
                    string.Equals(typeProp.GetString(), targetType, StringComparison.OrdinalIgnoreCase))
                {
                    if (hub.TryGetProperty("Metadata", out var metaArr) && metaArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var meta in metaArr.EnumerateArray())
                        {
                            var item = meta.Deserialize<PlexMetadataItem>(PlexClient.DefaultJsonOptions);
                            if (item is not null)
                            {
                                results.Add(item);
                            }
                        }
                    }
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to perform Hub search for query '{Query}'", query);
            return Array.Empty<PlexMetadataItem>();
        }
    }

    private static string MapCategoryToSectionKey(string category)
    {
        return string.Equals(category, "tv", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(category, "show", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(category, "shows", StringComparison.OrdinalIgnoreCase)
            ? "My TV Shows"
            : "My Movies";
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
