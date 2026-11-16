using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Actions;
using Assistant.Core.Storage;

namespace Assistant.Core.Organizer;

/// <summary>
/// Something to ask the assistant by itself at a set time: "every morning at 8: what's on my calendar today".
/// Days: the weekdays it runs on (empty: every day). LastRun: the day it last ran (yyyy-MM-dd), so it runs once a day.
/// </summary>
public sealed record Routine(int Hour, int Minute, List<DayOfWeek> Days, string Prompt, string LastRun = "")
{
    public string TimeText => $"{Hour:00}:{Minute:00}";

    public string DaysText => Days.Count switch
    {
        0 or 7 => "every day",
        5 when !Days.Contains(DayOfWeek.Saturday) && !Days.Contains(DayOfWeek.Sunday) => "every weekday",
        _ => "every " + string.Join(", ", Days.OrderBy(day => ((int)day + 6) % 7)),
    };
}

/// <summary>
/// Routines, kept in routines.json. A routine is due once its time has passed today on one of its days and it hasn't
/// run today: so a morning routine still runs when the PC is switched on at ten, and yesterday's is never caught up.
/// </summary>
public sealed class RoutineStore(string file)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly Lock gate = new();

    public IReadOnlyList<Routine> All()
    {
        lock (gate) return Load();
    }

    /// <summary>Adds one. If its time has already passed today it starts tomorrow, not this minute.</summary>
    public Routine Add(Routine routine, DateTime now)
    {
        lock (gate)
        {
            List<Routine> all = Load();
            bool passed = now.TimeOfDay >= new TimeSpan(routine.Hour, routine.Minute, 0);
            Routine added = routine with { LastRun = passed ? Day(now) : "" };
            all.Add(added);
            Save(all);
            return added;
        }
    }

    /// <summary>Removes by number (1 is the first in the list) or by words in the prompt. Null when nothing matches.</summary>
    public Routine? Remove(string which)
    {
        lock (gate)
        {
            List<Routine> all = Load();
            int index = int.TryParse(which.Trim().TrimStart('#'), out int number) ? number - 1
                : all.FindIndex(routine => routine.Prompt.Contains(which.Trim(), StringComparison.OrdinalIgnoreCase) || routine.TimeText == which.Trim());
            if (index < 0 || index >= all.Count) return null;
            Routine removed = all[index];
            all.RemoveAt(index);
            Save(all);
            return removed;
        }
    }

    /// <summary>The routines to run now, each marked as run today so it isn't returned again.</summary>
    public IReadOnlyList<Routine> TakeDue(DateTime now)
    {
        lock (gate)
        {
            List<Routine> all = Load();
            var due = new List<Routine>();
            for (int i = 0; i < all.Count; i++)
            {
                Routine routine = all[i];
                bool today = routine.Days.Count == 0 || routine.Days.Contains(now.DayOfWeek);
                if (!today || routine.LastRun == Day(now) || now.TimeOfDay < new TimeSpan(routine.Hour, routine.Minute, 0)) continue;
                all[i] = routine with { LastRun = Day(now) };
                due.Add(routine);
            }
            if (due.Count > 0) Save(all);
            return due;
        }
    }

    static string Day(DateTime now) => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    List<Routine> Load()
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<Routine>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    void Save(List<Routine> all) => SafeFile.WriteAllText(file, JsonSerializer.Serialize(all, Json));
}

/// <summary>
/// "Every morning at 8 tell me what's on my calendar", "every weekday at 9: open spotify", "my routines", "delete
/// routine 2", and the Bulgarian "всеки ден в 8 кажи ми какво имам днес", "моите рутини", "изтрий рутина 2".
/// </summary>
public static partial class RoutineAnswers
{
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string Time = @"(?:at|around)\s+(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?\s*(?<ap>am|pm|a\.m\.|p\.m\.)?(?:\s+o'?clock)?";
    const string TimeBg = @"(?:в|във|от|към)\s+(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?(?:\s*(?:часа|ч\.?))?";
    const string WeekdayEn = @"monday|tuesday|wednesday|thursday|friday|saturday|sunday";
    const string WeekdayBg = @"понеделник|вторник|сряда|четвъртък|петък|събота|неделя";

    static readonly Regex[] AddRules =
    [
        new($@"^(?:every|each)\s+(?<d>day|morning|evening|night|weekday|work\s?day|{WeekdayEn})(?:\s+{Time})?\s*[:,]?\s+(?<p>.+)$", Options),
        new($@"^(?<d>daily|every\s+day)\s+{Time}\s*[:,]?\s+(?<p>.+)$", Options),
        new($@"^(?:всеки|всяка|всяко)\s+(?<d>ден|сутрин|вечер|делничен\s+ден|работен\s+ден|{WeekdayBg})(?:\s+{TimeBg})?\s*[:,]?\s+(?<p>.+)$", Options),
    ];

