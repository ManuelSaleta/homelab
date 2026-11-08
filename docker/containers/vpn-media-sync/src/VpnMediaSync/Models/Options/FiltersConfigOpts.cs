namespace VpnMediaSync.Models.Options;

public sealed class FiltersConfigOpts
{
    public const string SectionName = "Filters";

    /// <summary>
    /// Category filtering parameters and size thresholds applied to movie torrent candidates.
    /// </summary>
    public CategoryFilterConfigOpts Movies { get; set; } = new();

    /// <summary>
    /// Category filtering parameters and size thresholds applied to television torrent candidates.
    /// </summary>
    public CategoryFilterConfigOpts Tv { get; set; } = new();
}
