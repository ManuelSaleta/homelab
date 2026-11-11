using System.Text.RegularExpressions;
using Plex.Core.Http.Models;

namespace VpnMediaSync.Services;

/// <summary>
/// Provides fuzzy title parsing, cleaning, and matching between candidate/release titles
/// and Plex library metadata items.
/// </summary>
public static partial class MediaTitleMatcher
{
    private static readonly Regex YearRegex = new(
        @"\b(19\d\d|20\d\d)\b",
        RegexOptions.Compiled);

    private static readonly Regex TvSeasonEpisodeRegex = new(
        @"\b(s\d{1,2}(?:e\d{1,2})?|season\s*\d{1,2}|complete\s+season\s*\d{1,2}|series)\b.*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SceneTagsRegex = new(
        @"\b(1080p|720p|2160p|4k|uhd|bluray|blu-ray|brrip|bdrip|webrip|web-dl|web|h264|x264|h265|x265|hevc|av1|xvid|divx|10bit|10bits|remux|repack|proper|unrated|extended|directors\.cut|dual\.audio|multi|aac\d?\.?\d?|ac3|eac3|dts(-hd)?|truehd|atmos|ddp\d?\.?\d?|telesync|hdts|hdtc|cam|camrip|yify|yts(\.[a-z0-9]+)?|rarbg|galaxyrg|amzn|dsnp|nf|hulu|max)\b.*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FileExtensionRegex = new(
        @"\.(mp4|mkv|avi|mov|wmv|flv|webm|m4v|ts)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PunctuationRegex = new(
        @"[^\w\s]",
        RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "in", "on", "at", "to", "for", "with", "by"
    };