    static readonly Regex[] ListRules =
    [
        new(@"^(?:(?:show|list)\s+(?:me\s+)?)?(?:my\s+|the\s+)?(?:routines|scheduled\s+prompts|scheduled\s+questions)$|^what(?:'s|’s|\s+is)\s+scheduled$", Options),
        new(@"^(?:(?:покажи|изброй)\s+(?:ми\s+)?)?(?:моите\s+|мойте\s+)?рутини(?:те)?(?:\s+ми)?$|^какво\s+е\s+планирано$", Options),
    ];

    static readonly Regex[] RemoveRules =
    [
        new(@"^(?:delete|remove|stop|cancel)\s+(?:the\s+|my\s+)?routine\s+(?:number\s+|#|about\s+|for\s+|at\s+)?(?<w>.+)$", Options),
        new(@"^(?:изтрий|махни|спри|премахни)\s+рутина(?:та)?\s+(?:номер\s+|#|за\s+|в\s+)?(?<w>.+)$", Options),
    ];

    /// <summary>The reply when the message is about routines, or null. A new routine is saved here.</summary>
    public static string? TryAnswer(string message, RoutineStore store, DateTime now)
    {
        string text = ActionParser.Courtesy(Spaces().Replace(message.Trim(), " "));
        if (text.Length == 0 || text.Length > 300) return null;

        if (ListRules.Any(rule => rule.IsMatch(text)))
        {
            IReadOnlyList<Routine> all = store.All();
            if (all.Count == 0) return "No routines yet. Try: \"every morning at 8 tell me what's on my calendar today\".";
            return "Your routines:\n" + string.Join("\n", all.Select((routine, index) => $"{index + 1}. {routine.DaysText} at {routine.TimeText}: {routine.Prompt}"))
                   + "\n\n\"Delete routine 1\" removes one.";
        }

        foreach (Regex rule in RemoveRules)
        {
            if (rule.Match(text) is not { Success: true } remove) continue;
            Routine? gone = store.Remove(remove.Groups["w"].Value);
            return gone is null ? "I don't have a routine like that. \"My routines\" lists them." : $"Removed: {gone.DaysText} at {gone.TimeText}, \"{gone.Prompt}\".";
        }

        foreach (Regex rule in AddRules)
        {
            if (rule.Match(text) is not { Success: true } add) continue;
            if (Parse(add) is not { } routine) return null;
            Routine saved = store.Add(routine, now);
            string first = saved.LastRun.Length > 0 ? "starting tomorrow" : "starting today";
            return $"OK: {saved.DaysText} at {saved.TimeText} I'll ask myself \"{saved.Prompt}\" and show you the answer ({first}). " +
                   "If the PC is off at that time, it runs when you're back the same day. \"My routines\" lists them.";
        }
        return null;
    }

    static Routine? Parse(Match add)
    {
        string day = Spaces().Replace(add.Groups["d"].Value.ToLowerInvariant(), " ");
        string prompt = Lead().Replace(add.Groups["p"].Value.Trim(), "").Trim();
        if (prompt.Length < 3) return null;

        // No time said: the morning is 8:00, the evening 19:00, and a plain "every day" needs one.
        int hour, minute = 0;
        if (add.Groups["h"].Success)
        {
            hour = int.Parse(add.Groups["h"].Value, CultureInfo.InvariantCulture);
            if (add.Groups["m"].Success) minute = int.Parse(add.Groups["m"].Value, CultureInfo.InvariantCulture);
            string half = add.Groups["ap"].Value.ToLowerInvariant().Replace(".", "");
            if (half == "pm" && hour < 12) hour += 12;
            if (half == "am" && hour == 12) hour = 0;
            if (half.Length == 0 && hour < 12 && day is "evening" or "night" or "вечер") hour += 12;
        }
        else if (day is "morning" or "сутрин") hour = 8;
        else if (day is "evening" or "night" or "вечер") hour = 19;
        else return null;
        if (hour > 23 || minute > 59) return null;

        List<DayOfWeek> days = day switch
        {
            "weekday" or "workday" or "work day" or "делничен ден" or "работен ден" =>
                [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            "monday" or "понеделник" => [DayOfWeek.Monday],
            "tuesday" or "вторник" => [DayOfWeek.Tuesday],
            "wednesday" or "сряда" => [DayOfWeek.Wednesday],
            "thursday" or "четвъртък" => [DayOfWeek.Thursday],
            "friday" or "петък" => [DayOfWeek.Friday],
            "saturday" or "събота" => [DayOfWeek.Saturday],
            "sunday" or "неделя" => [DayOfWeek.Sunday],
            _ => [],
        };
        return new Routine(hour, minute, days, prompt);
    }

    // "tell me", "show me", "кажи ми" in front of the prompt say nothing: "what's on my calendar" is what gets asked.
    [GeneratedRegex(@"^(?:please\s+)?(?:tell\s+me|show\s+me|give\s+me|remind\s+me\s+(?:of|about)|ask(?:\s+yourself)?|кажи\s+ми|покажи\s+ми|припомни\s+ми)\s*[:,]?\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Lead();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
