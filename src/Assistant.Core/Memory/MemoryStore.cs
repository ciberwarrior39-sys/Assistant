using System.Text;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Memory;

public enum MemoryKind { AboutMe, Preferences, Places }

/// <summary>One saved line. Text is the fact without its "(learned ...)" note.</summary>
public sealed record MemoryFact(MemoryKind Kind, string Text, string Line);

/// <summary>
/// memory\about-me.md, preferences.md and places.md: one fact per line, starting with "- ".
/// The files are read on every use, so edits in Notepad apply right away; other lines (headings, notes) are ignored.
/// </summary>
public sealed partial class MemoryStore(string folder)
{
    public const int MaxFactLength = 200;

    readonly Lock gate = new();

    public string FileFor(MemoryKind kind) => Path.Combine(folder, kind switch
    {
        MemoryKind.Preferences => "preferences.md",
        MemoryKind.Places => "places.md",
        _ => "about-me.md",
    });

    public IReadOnlyList<MemoryFact> Facts(MemoryKind kind)
    {
        string[] lines;
        try
        {
            lock (gate)
            {
                string file = FileFor(kind);
                if (!File.Exists(file)) return [];
                lines = File.ReadAllLines(file);
            }
        }
        catch (IOException ex)
        {
            DiagnosticLog.Error("Could not read a memory file", ex);
            return [];
        }

        return lines
            .Select(line => (Line: line, Text: FactText(line)))
            .Where(fact => fact.Text.Length > 0)
            .Select(fact => new MemoryFact(kind, fact.Text, fact.Line))
            .ToList();
    }

    public IReadOnlyList<MemoryFact> AllFacts() =>
        Enum.GetValues<MemoryKind>().SelectMany(kind => Facts(kind)).ToList();

    /// <summary>Appends a fact with today's date and where it came from. False if it was already saved.</summary>
    public bool Add(MemoryKind kind, string fact, string source)
    {
        string text = Clean(fact);
        if (text.Length == 0) return false;
        if (Facts(kind).Any(existing => string.Equals(existing.Text, text, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Brackets in the source would break reading the note back, so they're dropped.
        string from = Clean(source).Replace("(", "").Replace(")", "");
        string line = $"- {text} (learned {DateTime.Today:yyyy-MM-dd} from: {from})";
        lock (gate)
        {
            string file = FileFor(kind);
            Directory.CreateDirectory(folder);
            // Starts a new line if the user's last edit didn't end with one.
            string existing = File.Exists(file) ? File.ReadAllText(file) : "";
            string prefix = existing.Length > 0 && !existing.EndsWith('\n') ? Environment.NewLine : "";
            File.AppendAllText(file, prefix + line + Environment.NewLine, Encoding.UTF8);
        }
        return true;
    }

    /// <summary>Deletes these exact lines from their files and returns the ones removed.</summary>
    public IReadOnlyList<MemoryFact> Remove(IEnumerable<MemoryFact> facts)
    {
        var removed = new List<MemoryFact>();
        foreach (var group in facts.GroupBy(fact => fact.Kind))
        {
            lock (gate)
            {
                string file = FileFor(group.Key);
                if (!File.Exists(file)) continue;

                var targets = group.Select(fact => fact.Line).ToHashSet(StringComparer.Ordinal);
                string[] lines = File.ReadAllLines(file);
                string[] kept = lines.Where(line => !targets.Contains(line)).ToArray();
                if (kept.Length == lines.Length) continue;

                // Written to a temporary file first, so a crash can't leave half a memory file.
                string temp = file + ".tmp";
                File.WriteAllLines(temp, kept, Encoding.UTF8);
                File.Move(temp, file, overwrite: true);
                removed.AddRange(group.Where(fact => !kept.Contains(fact.Line, StringComparer.Ordinal)));
            }
        }
        return removed;
    }

    /// <summary>Saved facts ranked by how many words they share with the topic. Ties keep file order.</summary>
    public IReadOnlyList<MemoryFact> Search(string topic, IEnumerable<MemoryFact> facts)
    {
        string[] words = Words(topic);
        return facts
            .Select((fact, index) => (fact, index, score: Overlap(words, Words(fact.Text))))
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.index)
            .Select(item => item.fact)
            .ToList();
    }

    /// <summary>How many of the query's words appear in the fact (prefix match, so "project" finds "projects").</summary>
    public static int Overlap(string[] query, string[] fact) =>
        query.Count(word => fact.Any(other => other.StartsWith(word, StringComparison.Ordinal)
                                              || word.StartsWith(other, StringComparison.Ordinal)));

    static readonly HashSet<string> StopWords =
    [
        "a", "an", "the", "my", "me", "i", "is", "are", "was", "to", "of", "in", "on", "at", "for", "and", "or",
        "that", "this", "it", "what", "do", "you", "know", "about", "your", "with", "from", "be", "am",
    ];

    public static string[] Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(word => word.Length > 1 && !StopWords.Contains(word))
            .Distinct()
            .ToArray();

    // "- Uses Unity a lot (learned 2026-10-14 from: you said)" -> "Uses Unity a lot". Non-bullet lines -> "".
    static string FactText(string line)
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith("- ", StringComparison.Ordinal) && !trimmed.StartsWith("* ", StringComparison.Ordinal))
            return "";
        return LearnedNote().Replace(trimmed[2..], "").Trim();
    }

    // One line, no bullet, no markdown heading, bounded length.
    static string Clean(string text)
    {
        string single = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        single = single.TrimStart('-', '*', '#', ' ').TrimEnd('.', ' ');
        return single.Length > MaxFactLength ? single[..MaxFactLength] : single;
    }

    [GeneratedRegex(@"\s*\(learned [^)]*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LearnedNote();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}
