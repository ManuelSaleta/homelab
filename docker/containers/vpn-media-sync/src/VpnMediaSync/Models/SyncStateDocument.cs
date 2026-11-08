namespace VpnMediaSync.Models;

public sealed record MediaHistoryEntry(
    string? ExternalId,
    string InfoHash,
    string Title,
    string Category,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SyncedAt
);

public sealed record SyncStateDocument(
    List<MediaHistoryEntry> Items
);