    /// <summary>
    /// Parses a raw torrent release or candidate query into a cleaned title and extracted year.
    /// </summary>
    public static (string CleanTitle, int? Year) ParseReleaseTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (string.Empty, null);
        }

        var text = raw;

        // Strip file extensions
        text = FileExtensionRegex.Replace(text, string.Empty);

        // Extract 4-digit year if present
        int? extractedYear = null;
        var yearMatches = YearRegex.Matches(text);
        if (yearMatches.Count > 0)
        {
            // Take the last 4-digit number that looks like a valid release year (e.g. 1950 - 2035)
            for (var i = yearMatches.Count - 1; i >= 0; i--)
            {
                if (int.TryParse(yearMatches[i].Value, out var y) && y is >= 1920 and <= 2035)
                {
                    extractedYear = y;
                    break;
                }
            }
        }

        // Replace common delimiters with spaces
        text = text.Replace('.', ' ')
                   .Replace('_', ' ')
                   .Replace('-', ' ')
                   .Replace('+', ' ');

        // If a release year was identified, strip it from the title text to isolate title words
        if (extractedYear.HasValue)
        {
            text = Regex.Replace(text, $@"\b{extractedYear.Value}\b", " ");
        }

        // Strip TV season/episode markers (e.g. S01E01, Season 1)
        text = TvSeasonEpisodeRegex.Replace(text, string.Empty);

        // Strip scene tags, resolutions, codecs
        text = SceneTagsRegex.Replace(text, string.Empty);

        // Strip any bracketed content left (e.g. [YTS], (2026))
        text = Regex.Replace(text, @"\[.*?\]|\(.*?\)", " ");

        // Normalize string
        var cleanTitle = Normalize(text);

        return (cleanTitle, extractedYear);
    }

    /// <summary>
    /// Normalizes a title for comparison: lowercase, alphanumeric and spaces only,
    /// leading/trailing articles removed, whitespace collapsed.
    /// </summary>
    public static string Normalize(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var s = title.ToLowerInvariant();

        // Replace punctuation with space
        s = PunctuationRegex.Replace(s, " ");

        // Collapse whitespace
        s = WhitespaceRegex.Replace(s, " ").Trim();

        // Strip leading articles
        if (s.StartsWith("the ", StringComparison.Ordinal)) s = s[4..];
        else if (s.StartsWith("a ", StringComparison.Ordinal)) s = s[2..];
        else if (s.StartsWith("an ", StringComparison.Ordinal)) s = s[3..];

        return s.Trim();
    }

    /// <summary>
    /// Evaluates whether a raw candidate or torrent release title matches an existing Plex metadata item.
    /// </summary>
    public static bool IsMatch(string rawTitle, string category, PlexMetadataItem libraryItem, int? explicitYear = null)
    {
        if (string.IsNullOrWhiteSpace(rawTitle) || libraryItem is null)
        {
            return false;
        }

        var (cleanTarget, targetYear) = ParseReleaseTitle(rawTitle);
        var effectiveTargetYear = explicitYear ?? targetYear;

        var libTitle = libraryItem.Title ?? libraryItem.OriginalTitle ?? libraryItem.ParentTitle ?? string.Empty;
        var cleanLib = Normalize(libTitle);

        if (string.IsNullOrWhiteSpace(cleanTarget) || string.IsNullOrWhiteSpace(cleanLib))
        {
            return false;
        }

        // 1. Year validation: If both target and library have years, they cannot differ by more than 1 year
        if (effectiveTargetYear.HasValue && libraryItem.Year.HasValue)
        {
            var yearDiff = Math.Abs(effectiveTargetYear.Value - libraryItem.Year.Value);
            if (yearDiff > 1)
            {
                return false;
            }
        }

        // 2. Exact match
        if (string.Equals(cleanTarget, cleanLib, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. Compact match (stripping spaces completely, e.g. "Spider Man" == "Spiderman")
        var compactTarget = cleanTarget.Replace(" ", string.Empty);
        var compactLib = cleanLib.Replace(" ", string.Empty);
        if (string.Equals(compactTarget, compactLib, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Token sets
        var targetTokens = cleanTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var libTokens = cleanLib.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Filter out stopwords for token analysis
        var meaningfulTarget = targetTokens.Where(t => !StopWords.Contains(t)).ToList();
        var meaningfulLib = libTokens.Where(t => !StopWords.Contains(t)).ToList();

        if (meaningfulTarget.Count == 0) meaningfulTarget = [.. targetTokens];
        if (meaningfulLib.Count == 0) meaningfulLib = [.. libTokens];

        // 4. Token subset containment
        // If one title is entirely contained in the other and has >= 2 meaningful words:
        if (meaningfulLib.Count >= 2 && meaningfulTarget.Count >= 2)
        {
            var targetSet = new HashSet<string>(meaningfulTarget, StringComparer.OrdinalIgnoreCase);
            var libSet = new HashSet<string>(meaningfulLib, StringComparer.OrdinalIgnoreCase);

            var intersectionCount = targetSet.Intersect(libSet).Count();
            var minCount = Math.Min(targetSet.Count, libSet.Count);

            // If 80%+ of the shorter title's words are in the longer title
            var overlapRatio = (double)intersectionCount / minCount;
            if (overlapRatio >= 0.8)
            {
                // If years matched or either had no year, this is a strong match
                return true;
            }
        }
        else if (meaningfulLib.Count == 1 && meaningfulTarget.Count == 1)
        {
            // For single-word titles (e.g. "Loki", "Suits", "Atlanta"), require exact or compact match
            if (string.Equals(meaningfulTarget[0], meaningfulLib[0], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 5. Levenshtein similarity
        var distance = ComputeLevenshteinDistance(cleanTarget, cleanLib);
        var maxLen = Math.Max(cleanTarget.Length, cleanLib.Length);
        if (maxLen > 0)
        {
            var similarity = 1.0 - ((double)distance / maxLen);
            // If years matched explicitly, allow 0.78 threshold; otherwise require 0.85
            var threshold = (effectiveTargetYear.HasValue && libraryItem.Year.HasValue &&
                             effectiveTargetYear.Value == libraryItem.Year.Value) ? 0.78 : 0.85;

            if (similarity >= threshold)
            {
                return true;
            }
        }

        return false;
    }

    private static int ComputeLevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
        if (string.IsNullOrEmpty(t)) return s.Length;

        var d = new int[s.Length + 1, t.Length + 1];

        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;

        for (var j = 1; j <= t.Length; j++)
        {
            for (var i = 1; i <= s.Length; i++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[s.Length, t.Length];
    }
}
