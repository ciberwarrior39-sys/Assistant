using System.Text.RegularExpressions;

namespace Assistant.Core.Organizer;

public sealed record TodoItem(string Text, bool Done);

/// <summary>
/// todo.md as Markdown checkboxes, "- [ ] buy milk" and "- [x] call grandma", the format notes apps understand too.
/// Read fresh every time, so ticking a box in another editor applies at once. Other lines are kept as they are.
/// </summary>
public sealed partial class TodoStore(string file)
{
    public const string Header = "# To-do\n\nOne task per line: \"- [ ] task\" is open, \"- [x] task\" is done. Edit freely.\n\n";

    public IReadOnlyList<TodoItem> Items() => Lines().Select(Parse).OfType<TodoItem>().ToList();

    public TodoItem Add(string text)
    {
        var item = new TodoItem(NotesStore.OneLine(text), false);
        if (!File.Exists(file)) File.WriteAllText(file, Header);
        else if (new FileInfo(file).Length > 0 && !File.ReadAllText(file).EndsWith('\n')) File.AppendAllText(file, "\n");
        File.AppendAllText(file, $"- [ ] {item.Text}\n");
        return item;
    }

    /// <summary>Ticks (or unticks) the one task matching the query. Returns all that matched among the ones it applies to.</summary>
    public IReadOnlyList<TodoItem> SetDone(string query, bool done) =>
        Change(query, item => item.Done != done, (lines, index, item) => lines[index] = $"- [{(done ? "x" : " ")}] {item.Text}");

    /// <summary>Deletes the one task matching the query.</summary>
    public IReadOnlyList<TodoItem> Remove(string query) => Change(query, _ => true, (lines, index, _) => lines.RemoveAt(index));

    /// <summary>Deletes every ticked task; returns how many.</summary>
    public int ClearDone()
    {
        List<string> lines = Lines();
        int removed = lines.RemoveAll(line => Parse(line) is { Done: true });
        if (removed > 0) File.WriteAllLines(file, lines);
        return removed;
    }

    IReadOnlyList<TodoItem> Change(string query, Func<TodoItem, bool> applies, Action<List<string>, int, TodoItem> change)
    {
        List<string> lines = Lines();
        var candidates = lines.Select((line, index) => (Item: Parse(line), Index: index))
                              .Where(entry => entry.Item is not null && applies(entry.Item))
                              .ToList();
        var found = TextMatch.Find(candidates, entry => entry.Item!.Text, query);
        if (found.Count == 1)
        {
            change(lines, found[0].Index, found[0].Item!);
            File.WriteAllLines(file, lines);
        }
        return found.Select(entry => entry.Item!).ToList();
    }

    List<string> Lines() => File.Exists(file) ? File.ReadAllLines(file).ToList() : [];

    static TodoItem? Parse(string line)
    {
        Match match = TaskLine().Match(line);
        return match.Success ? new TodoItem(match.Groups[2].Value.Trim(), match.Groups[1].Value is "x" or "X") : null;
    }

    [GeneratedRegex(@"^\s*[-*]\s+\[([ xX])\]\s+(.+)$")]
    private static partial Regex TaskLine();
}
