namespace Jarvis.Sharp.State;

using System.Text.RegularExpressions;

public static partial class CommandParser
{
    [GeneratedRegex(@"\b\d+\b", RegexOptions.Compiled)]
    private static partial Regex NumberRegex();

    public static IReadOnlyList<int> ParseDownloadCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<int>();
        }

        var results = new List<int>();
        var matches = NumberRegex().Matches(text);
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Value, out var idx) && idx > 0 && !results.Contains(idx))
            {
                results.Add(idx);
            }
        }

        return results;
    }
}
