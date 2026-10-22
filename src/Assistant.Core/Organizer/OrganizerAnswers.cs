using System.Globalization;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Organizer;

/// <summary>An answer about notes, to-dos or reminders. Notes are the green lines under it; ShowTodos adds the checklist card.</summary>
public sealed record OrganizerReply(string Text, IReadOnlyList<string> Notes, bool ShowTodos = false);

/// <summary>
/// Notes, the to-do list and reminders, in English and Bulgarian, answered by code before any model: instant, works in
/// Lite and game mode, and nothing can be made up. These requests are private like memory: the assistant keeps them out
/// of everything plain chat may send to the cloud.
/// </summary>
public sealed partial class OrganizerAnswers(NotesStore notes, TodoStore todos, IReminders reminders)
{
    const int MaxListed = 15;
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>For the checklist card, which ticks boxes directly.</summary>
    public TodoStore Todos => todos;

    public OrganizerReply? TryAnswer(string message, DateTime now)
    {
        string text = Spaces().Replace(message.Trim().TrimEnd('.', '!').Trim(), " ");
        if (text.Length == 0 || text.Length > 500) return null;
        try
        {
            return NoteRequest(text, now) ?? TodoRequest(text) ?? ReminderRequest(text, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Notes, to-do or reminders file failed", ex);
            return new OrganizerReply($"I couldn't read or save that file: {ex.Message}", []);
        }
    }

    // ---- Notes ---------------------------------------------------------------------------------------------------

    OrganizerReply? NoteRequest(string text, DateTime now)
    {
        if (First(text, AddNote) is { } added)
        {
            string content = added.Groups["t"].Value.Trim();
            if (content.Length == 0) return null;
            notes.Add(content, now);
            return new OrganizerReply("Noted.", [$"Saved to notes.md: {NotesStore.OneLine(content)}"]);
        }

        if (First(text, FindNotes) is { } search)
        {
            string query = search.Groups["q"].Value.Trim();
            IReadOnlyList<Note> found = notes.Find(query);
            return new OrganizerReply(found.Count == 0 ? $"No notes about \"{query}\"." : $"Notes about \"{query}\":\n" + Bullets(found), []);
        }

        if (First(text, ShowNotes) is not null)
        {
            IReadOnlyList<Note> all = notes.All();
            return new OrganizerReply(all.Count == 0
                ? "You have no notes yet. Say \"note: …\" to add one."
                : "Your notes, newest first:\n" + Bullets(all), []);
        }

        if (First(text, DeleteNote) is { } delete)
        {
            string query = delete.Groups["q"].Value.Trim();
            IReadOnlyList<Note> matched = notes.Remove(query);
            return matched.Count switch
            {
                0 => new OrganizerReply($"I didn't find a note about \"{query}\".", []),
                1 => new OrganizerReply("Deleted that note.", [$"Removed from notes.md: {matched[0].Text}"]),
                _ => new OrganizerReply("Several notes match. Say a few more of its words:\n" + Bullets(matched), []),
            };
        }
        return null;
    }

    static string Bullets(IReadOnlyList<Note> list)
    {
        string lines = string.Join("\n", list.Take(MaxListed).Select(note =>
            note.Written == DateTime.MinValue ? $"• {note.Text}" : $"• {note.Written.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)} · {note.Text}"));
        return list.Count > MaxListed ? lines + $"\n…and {list.Count - MaxListed} more in notes.md." : lines;
    }

    // ---- To-do ---------------------------------------------------------------------------------------------------

    OrganizerReply? TodoRequest(string text)
    {
        // Showing first, so "todo list" or "my list" is never added as a task.
        if (First(text, ShowTodo) is not null)
        {
            IReadOnlyList<TodoItem> items = todos.Items();
            int open = items.Count(item => !item.Done);
            string summary = items.Count == 0
                ? "Your to-do list is empty. Say \"add … to my list\" to put something on it."
                : open == 0 ? "Everything on your to-do list is done." : open == 1 ? "1 open task:" : $"{open} open tasks:";
            return new OrganizerReply(summary, [], ShowTodos: items.Count > 0);
        }

        if (First(text, AddTodo) is { } added)
        {
            string task = added.Groups["t"].Value.Trim();
            if (task.Length == 0) return null;
            TodoItem item = todos.Add(task);
            return new OrganizerReply($"Added to your to-do list: {item.Text}", [], ShowTodos: true);
        }

        if (First(text, ClearDone) is not null)
        {
            int removed = todos.ClearDone();
            return new OrganizerReply(removed switch
            {
                0 => "There are no finished tasks to clear.",
                1 => "Cleared 1 finished task.",
                _ => $"Cleared {removed} finished tasks.",
            }, [], ShowTodos: true);
        }

        if (First(text, RemoveTodo) is { } remove)
        {
            string query = remove.Groups["q"].Value.Trim();
            IReadOnlyList<TodoItem> matched = todos.Remove(query);
            return matched.Count switch
            {
                0 => new OrganizerReply($"\"{query}\" isn't on your to-do list.", []),
                1 => new OrganizerReply($"Removed from your to-do list: {matched[0].Text}", [], ShowTodos: true),
                _ => new OrganizerReply("Several tasks match. Say a few more of its words.", [], ShowTodos: true),
            };
        }

        // "done: buy milk" always answers; "I finished my homework" only when a task matches (otherwise it's just chat).
        Match? done = First(text, MarkDone);
        bool certain = done is not null;
        done ??= First(text, MaybeDone);
        if (done is not null)
        {
            string query = done.Groups["q"].Value.Trim();
            IReadOnlyList<TodoItem> matched = todos.SetDone(query, true);
            if (matched.Count == 0 && !certain) return null;
            return matched.Count switch
            {
                0 => new OrganizerReply($"I couldn't find \"{query}\" among your open tasks.", [], ShowTodos: true),
                1 => new OrganizerReply($"Done: {matched[0].Text} ✓", [], ShowTodos: true),
                _ => new OrganizerReply("Several tasks match. Say a few more of its words, or tick it below.", [], ShowTodos: true),
            };
        }

        return null;
    }

    // ---- Reminders -----------------------------------------------------------------------------------------------

    OrganizerReply? ReminderRequest(string text, DateTime now)
    {
        if (First(text, ListReminders) is not null)
        {
            var waiting = reminders.Upcoming;
            if (waiting.Count == 0) return new OrganizerReply("You have no reminders or timers waiting.", []);
            return new OrganizerReply("Waiting:\n" + string.Join("\n", waiting.Select(entry =>
                $"• {Describe(entry.Due, now)} · {(entry.Label.Length > 0 ? entry.Label : entry.Timer ? "timer" : "reminder")}")), []);
        }

        if (First(text, CancelAllReminders) is not null)
        {
            int cancelled = reminders.CancelAll();
            return new OrganizerReply(cancelled == 0 ? "Nothing was waiting." : $"Cancelled {cancelled}.", []);
        }

        if (First(text, CancelReminder) is { } cancel)
        {
            string query = cancel.Groups["q"].Value.Trim();
            IReadOnlyList<string> matched = reminders.Cancel(query);
            return matched.Count switch
            {
                0 => new OrganizerReply($"I didn't find a reminder about \"{query}\".", []),
                1 => new OrganizerReply($"Cancelled the reminder: {matched[0]}", []),
                _ => new OrganizerReply("Several reminders match. Say a few more of its words.", []),
            };
        }

        // "remind me tomorrow at 9 to call the bank" / "напомни ми утре в 9 да се обадя в банката"
        if (First(text, RemindWhenWhat) is { } whenFirst)
            return SetReminder(whenFirst.Groups["when"].Value, whenFirst.Groups["what"].Value, now);

        // "remind me to call the bank tomorrow at 9": try each time word as the start of "when".
        if (First(text, RemindWhatWhen) is { } whatFirst)
        {
            string rest = whatFirst.Groups["rest"].Value;
            foreach (Match start in TimeWord().Matches(rest))
            {
                if (start.Index == 0) continue;
                if (WhenParser.Parse(rest[start.Index..], now) is { } due)
                    return SetReminder(due, rest[..start.Index].Trim(), now);
            }
            return new OrganizerReply(NotUnderstood, []);
        }

        // "remind me at 9" with nothing to remind about
        if (First(text, RemindWhenOnly) is { } whenOnly)
            return SetReminder(whenOnly.Groups["when"].Value, "", now);
        return null;
    }

    const string NotUnderstood =
        "I didn't understand when. Try \"at 18:00\", \"tomorrow at 9\", \"on Friday at 10\" or \"in 2 hours\".";

    OrganizerReply SetReminder(string when, string what, DateTime now) =>
        WhenParser.Parse(when, now) is { } due ? SetReminder(due, what, now) : new OrganizerReply(NotUnderstood, []);

    OrganizerReply SetReminder(DateTime due, string what, DateTime now)
    {
        what = NotesStore.OneLine(what);
        if (due <= now) return new OrganizerReply("That time has already passed. Say another one, for example \"tomorrow at 9\".", []);
        if (!reminders.At(due, what)) return new OrganizerReply("That's a lot of reminders already. Cancel some first.", []);
        string when = Describe(due, now);
        // "…: call the bank" reads right in English and in Bulgarian ("…: да се обадя в банката").
        return new OrganizerReply(what.Length > 0 ? $"OK, I'll remind you {when}: {what}." : $"OK, I'll remind you {when}.", []);
    }

    /// <summary>"today at 18:00", "tomorrow at 09:00", "on Friday at 10:00", "on Mon 3 Nov at 09:00".</summary>
    public static string Describe(DateTime due, DateTime now)
    {
        string time = due.ToString("HH:mm", CultureInfo.InvariantCulture);
        int days = (due.Date - now.Date).Days;
        return days switch
        {
            0 => $"today at {time}",
            1 => $"tomorrow at {time}",
            > 1 and < 7 => $"on {due.ToString("dddd", CultureInfo.InvariantCulture)} at {time}",
            _ => $"on {due.ToString("ddd d MMM", CultureInfo.InvariantCulture)} at {time}",
        };
    }

    static Match? First(string text, Regex[] patterns)
    {
        foreach (Regex pattern in patterns)
        {
            Match match = pattern.Match(text);
            if (match.Success) return match;
        }
        return null;
    }

    // ---- Phrases -------------------------------------------------------------------------------------------------

    // "to-do", "todo list", "tasks", or just "list" ("add milk to my list").
    const string TodoWord = @"(?:(?:to-?\s?dos?|todos?|tasks?)(?:\s+list)?|list)";
    const string BgList = @"(?:задачите|списъка(?:\s+(?:със|с)\s+задачи)?|нещата\s+за\s+правене)";
    const string Polite = @"(?:(?:please|can\s+you|could\s+you)\s+)?";

    static readonly Regex[] AddNote =
    [
        new(@"^(?:(?:please|can\s+you|could\s+you)\s+)?(?:make|take|write|jot)\s+(?:a\s+)?note(?:\s+that|\s+of)?\s*[:,-]?\s+(?<t>.+)$", Options),
        new(@"^(?:(?:please|can\s+you|could\s+you)\s+)?(?:note|write\s+down|jot\s+down)(?:\s+that)?\s*[:,-]?\s+(?<t>.+)$", Options),
        new(@"^(?:бележка|запиши(?:\s+си)?|отбележи(?:\s+си)?|направи\s+бележка)(?:\s+че)?\s*[:,-]?\s+(?<t>.+)$", Options),
    ];

    static readonly Regex[] FindNotes =
    [
        new(@"^(?:(?:show|find|search|any)\s+)?(?:me\s+)?(?:my\s+|the\s+)?notes?\s+(?:about|on|with|for|mentioning)\s+(?<q>.+?)\??$", Options),
        new(@"^search\s+(?:my\s+)?notes\s+for\s+(?<q>.+?)\??$", Options),
        new(@"^(?:(?:покажи(?:\s+ми)?|намери)\s+)?бележки(?:те)?(?:\s+ми)?\s+за\s+(?<q>.+?)\??$", Options),
    ];

    static readonly Regex[] ShowNotes =
    [
        new(@"^(?:(?:show|list|read|open|see)\s+)?(?:me\s+)?(?:all\s+)?(?:my\s+|the\s+)?notes\??$", Options),
        new(@"^what\s+(?:are|were)\s+my\s+notes\??$", Options),
        new(@"^(?:покажи(?:\s+ми)?\s+)?(?:всички\s+)?бележките(?:\s+ми)?\??$", Options),
        new(@"^(?:какви\s+)?бележки\s+имам\??$", Options),
    ];

    static readonly Regex[] DeleteNote =
    [
        new(@"^(?:delete|remove|erase)\s+(?:the\s+|my\s+)?note\s+(?:about|on|with|that\s+says|saying)\s+(?<q>.+)$", Options),
        new(@"^(?:изтрий|премахни|махни)\s+бележка(?:та)?\s+(?:за\s+)?(?<q>.+)$", Options),
    ];

    static readonly Regex[] AddTodo =
    [
        new($@"^{Polite}(?:add|put|write|stick)\s+(?<t>.+?)\s+(?:to|on|in|onto|into)\s+(?:my\s+|the\s+)?{TodoWord}$", Options),
        new($@"^{Polite}(?:add|new|create)\s+(?:a\s+)?(?:task|to-?\s?do|todo)\s*:?\s+(?<t>.+)$", Options),
        new(@"^(?:to-?\s?do|todo|task)\s*:?\s+(?<t>.+)$", Options),
        new($@"^(?:добави|сложи|запиши|пиши)\s+(?<t>.+?)\s+(?:в|към)\s+{BgList}(?:\s+ми)?$", Options),
        new(@"^(?:(?:добави|нова)\s+задача\s*:?|задача\s*:)\s*(?<t>.+)$", Options),
    ];

    static readonly Regex[] ClearDone =
    [
        new(@"^(?:clear|remove|delete)\s+(?:all\s+)?(?:the\s+)?(?:done|finished|completed|ticked|checked)(?:\s+(?:tasks|items|to-?\s?dos|todos))?$", Options),
        new(@"^(?:изчисти|изтрий|махни)\s+(?:всички\s+)?(?:готовите|свършените|отметнатите)(?:\s+задачи)?$", Options),
    ];

    static readonly Regex[] RemoveTodo =
    [
        new($@"^(?:remove|delete|take)\s+(?<q>.+?)\s+(?:from|off)\s+(?:my\s+|the\s+)?{TodoWord}$", Options),
        new($@"^(?:махни|изтрий|премахни)\s+(?<q>.+?)\s+от\s+{BgList}(?:\s+ми)?$", Options),
    ];

    static readonly Regex[] MarkDone =
    [
        new(@"^(?:done|finished|completed?)\s*[:,-]\s*(?<q>.+)$", Options),
        new(@"^(?:mark|tick|check)(?:\s+off)?\s+(?<q>.+?)\s+(?:as\s+)?(?:done|complete|completed|finished)$", Options),
        new($@"^(?:tick|check|cross)\s+off\s+(?<q>.+?)(?:\s+(?:from|on)\s+(?:my\s+|the\s+)?{TodoWord})?$", Options),
        new($@"^(?:tick|cross)\s+(?<q>.+?)\s+off(?:\s+(?:my\s+|the\s+)?{TodoWord})?$", Options),
        new(@"^(?:готово|отметни|зачеркни)\s*[:,-]?\s+(?<q>.+)$", Options),
    ];

    static readonly Regex[] MaybeDone =
    [
        new(@"^(?:i\s+(?:did|finished|completed)|i(?:'ve|\s+have)\s+(?:done|finished|completed)|finished|completed)\s+(?<q>.+)$", Options),
        new(@"^i(?:'m|\s+am)\s+(?:done|finished)\s+with\s+(?<q>.+)$", Options),
        new(@"^(?<q>.+?)\s+is\s+(?:done|finished|completed)$", Options),
        new(@"^(?:свърших|направих|приключих|завърших)(?:\s+с)?\s+(?<q>.+)$", Options),
        new(@"^(?<q>.+?)\s+(?:е|са)\s+(?:готово|готова|готов|готови|свършено|свършена|направено|направена)$", Options),
    ];

    static readonly Regex[] ShowTodo =
    [
        new($@"^(?:(?:show|list|open|see|read|check)\s+)?(?:me\s+)?(?:all\s+)?(?:my\s+|the\s+)?{TodoWord}\??$", Options),
        new($@"^what(?:'s|\s+is|\s+are|s)?\s+(?:on|in)\s+(?:my|the)\s+{TodoWord}\??$", Options),
        new($@"^what\s+are\s+my\s+{TodoWord}\??$", Options),
        new(@"^what\s+(?:else\s+)?do\s+i\s+(?:still\s+)?(?:have|need|got)\s+to\s+do(?:\s+today)?\??$", Options),
        new(@"^what(?:'s|\s+is|s)\s+(?:left|next|still\s+open)(?:\s+to\s+do)?(?:\s+on\s+my\s+list)?\??$", Options),
        new(@"^(?:do\s+i\s+have|is\s+there|have\s+i\s+got)\s+(?:anything|something)\s+(?:left\s+)?to\s+do\??$", Options),
        new(@"^anything\s+(?:left\s+)?to\s+do\??$", Options),
        new($@"^(?:покажи(?:\s+ми)?\s+)?{BgList}(?:\s+ми)?\??$", Options),
        new(@"^(?:моите\s+задачи|списъкът\s+ми|задачите\s+ми)\??$", Options),
        new(@"^какви\s+(?:са\s+)?(?:ми\s+)?задачите(?:\s+ми)?\??$", Options),
        new(@"^какво\s+(?:още\s+)?(?:имам|трябва|ми\s+остава)(?:\s+(?:да\s+(?:правя|свърша|направя)|за\s+правене))?(?:\s+днес)?\??$", Options),
        new(@"^какво\s+има\s+в\s+(?:списъка|задачите)(?:\s+ми)?\??$", Options),
    ];

    static readonly Regex[] ListReminders =
    [
        new(@"^(?:(?:show|list)\s+(?:me\s+)?)?(?:all\s+)?(?:my\s+|the\s+)?reminders\??$", Options),
        new(@"^what\s+(?:are\s+my\s+reminders|reminders\s+do\s+i\s+have)\??$", Options),
        new(@"^(?:покажи(?:\s+ми)?\s+)?напомнянията(?:\s+ми)?\??$", Options),
        new(@"^какви\s+напомняния\s+имам\??$", Options),
    ];

    static readonly Regex[] CancelAllReminders =
    [
        new(@"^(?:отмени|изтрий|махни)\s+(?:всички\s+)?напомнянията(?:\s+ми)?$", Options),
    ];

    static readonly Regex[] CancelReminder =
    [
        new(@"^(?:cancel|delete|remove|stop)\s+(?:the\s+|my\s+)?reminder\s+(?:about|to|for)\s+(?<q>.+)$", Options),
        new(@"^(?:отмени|изтрий|махни)\s+напомнянето\s+(?:за|да)\s+(?<q>.+)$", Options),
    ];

    static readonly Regex[] RemindWhenWhat =
    [
        new(@"^(?:please\s+)?remind\s+me\s+(?!to\s)(?<when>.+?)\s+to\s+(?<what>.+)$", Options),
        new(@"^напомни\s+ми\s+(?!да\s)(?<when>.+?)\s+да\s+(?<what>.+)$", Options),
    ];

    static readonly Regex[] RemindWhatWhen =
    [
        new(@"^(?:please\s+)?remind\s+me\s+to\s+(?<rest>.+)$", Options),
        new(@"^напомни\s+ми\s+да\s+(?<rest>.+)$", Options),
    ];

    static readonly Regex[] RemindWhenOnly =
    [
        new(@"^(?:please\s+)?remind\s+me\s+(?<when>(?:at|on|in|tomorrow|today|tonight)\b.+|tomorrow|tonight)$", Options),
        new(@"^напомни\s+ми\s+(?<when>(?:в|във|утре|днес|довечера|след|на)\s.+|утре|довечера)$", Options),
    ];

    // Where "when" may start inside "call the bank tomorrow at 9".
    [GeneratedRegex(@"(?<=\s)(?:at|on|in|tomorrow|today|tonight|this|next|monday|tuesday|wednesday|thursday|friday|saturday|sunday|в|във|утре|днес|довечера|вдругиден|след|на|понеделник|вторник|сряда|четвъртък|петък|събота|неделя)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
