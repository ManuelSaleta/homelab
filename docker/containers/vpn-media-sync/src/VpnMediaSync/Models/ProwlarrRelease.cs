using System.Text.Json.Serialization;

namespace VpnMediaSync.Models;

public sealed record ProwlarrCategory(
    int Id,
    string? Name
);

public sealed record ProwlarrRelease(
    string Guid,
    string Title,
    long Size,
    int IndexerId,
    string? Indexer,
    string? InfoHash,
    string? MagnetUrl,
    string? DownloadUrl,
    int? Seeders,
    int? Leechers,
    DateTimeOffset PublishDate,
    IReadOnlyList<ProwlarrCategory>? Categories
)
{
    public string? GetBestDownloadUri() =>
        !string.IsNullOrWhiteSpace(MagnetUrl) ? MagnetUrl : DownloadUrl;
}

public sealed record TrendingMovieItem(
    string? Title,
    string? Name,
    string? Imdb_Id,
    string? Poster_Url
)
{
    public string EffectiveTitle => !string.IsNullOrWhiteSpace(Title) ? Title : (Name ?? string.Empty);
    public string? ImdbId => Imdb_Id;
    public string? PosterUrl => Poster_Url;
}

public sealed record TrendingTvItem(
    string? Title,
    string? Name,
    string? Imdb_Id,
    string? Poster_Url
)
{
    public string EffectiveTitle => !string.IsNullOrWhiteSpace(Title) ? Title : (Name ?? string.Empty);
    public string? ImdbId => Imdb_Id;
    public string? PosterUrl => Poster_Url;
}
