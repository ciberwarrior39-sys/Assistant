using System.Text;
using System.Text.RegularExpressions;
using Assistant.Core.Actions;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Agenda;

/// <summary>What was asked about the calendar: the days from From up to To, or just the next thing (Next).</summary>
public sealed record CalendarAsk(DateTime From, DateTime To, string Label, bool Next = false);

/// <summary>A button for "what's on my calendar", offered by cloud chat. Day: today, tomorrow, this week, next week, or next.</summary>
public sealed record CheckCalendar(string Day = "today") : ActionIntent;

/// <summary>Where the calendars come from: the app fetches the user's private iCal links.</summary>
public interface ICalendarSource
{
    /// <summary>At least one calendar link is set.</summary>
    bool Connected { get; }

    /// <summary>What's on between two moments. Throws HttpRequestException when a calendar can't be fetched.</summary>
    Task<IReadOnlyList<CalendarEvent>> BetweenAsync(DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>
/// "What's on my calendar today?", "what do I have tomorrow?", "when is my next meeting?", and the Bulgarian
/// "какво имам утре в календара?", "кога е следващата ми среща?": answered by code from the calendar, on this
/// computer. Titles and places never go to the cloud.
/// </summary>
public static partial class CalendarAnswers
{
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    const string Day = @"(?<d>today|tonight|tomorrow|this\s+week|next\s+week|(?:on\s+|this\s+|next\s+)?(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday))";
    const string Thing = @"(?:calendar|schedule|agenda|meetings?|events?|appointments?|plans)";
    const string DayBg = @"(?<d>днес|довечера|утре|тази\s+седмица|следващата\s+седмица|(?:в|във)\s+(?:понеделник|вторник|сряда|четвъртък|петък|събота|неделя))";
    const string ThingBg = @"(?:календар\w*|програма(?:та)?|график\w*|срещи|среща|събития|ангажименти|планове)";

    static readonly Regex[] Next =
    [
        new(@"^(?:when(?:'s|’s|\s+is)|what(?:'s|’s|\s+is))\s+my\s+next\s+(?:meeting|event|appointment|thing)(?:\s+on\s+(?:my\s+)?calendar)?$", Options),
        new(@"^(?:what(?:'s|’s|\s+is)\s+next|next)\s+on\s+my\s+(?:calendar|schedule|agenda)$", Options),
        new(@"^(?:кога\s+е|какв[оа]\s+е)\s+следващ\w+\s+(?:ми\s+)?(?:среща|събитие|ангажимент)$", Options),
    ];

    static readonly Regex[] Days =
    [
        new($@"^(?:what(?:'s|’s|\s+is)\s+(?:on\s+|in\s+)?|show\s+(?:me\s+)?|check\s+)?my\s+{Thing}(?:\s+(?:for|like))?(?:\s+{Day})?$", Options),
        new($@"^what(?:'s|’s|\s+is)\s+on\s+(?:the\s+)?(?:calendar|schedule|agenda)(?:\s+(?:for\s+)?{Day})?$", Options),
        new($@"^what\s+do\s+i\s+have\s+(?:on\s+(?:my\s+)?(?:calendar|schedule|agenda)\s+)?(?:for\s+)?{Day}(?:\s+on\s+(?:my\s+)?(?:calendar|schedule|agenda))?$", Options),
        new($@"^what\s+do\s+i\s+have\s+on\s+(?:my\s+)?(?:calendar|schedule|agenda)$", Options),
        new($@"^(?:is\s+there\s+|do\s+i\s+have\s+)?any(?:thing)?\s+(?:on|in)\s+(?:my\s+|the\s+)?(?:calendar|schedule|agenda)(?:\s+(?:for\s+)?{Day})?$", Options),
        new($@"^what(?:'s|’s|\s+is)\s+in\s+(?:my\s+|the\s+)?(?:calendar|schedule|agenda)(?:\s+(?:for\s+)?{Day})?$", Options),
        new($@"^(?:какво\s+има|има\s+ли\s+нещо)\s+(?:в|на)\s+(?:календара|програмата)(?:\s+ми)?(?:\s+(?:за\s+)?{DayBg})?$", Options),
        new($@"^(?:do\s+i\s+have\s+)?any(?:thing)?\s+(?:{Thing}\s+)?(?:on\s+(?:my\s+)?(?:calendar|schedule)\s+)?(?:for\s+)?{Day}$", Options),
        new($@"^(?:am\s+i|are\s+we)\s+(?:free|busy)\s+{Day}$", Options),
        new($@"^(?:какво\s+имам|какво\s+има|имам\s+ли\s+(?:нещо|срещи|ангажименти))\s+(?:(?:в|на|по)\s+{ThingBg}\s+(?:ми\s+)?)?{DayBg}(?:\s+(?:в|на|по)\s+{ThingBg}(?:\s+ми)?)?$", Options),
        new($@"^(?:какво\s+имам\s+(?:в|на|по)\s+{ThingBg}(?:\s+ми)?|(?:покажи\s+(?:ми\s+)?)?{ThingBg}\s+ми|моят\s+календар|моя\s+календар)(?:\s+(?:за\s+)?{DayBg})?$", Options),
        new($@"^(?:какви|имам\s+ли)\s+(?:срещи|събития|ангажименти)\s+(?:имам\s+)?{DayBg}$", Options),
        new($@"^свобод(?:ен|на)\s+ли\s+съм\s+{DayBg}$", Options),
    ];

    /// <summary>The days a button means ("today", "tomorrow", "this week", "next week", "next").</summary>
    public static CalendarAsk ForButton(string day, DateTime now) =>
        day.Trim().ToLowerInvariant() == "next" ? new CalendarAsk(now, now.Date.AddDays(60), "next", Next: true) : Range(day.Trim().ToLowerInvariant(), now);

    public static CalendarAsk? Parse(string message, DateTime now)
    {
        string text = ActionParser.Courtesy(Spaces().Replace(message.Trim(), " "));
        if (text.Length == 0 || text.Length > 120) return null;
        if (Next.Any(rule => rule.IsMatch(text))) return new CalendarAsk(now, now.Date.AddDays(60), "next", Next: true);
        foreach (Regex rule in Days)
        {
            if (rule.Match(text) is { Success: true } match) return Range(match.Groups["d"].Value.ToLowerInvariant(), now);
        }
        return null;
    }

    static CalendarAsk Range(string day, DateTime now)
    {
        DateTime today = now.Date;
        string word = Spaces().Replace(day, " ").Trim();
        if (word.Length == 0 || word is "today" or "tonight" or "днес" or "довечера") return new CalendarAsk(today, today.AddDays(1), "today");
        if (word is "tomorrow" or "утре") return new CalendarAsk(today.AddDays(1), today.AddDays(2), "tomorrow");

        // Weeks run Monday to Sunday.
        DateTime monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (word is "this week" or "тази седмица") return new CalendarAsk(today, monday.AddDays(7), "the rest of this week");
        if (word is "next week" or "следващата седмица") return new CalendarAsk(monday.AddDays(7), monday.AddDays(14), "next week");

        string name = word.Split(' ')[^1];
        int weekday = Array.IndexOf(WeekdayNames, name) % 7;
        if (weekday < 0) return new CalendarAsk(today, today.AddDays(1), "today");
        int ahead = (weekday - (int)today.DayOfWeek + 7) % 7;
        if (ahead == 0) ahead = 7; // "on Monday", asked on a Monday: the coming one (today is "today")
        DateTime date = today.AddDays(ahead);
        return new CalendarAsk(date, date.AddDays(1), $"{date:dddd, d MMMM}");
    }

    // Sunday first, like DayOfWeek; English then Bulgarian.
    static readonly string[] WeekdayNames =
    [
        "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday",
        "неделя", "понеделник", "вторник", "сряда", "четвъртък", "петък", "събота",
    ];

    /// <summary>The answer in words. Never logged; a count is.</summary>
    public static async Task<string> AnswerAsync(CalendarAsk ask, ICalendarSource source, DateTime now, CancellationToken ct)
    {
        if (!source.Connected)
            return "No calendar is connected yet. In Settings > Calendar and mail, paste your calendar's private iCal link " +
                   "(Google Calendar: Settings > your calendar > \"Secret address in iCal format\").";
        IReadOnlyList<CalendarEvent> events;
        try
        {
            events = await source.BetweenAsync(ask.From, ask.To, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status)
        {
            // The server answered, but not with a calendar. The status says why; the link itself is never logged.
            DiagnosticLog.Info($"Calendar couldn't be fetched (the server answered {(int)status})");
            return status == System.Net.HttpStatusCode.UnsupportedMediaType
                ? "That link opens a web page, not a calendar file. In Google Calendar, use \"Secret address in iCal format\" " +
                  "(Settings > your calendar > Integrate calendar); it ends in basic.ics."
                : $"Google didn't accept that calendar link (error {(int)status}). Use \"Secret address in iCal format\", not the " +
                  "public address: Google Calendar > Settings > your calendar > Integrate calendar. It ends in private-…/basic.ics.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            DiagnosticLog.Info($"Calendar couldn't be fetched ({ex.GetType().Name})"); // never the link: it is a key to the calendar
            return "I couldn't fetch your calendar just now. Check the internet connection and the link in Settings > Calendar and mail.";
        }
        DiagnosticLog.Info($"Calendar: {events.Count} event(s) in the days asked about");

        if (ask.Next)
        {
            CalendarEvent? next = events.Where(item => !item.AllDay && item.Start > now).FirstOrDefault();
            return next is null
                ? "Nothing with a time is coming up on your calendar in the next two months."
                : $"Next: {next.Title}, {When(next.Start, now)}{(next.Location.Length > 0 ? $" ({next.Location})" : "")}.";
        }

        if (events.Count == 0) return $"Nothing on your calendar for {ask.Label}.";
        bool severalDays = ask.To - ask.From > TimeSpan.FromDays(1);
        var text = new StringBuilder($"On your calendar for {ask.Label}:");
        DateTime shown = DateTime.MinValue;
        foreach (CalendarEvent item in events.Take(30))
        {
            DateTime day = item.Start < ask.From ? ask.From.Date : item.Start.Date;
            if (severalDays && day != shown)
            {
                text.Append($"\n{day:dddd, d MMMM}");
                shown = day;
            }
            string time = item.AllDay || item.Start < ask.From ? "all day" : item.End > item.Start ? $"{item.Start:HH:mm}–{item.End:HH:mm}" : $"{item.Start:HH:mm}";
            text.Append($"\n- {time}: {item.Title}{(item.Location.Length > 0 ? $" ({item.Location})" : "")}");
        }
        if (events.Count > 30) text.Append($"\n…and {events.Count - 30} more.");
        return text.ToString();
    }

    static string When(DateTime start, DateTime now) =>
        start.Date == now.Date ? $"today at {start:HH:mm}"
        : start.Date == now.Date.AddDays(1) ? $"tomorrow at {start:HH:mm}"
        : $"{start:dddd, d MMMM} at {start:HH:mm}";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// Fetches the user's private iCal links (HTTPS only) and keeps each answer for ten minutes, so asking twice doesn't
/// fetch twice. The links are keys to the calendar: they are never logged.
/// </summary>
public sealed class IcsCalendarSource(HttpClient http, Func<IReadOnlyList<string>> links) : ICalendarSource
{
    static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);
    readonly Dictionary<string, (DateTime At, string Ics)> kept = [];

    public bool Connected => Usable().Count > 0;

    List<string> Usable() => links()
        .Select(link => link.Trim())
        .Select(link => link.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + link[9..] : link)
        .Where(link => Uri.TryCreate(link, UriKind.Absolute, out Uri? address) && address.Scheme == Uri.UriSchemeHttps)
        .Distinct()
        .ToList();

    public async Task<IReadOnlyList<CalendarEvent>> BetweenAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var all = new List<CalendarEvent>();
        foreach (string link in Usable())
        {
            if (!kept.TryGetValue(link, out var have) || DateTime.UtcNow - have.At > KeepFor)
            {
                string text = await http.GetStringAsync(link, ct);
                // A sign-in page or the calendar's web page comes back as HTML, not as a calendar.
                if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("Not a calendar file", null, System.Net.HttpStatusCode.UnsupportedMediaType);
                have = (DateTime.UtcNow, text);
                kept[link] = have;
            }
            all.AddRange(IcsCalendar.Between(have.Ics, from, to));
        }
        return all.OrderBy(item => item.Start).ToList();
    }
}
