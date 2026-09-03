using System.Text.RegularExpressions;

namespace LidUtils.Data;

public static partial class VdfLibraryParser
{
    [GeneratedRegex("^\\s*\"(?<key>[^\"]+)\"\\s*\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Multiline)]
    private static partial Regex KeyValueLinePattern();

    public static IReadOnlyList<string> ParseLibraryPaths(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in KeyValueLinePattern().Matches(contents))
        {
            var key = match.Groups["key"].Value;
            var value = Unescape(match.Groups["value"].Value);

            var isPathEntry = key.Equals("path", StringComparison.OrdinalIgnoreCase);
            var isLegacyEntry = int.TryParse(key, out _) && Path.IsPathRooted(value);

            if ((isPathEntry || isLegacyEntry) && Path.IsPathRooted(value) && seen.Add(value))
            {
                paths.Add(value);
            }
        }

        return paths;
    }

    private static string Unescape(string value) => value
        .Replace("\\\\", "\\", StringComparison.Ordinal)
        .Replace("\\\"", "\"", StringComparison.Ordinal);
}

