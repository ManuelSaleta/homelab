using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnMediaSync.Models;

namespace VpnMediaSync.Services;

public sealed class ProwlarrClient(
    HttpClient httpClient,
    IOptions<IndexerConfig> indexerOptions,
    ILogger<ProwlarrClient> logger)
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private readonly ILogger<ProwlarrClient> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IndexerConfig _config = indexerOptions?.Value ?? throw new ArgumentNullException(nameof(indexerOptions));

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.ApiKey) &&
        !_config.ApiKey.StartsWith("UNCONFIGURED", StringComparison.OrdinalIgnoreCase);

    public async Task<ProwlarrRelease?> SearchBestReleaseAsync(
        string query,
        CategoryFilterConfig filter,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Prowlarr API key is not configured; skipping search for '{Query}'.", query);
            return null;
        }

        var categoryQuery = string.Join("&", filter.CategoryIds.Select(id => $"categories={id}"));
        var baseUrl = _config.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/api/v1/search?query={Uri.EscapeDataString(query)}&{categoryQuery}&type=search";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Api-Key", _config.ApiKey);

        using var resp = await httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var releases = await resp.Content.ReadFromJsonAsync(
            AppJsonSerializerContext.Default.ListProwlarrRelease,
            ct) ?? [];

        var minBytes = filter.MinSizeMb * 1024L * 1024L;
        var maxBytes = filter.MaxSizeMb * 1024L * 1024L;

        var allowedRegexes = filter.AllowedPatterns.Select(GetOrCompileRegex).ToArray();
        var blockedRegexes = filter.BlockedPatterns.Select(GetOrCompileRegex).ToArray();

        var best = releases
            .Where(r => !string.IsNullOrWhiteSpace(r.GetBestDownloadUri()))
            .Where(r => (r.Seeders ?? 0) >= _config.MinSeeders)
            .Where(r => r.Size >= minBytes && r.Size <= maxBytes)
            .Where(r => allowedRegexes.Length == 0 || allowedRegexes.Any(rx => rx.IsMatch(r.Title)))
            .Where(r => !blockedRegexes.Any(rx => rx.IsMatch(r.Title)))
            .OrderByDescending(r => r.Seeders ?? 0)
            .FirstOrDefault();

        if (best is null) return null;

        _logger.LogInformation(
            "Matched release '{Title}' ({Seeders} seeders, {SizeMb} MB) for query '{Query}'.",
            best.Title,
            best.Seeders ?? 0,
            best.Size / (1024 * 1024),
            query);

        return best;
    }

    private static Regex GetOrCompileRegex(string pattern) =>
        RegexCache.GetOrAdd(
            pattern,
            static p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout));
}
