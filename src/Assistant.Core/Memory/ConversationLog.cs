using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Memory;

/// <summary>A few words about one past chat, written by the local model after the chat.</summary>
public sealed record ConversationNote(DateTime Time, string Text);

/// <summary>
/// conversations\yyyy-MM-dd.md: a line or two about each chat ("- 14:20 · Asked how to revise for the physics test"),
/// written on this computer by the local model, never sent to the cloud and deleted after 7 days like the other logs.
/// The local model gets the notes that match a new message, and "what did we talk about yesterday?" lists them.
/// </summary>
public sealed partial class ConversationLog(string folder)
{
    public const int KeepDays = 7;
    const int MaxNoteLength = 300;

    public string Folder => folder;

    public void Add(DateTime time, string text)
    {
        string note = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim().TrimStart('-', '*', '#', ' ');
        if (note.Length == 0) return;
        if (note.Length > MaxNoteLength) note = note[..MaxNoteLength] + "…";
        try
        {
            Directory.CreateDirectory(folder);
            string file = FileFor(time);
            string header = File.Exists(file) ? "" : $"# Conversations on {time:yyyy-MM-dd}\n\nWhat we talked about, one line per chat. Deleted after {KeepDays} days.\n\n";
            File.AppendAllText(file, header + $"- {time:HH:mm} · {note}\n", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Could not write the conversation notes", ex);
        }
    }

    /// <summary>The notes of the last 7 days, oldest first.</summary>
    public IReadOnlyList<ConversationNote> Recent(DateTime now)
    {
        var notes = new List<ConversationNote>();
        for (int back = KeepDays - 1; back >= 0; back--)
        {
            DateTime day = now.Date.AddDays(-back);
            string file = FileFor(day);
            if (!File.Exists(file)) continue;
            try
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    Match match = NoteLine().Match(line);
                    if (match.Success && TimeSpan.TryParseExact(match.Groups["time"].Value, @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan time))
                        notes.Add(new ConversationNote(day + time, match.Groups["text"].Value.Trim()));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Error("Could not read the conversation notes", ex);
            }
        }
        return notes;
    }

    /// <summary>"yesterday 14:20: Asked about …", the way the local model sees a note.</summary>
    public static string Describe(ConversationNote note, DateTime now) => $"{Day(note.Time, now)} {note.Time:HH:mm}: {note.Text}";

    /// <summary>"What did we talk about yesterday?" / "за какво говорихме вчера": the notes of that day, or null.</summary>
    public string? Answer(string message, DateTime now)
    {
        string text = message.ToLowerInvariant().Replace('’', '\'');
        if (!Question().IsMatch(text)) return null;

        IReadOnlyList<ConversationNote> notes = Recent(now);
        bool bulgarian = text.Any(c => c is >= 'а' and <= 'я');
        if (notes.Count == 0)
            return bulgarian
                ? "Нямам записано нищо от последните 7 дни. Записвам по ред-два след всеки разговор, когато затворите чата."
                : "I don't have anything from the last 7 days yet. I keep a line or two about each chat after you close the bubble.";

        DateTime? day = text.Contains("yesterday") || text.Contains("вчера") ? now.Date.AddDays(-1)
                      : text.Contains("today") || text.Contains("днес") ? now.Date
                      : null;
        // "Last time": the most recent day with notes.
        DateTime shown = day ?? notes[^1].Time.Date;
        var thatDay = notes.Where(note => note.Time.Date == shown).ToList();
        if (thatDay.Count == 0)
            return bulgarian ? $"Нямам нищо записано за {Day(shown, now, bulgarian)}." : $"I don't have anything from {Day(shown, now)}.";

        var reply = new StringBuilder(bulgarian ? $"{Capital(Day(shown, now, bulgarian))}:" : $"{Capital(Day(shown, now))}:");
        foreach (ConversationNote note in thatDay) reply.Append($"\n• {note.Time:HH:mm} {note.Text}");
        return reply.ToString();
    }

    string FileFor(DateTime day) => Path.Combine(folder, $"{day:yyyy-MM-dd}.md");

    static string Day(DateTime time, DateTime now, bool bulgarian = false)
    {
        int ago = (now.Date - time.Date).Days;
        if (bulgarian)
            return ago switch
            {
                0 => "днес",
                1 => "вчера",
                _ => time.ToString("dddd", new CultureInfo("bg-BG")),
            };
        return ago switch
        {
            0 => "today",
            1 => "yesterday",
            _ => time.ToString("dddd", CultureInfo.InvariantCulture),
        };
    }

    static string Capital(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    [GeneratedRegex(@"^- (?<time>\d{1,2}:\d{2}) · (?<text>.+)$")]
    private static partial Regex NoteLine();

    [GeneratedRegex(@"\bwhat (did|have) we (talk|talked|chat|chatted|been talking) about\b|\bwhat were we (talking|chatting) about\b|\bwhat did we (discuss|say)\b|\bwhat we (talked|chatted) about\b|за какво (си )?(говорихме|разговаряхме|си говорихме)|какво (обсъждахме|си казахме)")]
    private static partial Regex Question();
}
