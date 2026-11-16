namespace Jarvis.Sharp.Services;

using System.Text.Json;
using System.Text.RegularExpressions;

public record PlexQueryResult(bool Exists, string Title, int? Year, string Source);

public interface IPlexService
{
    Task<PlexQueryResult> QueryPlexLibraryAsync(
        string title,
        int? year = null,
        string? plexUrl = null,
        string? plexToken = null,
        string? existingInventoryPath = null,
        CancellationToken cancellationToken = default);
}

public partial class PlexService : IPlexService
{
    private readonly HttpClient _httpClient;

    [GeneratedRegex("[^a-zA-Z0-9 ]", RegexOptions.Compiled)]
    private static partial Regex AlphanumericRegex();

    public PlexService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<PlexQueryResult> QueryPlexLibraryAsync(
        string title,
        int? year = null,
        string? plexUrl = null,
        string? plexToken = null,
        string? existingInventoryPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var normalizedTarget = NormalizeTitle(title);
        var resolvedUrl = (plexUrl ?? Environment.GetEnvironmentVariable("PLEX_URL") ?? string.Empty).TrimEnd('/');
        var resolvedToken = plexToken ?? Environment.GetEnvironmentVariable("PLEX_TOKEN") ?? string.Empty;
        var resolvedInventory = existingInventoryPath ?? Environment.GetEnvironmentVariable("EXISTING_MOVIES_INDEX_PATH") ?? "/state/existing-movies.json";

        if (!string.IsNullOrEmpty(resolvedUrl) && !string.IsNullOrEmpty(resolvedToken))
        {
            try
            {
                var searchUrl = $"{resolvedUrl}/search?query={Uri.EscapeDataString(title)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                request.Headers.Add("X-Plex-Token", resolvedToken);
                request.Headers.Add("Accept", "application/json");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) &&
                        mc.TryGetProperty("Metadata", out var metadataList) &&
                        metadataList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in metadataList.EnumerateArray())
                        {
                            var itemTitle = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                            var itemYear = item.TryGetProperty("year", out var y) && y.TryGetInt32(out var parsedYear) ? parsedYear : (int?)null;

                            if (NormalizeTitle(itemTitle) == normalizedTarget)
                            {
                                if (year == null || itemYear == null || year == itemYear)
                                {
                                    return new PlexQueryResult(true, itemTitle, itemYear, "Plex REST API");
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fall back to local inventory on connection failure
            }
        }

        // Local Inventory JSON Fallback
        if (File.Exists(resolvedInventory))
        {
            try
            {
                var json = await File.ReadAllTextAsync(resolvedInventory, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        var entryTitle = elem.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                        var entryYear = elem.TryGetProperty("year", out var y) && y.TryGetInt32(out var py) ? py : (int?)null;

                        if (NormalizeTitle(entryTitle) == normalizedTarget)
                        {
                            if (year == null || entryYear == null || year == entryYear)
                            {
                                return new PlexQueryResult(true, entryTitle, entryYear, "Local Inventory File");
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback returns false
            }
        }

        return new PlexQueryResult(false, title, year, "None");
    }

    private static string NormalizeTitle(string title)
    {
        return AlphanumericRegex().Replace(title.ToLowerInvariant(), string.Empty).Trim();
    }
}
