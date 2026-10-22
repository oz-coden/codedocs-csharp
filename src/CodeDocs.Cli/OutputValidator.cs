using System.Net;
using System.Text.RegularExpressions;

namespace CodeDocs.Cli;

internal static partial class OutputValidator
{
    [GeneratedRegex("href=\"([^\"]+)\"")]
    private static partial Regex HtmlLinks();

    public static void Validate(string directory)
    {
        var anchors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.html", SearchOption.AllDirectories))
        {
            foreach (Match match in HtmlLinks().Matches(File.ReadAllText(file)))
            {
                var url = WebUtility.HtmlDecode(match.Groups[1].Value);
                if (Uri.TryCreate(url, UriKind.Absolute, out _)) continue;
                var parts = url.Split('#', 2);
                var target = parts[0].Length == 0 ? file : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, parts[0]));
                if (!File.Exists(target)) throw new IOException("Generated output has a missing link target: " + url);
                if (parts.Length < 2 || parts[1].Length == 0 || !target.EndsWith(".html", StringComparison.Ordinal)) continue;
                if (!anchors.TryGetValue(target, out var ids))
                    anchors[target] = ids = Regex.Matches(File.ReadAllText(target), "id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                if (!ids.Contains(parts[1])) throw new IOException("Generated output has a missing anchor: " + url);
            }
        }
    }
}
