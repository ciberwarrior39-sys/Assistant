using System.Globalization;
using System.Text;

namespace Assistant.Core.Agenda;

/// <summary>One thing on the calendar, in this computer's time.</summary>
public sealed record CalendarEvent(string Title, DateTime Start, DateTime End, bool AllDay, string Location = "");

/// <summary>
/// Reads a calendar in the iCalendar format (.ics), which Google Calendar, Outlook and most others hand out as a
/// private link. Understands timed and all-day events, time zones, and events that repeat daily, weekly, monthly or
/// yearly (with an interval, chosen weekdays, an end date or a count, and skipped dates). Not understood: single
/// occurrences of a repeating event that were moved or renamed (they show at their original time), and rarer rules
/// like "the last Friday of the month".
/// </summary>
public static class IcsCalendar
{
    sealed class Entry
    {
        public string Title = "";
        public string Location = "";
        public DateTime Start, End;
        public bool AllDay, Cancelled, HasEnd;
        public string Rule = "";
        public HashSet<DateTime> Skipped = [];
    }

    /// <summary>Everything in the calendar text that happens between two moments, earliest first.</summary>
    public static IReadOnlyList<CalendarEvent> Between(string ics, DateTime from, DateTime to)
    {
        var found = new List<CalendarEvent>();
        foreach (Entry entry in Entries(ics))
        {
            if (entry.Cancelled || entry.Start == default) continue;
            TimeSpan length = entry.HasEnd && entry.End > entry.Start ? entry.End - entry.Start : entry.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
            foreach (DateTime start in Starts(entry, from - length, to))
            {
                if (start >= to || start + length <= from && !(length == TimeSpan.Zero && start >= from)) continue;
                if (entry.Skipped.Contains(start)) continue;
                found.Add(new CalendarEvent(entry.Title.Length > 0 ? entry.Title : "(no title)", start, start + length, entry.AllDay, entry.Location));
            }
        }
        return found.OrderBy(item => item.Start).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The times an event starts within a window: once, or as often as its rule says.
    static IEnumerable<DateTime> Starts(Entry entry, DateTime from, DateTime to)
    {
        if (entry.Rule.Length == 0)
        {
            yield return entry.Start;
            yield break;
        }

        var rule = entry.Rule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToDictionary(part => part[0].ToUpperInvariant(), part => part[1], StringComparer.Ordinal);
        string every = rule.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        int interval = int.TryParse(rule.GetValueOrDefault("INTERVAL"), out int step) && step > 0 ? step : 1;
        int count = int.TryParse(rule.GetValueOrDefault("COUNT"), out int times) && times > 0 ? times : int.MaxValue;
        DateTime until = rule.TryGetValue("UNTIL", out string? last) && Moment(last, "", out DateTime end, out _) ? end : DateTime.MaxValue;
        DayOfWeek[] days = rule.GetValueOrDefault("BYDAY", "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(day => Weekday(day)).OfType<DayOfWeek>().ToArray();

        int made = 0;
        if (every == "WEEKLY" && days.Length > 0)
        {
            // Week by week from the first one, each chosen weekday at the event's own time.
            DateTime week = entry.Start.Date.AddDays(-(int)entry.Start.DayOfWeek);
            for (int guard = 0; guard < 3000 && week <= to; guard++, week = week.AddDays(7 * interval))
            {
                foreach (DayOfWeek day in days.OrderBy(day => day))
                {
                    DateTime start = week.AddDays((int)day) + entry.Start.TimeOfDay;
                    if (start < entry.Start) continue;
                    if (start > until || ++made > count) yield break;
                    if (start >= from && start < to) yield return start;
                }
            }
            yield break;
        }

        for (int index = 0; index < 5000; index++)
        {
            DateTime start = every switch
            {
                "DAILY" => entry.Start.AddDays((double)index * interval),
                "WEEKLY" => entry.Start.AddDays(7.0 * index * interval),
                "MONTHLY" => entry.Start.AddMonths(index * interval),
                "YEARLY" => entry.Start.AddYears(index * interval),
                _ => index == 0 ? entry.Start : DateTime.MaxValue,
            };
            if (start > until || start >= to || ++made > count) yield break;
            // A monthly event on the 31st skips the months that have no 31st.
            if (every == "MONTHLY" && start.Day != entry.Start.Day) continue;
            if (start >= from) yield return start;
        }
    }

    static IEnumerable<Entry> Entries(string ics)
    {
        Entry? entry = null;
        foreach (string line in Unfolded(ics))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                entry = new Entry();
                continue;
            }
            if (entry is null) continue;
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
                entry = null;
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string head = line[..colon], value = line[(colon + 1)..];
            int semicolon = head.IndexOf(';');
            string name = (semicolon < 0 ? head : head[..semicolon]).ToUpperInvariant();
            string parameters = semicolon < 0 ? "" : head[(semicolon + 1)..];
            switch (name)
            {
                case "SUMMARY": entry.Title = Text(value); break;
                case "LOCATION": entry.Location = Text(value); break;
                case "STATUS": entry.Cancelled = value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "RRULE": entry.Rule = value.Trim(); break;
                case "DTSTART":
                    if (Moment(value, parameters, out DateTime start, out bool allDay))
                    {
                        entry.Start = start;
                        entry.AllDay = allDay;
                    }
                    break;
                case "DTEND":
                    if (Moment(value, parameters, out DateTime end, out _))
                    {
                        entry.End = end;
                        entry.HasEnd = true;
                    }
                    break;
                case "EXDATE":
                    foreach (string skipped in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (Moment(skipped, parameters, out DateTime gone, out _)) entry.Skipped.Add(gone);
                    }
                    break;
            }
        }
    }

    // A long line is continued on the next one, which then starts with a space or a tab.
    static IEnumerable<string> Unfolded(string ics)
    {
        var whole = new StringBuilder();
        foreach (string raw in ics.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                whole.Append(line, 1, line.Length - 1);
                continue;
            }
            if (whole.Length > 0) yield return whole.ToString();
            whole.Clear().Append(line);
        }
        if (whole.Length > 0) yield return whole.ToString();
    }

    // "20261116T090000Z" (world time), "20261116T110000" with TZID=Europe/Sofia (that zone, or this computer's when
    // it isn't known), or "20261116" (a whole day). Out: this computer's time.
    static bool Moment(string value, string parameters, out DateTime moment, out bool allDay)
    {
        string text = value.Trim();
        allDay = false;
        if (DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out moment))
        {
            allDay = true;
            return true;
        }
        if (text.EndsWith('Z') && DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                                                         DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utc))
        {
            moment = utc.ToLocalTime();
            return true;
        }
        if (!DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime wall)) return false;

        moment = wall;
        string zone = parameters.Split(';').FirstOrDefault(part => part.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))?[5..].Trim('"') ?? "";
        if (zone.Length == 0) return true;
        try
        {
            TimeZoneInfo there = TimeZoneInfo.FindSystemTimeZoneById(zone);
            moment = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), there, TimeZoneInfo.Local);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            // An unknown zone name: taken as this computer's time.
        }
        return true;
    }

    static DayOfWeek? Weekday(string code) => code.Trim().ToUpperInvariant() switch
    {
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        "SU" => DayOfWeek.Sunday,
        _ => null, // "1MO", "-1FR": not understood
    };

    // "Lunch\, then a walk" is written with backslashes.
    static string Text(string value) =>
        value.Replace(@"\n", " ", StringComparison.OrdinalIgnoreCase).Replace(@"\,", ",").Replace(@"\;", ";").Replace(@"\\", @"\").Trim();
}
