using System.Text.Encodings.Web;
using System.Text.Json;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Activity;

/// <summary>One stretch of time in one window. Idle time is never part of an entry.</summary>
public sealed record ActivityEntry(DateTime Start, DateTime End, string App, string Title)
{
    public double Seconds => Math.Round((End - Start).TotalSeconds);
}

/// <summary>
/// activity\yyyy-MM-dd.jsonl: one line per window the user spent time in.
/// Plain text on this computer only; deleted after 7 days.
/// </summary>
public sealed class ActivityLog(string folder)
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // readable titles in Notepad
    };

    static readonly Lock Gate = new();

    public void Append(ActivityEntry entry)
    {
        try
        {
            string line = JsonSerializer.Serialize(entry, Options);
            lock (Gate)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(FileFor(entry.Start), line + Environment.NewLine);
            }
        }
        catch (IOException ex)
        {
            DiagnosticLog.Error("Could not write the activity log", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            DiagnosticLog.Error("Could not write the activity log", ex);
        }
    }

    /// <summary>Entries that overlap the period, from the daily files it spans.</summary>
    public IReadOnlyList<ActivityEntry> Read(DateTime from, DateTime to)
    {
        var entries = new List<ActivityEntry>();
        for (DateTime day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            string file = FileFor(day);
            string[] lines;
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(file)) continue;
                    lines = File.ReadAllLines(file);
                }
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    ActivityEntry? entry = JsonSerializer.Deserialize<ActivityEntry>(line, Options);
                    if (entry is not null && entry.End > from && entry.Start < to) entries.Add(entry);
                }
                catch (JsonException)
                {
                    // A line the user edited by hand into something unreadable: skip it.
                }
            }
        }
        return entries;
    }

    string FileFor(DateTime day) => Path.Combine(folder, $"{day:yyyy-MM-dd}.jsonl");
}
