namespace VpnMediaSync.Models;

public sealed record QBitTorrentItem(
    string Hash,
    string Name,
    string State,
    double Progress,
    long Size,
    double Ratio,
    long Seeding_Time,
    string? Category,
    string? Content_Path,
    string? Save_Path
)
{
    public string? ContentPath => Content_Path;
    public string? SavePath => Save_Path;
    public long SeedingTimeSeconds => Seeding_Time;
}

public sealed record QBitTorrentFileItem(
    int Index,
    string Name,
    long Size,
    double Progress,
    int Priority,
    bool? Is_Seed
);

