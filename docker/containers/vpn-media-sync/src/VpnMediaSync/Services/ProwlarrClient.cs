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

    private static readonly Regex SoloEpisodeRegex = new(
        @"\b(S\d{1,2}\s*E\d{1,3}|\d{1,2}x\d{1,3}|E(pisode)?\s*\d{1,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex SeasonPackRegex = new(
        @"\b(Season\s*\d+|Seasons|Complete|S\d{1,2}(?!\s*E\d+)|S\d{1,2}\s*-\s*S?\d{1,2})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
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
            .OrderByDescending(r => GetSeasonPackScore(r.Title, filter.PreferSeasonPacks))
            .ThenByDescending(r => r.Seeders ?? 0)
            .FirstOrDefault();

        if (best is null) return null;

        _logger.LogInformation(
            "Matched release '{Title}' ({Seeders} seeders, {SizeMb} MB, seasonPackScore: {Score}) for query '{Query}'.",
            best.Title,
            best.Seeders ?? 0,
            best.Size / (1024 * 1024),
            GetSeasonPackScore(best.Title, filter.PreferSeasonPacks),
            query);

        return best;
    }

    private static int GetSeasonPackScore(string title, bool preferSeasonPacks)
    {
        if (!preferSeasonPacks) return 0;
        var isSolo = SoloEpisodeRegex.IsMatch(title);
        var isSeason = SeasonPackRegex.IsMatch(title) && !isSolo;
        if (isSeason) return 2;
        if (!isSolo) return 1;
        return 0;
    }

    private static Regex GetOrCompileRegex(string pattern) =>
        RegexCache.GetOrAdd(
            pattern,
            static p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout));
}
