using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Organizer;

/// <summary>A timer ("set a timer for 5 minutes") or a reminder ("remind me tomorrow at 9 to call the bank").</summary>
public sealed record ReminderEntry(DateTime Due, string Text, bool Timer);

/// <summary>
/// reminders.json: what's waiting to go off, so it survives quitting, restarting and updates. Kept in memory and written
/// on every change. Times are local. A file that can't be read is set aside as reminders.json.bad, never overwritten.
/// </summary>
public sealed class ReminderStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    readonly string file;
    readonly List<ReminderEntry> entries;

    public ReminderStore(string file)
    {
        this.file = file;
        entries = Load(file);
    }

    public IReadOnlyList<ReminderEntry> All => entries.OrderBy(entry => entry.Due).ToList();

    public void Add(ReminderEntry entry)
    {
        entries.Add(entry);
        Save();
    }

    /// <summary>Removes and returns everything due at or before <paramref name="now"/>, oldest first.</summary>
    public IReadOnlyList<ReminderEntry> TakeDue(DateTime now)
    {
        List<ReminderEntry> due = entries.Where(entry => entry.Due <= now).OrderBy(entry => entry.Due).ToList();
        if (due.Count == 0) return due;
        entries.RemoveAll(due.Contains);
        Save();
        return due;
    }

    /// <summary>Removes the one entry matching the words. Returns all that matched.</summary>
    public IReadOnlyList<ReminderEntry> Remove(string words)
    {
        List<ReminderEntry> found = TextMatch.Find(entries, entry => entry.Text, words);
        if (found.Count == 1)
        {
            entries.Remove(found[0]);
            Save();
        }
        return found;
    }

    public int Clear()
    {
        int count = entries.Count;
        entries.Clear();
        Save();
        return count;
    }

    void Save()
    {
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new Saved(entries.OrderBy(entry => entry.Due).ToList()), Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Could not save reminders.json", ex);
        }
    }

    static List<ReminderEntry> Load(string file)
    {
        if (!File.Exists(file)) return [];
        try
        {
            return JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), Options)?.Reminders?.ToList() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DiagnosticLog.Error("reminders.json couldn't be read; set aside as reminders.json.bad", ex);
            try
            {
                File.Move(file, file + ".bad", overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // Left in place; the next save replaces it.
            }
            return [];
        }
    }

    sealed record Saved(List<ReminderEntry> Reminders);
}
