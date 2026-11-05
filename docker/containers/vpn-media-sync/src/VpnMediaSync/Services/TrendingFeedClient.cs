using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnMediaSync.Models;

namespace VpnMediaSync.Services;

public sealed record DiscoveryCandidate(string ExternalId, string SearchQuery, string Category);

public sealed class TrendingFeedClient(
    HttpClient httpClient,
    IOptions<TrendingConfig> trendingOptions,
    JsonStateStore stateStore,
    ILogger<TrendingFeedClient> logger)
{
    private readonly TrendingConfig _config = trendingOptions?.Value ?? throw new ArgumentNullException(nameof(trendingOptions));

    public async Task<IReadOnlyList<DiscoveryCandidate>> GetUnseenCandidatesAsync(CancellationToken ct)
    {
        var results = new List<DiscoveryCandidate>();

        // 1. Add user-defined custom queries first
        if (_config.CustomQueries is { Count: > 0 })
        {
            foreach (var query in _config.CustomQueries)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    continue;
                }

                var externalId = $"custom:{query.Trim().ToLowerInvariant()}";
                if (!stateStore.IsExternalIdProcessed(externalId))
                {
                    results.Add(new DiscoveryCandidate(externalId, query.Trim(), "movies"));
                }
            }
        }

        // 2. Fetch popular movies JSON feed if enabled
        if (_config.Enabled && !string.IsNullOrWhiteSpace(_config.PopularMoviesUrl))
        {
            try
            {
                var movies = await httpClient.GetFromJsonAsync(
                    _config.PopularMoviesUrl,
                    AppJsonSerializerContext.Default.ListTrendingMovieItem,
                    ct) ?? [];

                foreach (var movie in movies)
                {
                    if (string.IsNullOrWhiteSpace(movie.Title))
                    {
                        continue;
                    }

                    var externalId = !string.IsNullOrWhiteSpace(movie.ImdbId)
                        ? movie.ImdbId
                        : $"title:{movie.Title.Trim().ToLowerInvariant()}";

                    if (!stateStore.IsExternalIdProcessed(externalId))
                    {
                        results.Add(new DiscoveryCandidate(externalId, movie.Title.Trim(), "movies"));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch popular movies feed from {Url}.", _config.PopularMoviesUrl);
            }
        }

        return results;
    }
}
