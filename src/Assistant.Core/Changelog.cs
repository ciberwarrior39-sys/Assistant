using System.Reflection;
using System.Text.RegularExpressions;

namespace Assistant.Core;

/// <summary>One version's notes: "0.6.0", "November 2026", and its changes (each may start with a bold title).</summary>
public sealed record ReleaseNotes(string Version, string Date, IReadOnlyList<string> Changes)
{
    /// <summary>Shown after an update: the first three changes.</summary>
    public IReadOnlyList<string> Highlights => Changes.Take(3).ToList();
}

/// <summary>
/// CHANGELOG.md, built into the app: what's new in each version, for Settings > What's new and the card after an
/// update. The same file becomes each GitHub release's notes, so it's written once.
/// </summary>
public static partial class Changelog
{
    public static IReadOnlyList<ReleaseNotes> Parse(string markdown)
    {
        var releases = new List<ReleaseNotes>();
        string? version = null, date = "";
        var changes = new List<string>();
        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (Heading().Match(line) is { Success: true } heading)
            {
                if (version is not null) releases.Add(new ReleaseNotes(version, date, changes));
                version = heading.Groups["version"].Value;
                date = heading.Groups["date"].Value.Trim();
                changes = [];
            }
            else if (version is not null && line.StartsWith("- ", StringComparison.Ordinal))
            {
                changes.Add(line[2..].Trim());
            }
        }
        if (version is not null) releases.Add(new ReleaseNotes(version, date, changes));
        return releases;
    }

    /// <summary>The changelog built into the app (empty if it's missing).</summary>
    public static IReadOnlyList<ReleaseNotes> BuiltIn(Assembly assembly)
    {
        using Stream? stream = assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null) return [];
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>The notes for this version: "0.6.0" also matches "0.6.0.0" and "v0.6.0".</summary>
    public static ReleaseNotes? For(IReadOnlyList<ReleaseNotes> releases, string version)
    {
        string wanted = version.TrimStart('v', 'V');
        return releases.FirstOrDefault(release => wanted == release.Version || wanted.StartsWith(release.Version + ".", StringComparison.Ordinal));
    }

    /// <summary>"**Title:** text" → ("Title:", "text"); a plain line → ("", line).</summary>
    public static (string Title, string Text) Split(string change)
    {
        Match bold = BoldStart().Match(change);
        return bold.Success ? (bold.Groups["title"].Value, change[bold.Length..].TrimStart()) : ("", change.Replace("**", ""));
    }

    [GeneratedRegex(@"^##\s+v?(?<version>\d+\.\d+\.\d+)\s*(\((?<date>[^)]*)\))?")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\*\*(?<title>[^*]+)\*\*")]
    private static partial Regex BoldStart();
}
