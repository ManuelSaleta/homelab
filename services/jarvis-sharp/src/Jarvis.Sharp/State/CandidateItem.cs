namespace Jarvis.Sharp.State;

using System.Text.Json.Serialization;

public record CandidateItem(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")] int? Year = null,
    [property: JsonPropertyName("media_type")] string MediaType = "Movie"
);

public record CandidateBatch(
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("items")] IReadOnlyList<CandidateItem> Items
);
