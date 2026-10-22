using System.Globalization;
using System.Text.RegularExpressions;

namespace Assistant.Core.Organizer;

/// <summary>One note. Written is MinValue for a line typed by hand without a date.</summary>
public sealed record Note(DateTime Written, string Text);

/// <summary>
/// notes.md: one note per line, "- 2026-10-22 18:40 · the wifi password is on the router". Read fresh every time,
/// so edits by hand apply at once. Other lines (the heading, blank lines) are kept as they are.
/// </summary>
public sealed partial class NotesStore(string file)
{
    public const string Header = "# Notes\n\nOne note per line, starting with \"- \". Edit or delete any line.\n\n";
    const string TimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>Newest first; hand-written notes without a date come last, in file order.</summary>
    public IReadOnlyList<Note> All() =>
        Lines().Select(Parse).OfType<Note>().Reverse().OrderByDescending(note => note.Written).ToList();

    public Note Add(string text, DateTime now)
    {
        var note = new Note(now, OneLine(text));
        EnsureFile();
        File.AppendAllText(file, $"- {now.ToString(TimeFormat, CultureInfo.InvariantCulture)} · {note.Text}\n");
        return note;
    }

    public IReadOnlyList<Note> Find(string query) => TextMatch.Find(All(), note => note.Text, query);

    /// <summary>Removes the note matching the query, if exactly one does. Returns all that matched.</summary>
    public IReadOnlyList<Note> Remove(string query)
    {
        List<string> lines = Lines();
        var matching = lines.Select((line, index) => (Note: Parse(line), Index: index))
                            .Where(entry => entry.Note is not null)
                            .ToList();
        var found = TextMatch.Find(matching, entry => entry.Note!.Text, query);
        if (found.Count == 1)
        {
            lines.RemoveAt(found[0].Index);
            File.WriteAllLines(file, lines);
        }
        return found.Select(entry => entry.Note!).ToList();
    }

    List<string> Lines() => File.Exists(file) ? File.ReadAllLines(file).ToList() : [];

    void EnsureFile()
    {
        if (!File.Exists(file)) File.WriteAllText(file, Header);
        else if (new FileInfo(file).Length > 0 && !File.ReadAllText(file).EndsWith('\n')) File.AppendAllText(file, "\n");
    }

    static Note? Parse(string line)
    {
        Match dated = DatedLine().Match(line);
        if (dated.Success && DateTime.TryParseExact(dated.Groups[1].Value, TimeFormat, CultureInfo.InvariantCulture,
                                                     DateTimeStyles.None, out DateTime written))
            return new Note(written, dated.Groups[2].Value.Trim());
        Match plain = PlainLine().Match(line);
        return plain.Success ? new Note(DateTime.MinValue, plain.Groups[1].Value.Trim()) : null;
    }

    internal static string OneLine(string text) => Spaces().Replace(text.Trim(), " ");

    [GeneratedRegex(@"^\s*-\s+(\d{4}-\d{2}-\d{2} \d{2}:\d{2})\s+·\s+(.+)$")]
    private static partial Regex DatedLine();

    [GeneratedRegex(@"^\s*-\s+(?!\[[ xX]\])(.+)$")]
    private static partial Regex PlainLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
