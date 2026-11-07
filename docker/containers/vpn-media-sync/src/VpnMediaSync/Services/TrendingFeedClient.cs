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

        // 1. Add user-defined custom queries (supports "tv:Title" or "movie:Title" prefixes)
        if (_config.CustomQueries is { Count: > 0 })
        {
            foreach (var rawQuery in _config.CustomQueries)
            {
                if (string.IsNullOrWhiteSpace(rawQuery))
                {
                    continue;
                }

                var trimmed = rawQuery.Trim();
                var category = "movies";
                var query = trimmed;

                if (trimmed.StartsWith("tv:", StringComparison.OrdinalIgnoreCase))
                {
                    category = "tv";
                    query = trimmed[3..].Trim();
                }
                else if (trimmed.StartsWith("movie:", StringComparison.OrdinalIgnoreCase))
                {
                    category = "movies";
                    query = trimmed[6..].Trim();
                }
                else if (trimmed.StartsWith("movies:", StringComparison.OrdinalIgnoreCase))
                {
                    category = "movies";
                    query = trimmed[7..].Trim();
                }

                if (string.IsNullOrWhiteSpace(query))
                {
                    continue;
                }

                var externalId = $"custom:{category}:{query.ToLowerInvariant()}";
                var legacyExternalId = $"custom:{query.ToLowerInvariant()}";
                if (!stateStore.IsExternalIdProcessed(externalId) && !stateStore.IsExternalIdProcessed(legacyExternalId))
                {
                    results.Add(new DiscoveryCandidate(externalId, query, category));
                }
            }
        }

        // 2. Add user-defined custom TV queries
        if (_config.CustomTvQueries is { Count: > 0 })
        {
            foreach (var rawQuery in _config.CustomTvQueries)
            {
                if (string.IsNullOrWhiteSpace(rawQuery))
                {
                    continue;
                }

                var query = rawQuery.Trim();
                var externalId = $"custom:tv:{query.ToLowerInvariant()}";
                if (!stateStore.IsExternalIdProcessed(externalId))
                {
                    results.Add(new DiscoveryCandidate(externalId, query, "tv"));
                }
            }
        }

        // 3. Fetch popular movies JSON feeds if enabled
        if (_config.Enabled)
        {
            var movieUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_config.PopularMoviesUrl))
            {
                movieUrls.Add(_config.PopularMoviesUrl.Trim());
            }
            if (_config.MovieFeedUrls is { Count: > 0 })
            {
                foreach (var url in _config.MovieFeedUrls)
                {
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        movieUrls.Add(url.Trim());
                    }
                }
            }

            foreach (var url in movieUrls)
            {
                try
                {
                    var movies = await httpClient.GetFromJsonAsync(
                        url,
                        AppJsonSerializerContext.Default.ListTrendingMovieItem,
                        ct) ?? [];

                    foreach (var movie in movies)
                    {
                        var title = movie.EffectiveTitle;
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            continue;
                        }

                        var externalId = !string.IsNullOrWhiteSpace(movie.ImdbId)
                            ? movie.ImdbId
                            : $"title:{title.Trim().ToLowerInvariant()}";

                        if (!stateStore.IsExternalIdProcessed(externalId))
                        {
                            results.Add(new DiscoveryCandidate(externalId, title.Trim(), "movies"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to fetch popular movies feed from {Url}.", url);
                }
            }
        }

        // 4. Fetch popular TV shows JSON feeds if enabled and configured
        if (_config.Enabled)
        {
            var tvUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_config.PopularTvUrl))
            {
                tvUrls.Add(_config.PopularTvUrl.Trim());
            }
            if (_config.TvFeedUrls is { Count: > 0 })
            {
                foreach (var url in _config.TvFeedUrls)
                {
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        tvUrls.Add(url.Trim());
                    }
                }
            }

            foreach (var url in tvUrls)
            {
                try
                {
                    var shows = await httpClient.GetFromJsonAsync(
                        url,
                        AppJsonSerializerContext.Default.ListTrendingTvItem,
                        ct) ?? [];

                    foreach (var show in shows)
                    {
                        var title = show.EffectiveTitle;
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            continue;
                        }

                        var externalId = !string.IsNullOrWhiteSpace(show.ImdbId)
                            ? show.ImdbId
                            : $"title:tv:{title.Trim().ToLowerInvariant()}";

                        if (!stateStore.IsExternalIdProcessed(externalId))
                        {
                            results.Add(new DiscoveryCandidate(externalId, title.Trim(), "tv"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to fetch popular TV feed from {Url}.", url);
                }
            }
        }

        return results;
    }
}
