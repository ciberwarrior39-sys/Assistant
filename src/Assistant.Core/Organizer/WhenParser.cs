using System.Globalization;
using System.Text.RegularExpressions;

namespace Assistant.Core.Organizer;

/// <summary>
/// "at 18:00", "at 6pm", "tomorrow at 9", "on friday at 10", "tonight", "in 2 hours", and the Bulgarian
/// "в 18:00", "утре в 9", "в петък в 10", "довечера", "след 2 часа". Anything it doesn't fully understand gives null,
/// so a reminder is never set for a time the user didn't mean. The reply always states the exact time it set.
/// </summary>
public static partial class WhenParser
{
    static readonly Dictionary<string, DayOfWeek> Weekdays = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monday"] = DayOfWeek.Monday, ["tuesday"] = DayOfWeek.Tuesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["friday"] = DayOfWeek.Friday, ["saturday"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday,
        ["понеделник"] = DayOfWeek.Monday, ["вторник"] = DayOfWeek.Tuesday, ["сряда"] = DayOfWeek.Wednesday,
        ["четвъртък"] = DayOfWeek.Thursday, ["петък"] = DayOfWeek.Friday, ["събота"] = DayOfWeek.Saturday,
        ["неделя"] = DayOfWeek.Sunday,
    };

    // Words that only join the parts: "on friday at 10", "в петък в 10 часа".
    static readonly HashSet<string> Joiners = new(StringComparer.OrdinalIgnoreCase)
    {
        "at", "on", "this", "next", "around", "about", "the", "o'clock", "oclock",
        "в", "във", "на", "около", "към", "следващия", "следващата", "следващото", "часа", "ч", "ч.",
    };

    public static DateTime? Parse(string when, DateTime now)
    {
        string text = Spaces().Replace(when.Trim().TrimEnd('.', '!', '?', ',').ToLowerInvariant(), " ");
        if (text.Length == 0) return null;
        if (Relative(text, now) is { } relative) return relative;

        DateTime? day = null;
        bool weekday = false;
        string? part = null; // morning, afternoon, evening, noon, midnight
        int? hour = null, minute = null;
        string? meridiem = null;

        // Time of day first, so "18:00" isn't read as words.
        Match time = Clock().Match(text);
        if (time.Success)
        {
            hour = int.Parse(time.Groups["h"].Value, CultureInfo.InvariantCulture);
            minute = time.Groups["m"].Success ? int.Parse(time.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            if (time.Groups["ap"].Success) meridiem = time.Groups["ap"].Value.StartsWith('p') ? "pm" : "am";
            text = text.Remove(time.Index, time.Length).Insert(time.Index, " ");
        }
        if (hour is > 23 || minute is > 59 || (meridiem is not null && hour is 0 or > 12)) return null;

        foreach (string word in text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (word)
            {
                case "today" or "днес": day = now.Date; break;
                case "tomorrow" or "утре": day = now.Date.AddDays(1); break;
                case "вдругиден": day = now.Date.AddDays(2); break;
                case "tonight" or "довечера": day = now.Date; part = "evening"; break;
                case "morning" or "сутринта" or "сутрин": part = "morning"; break;
                case "afternoon" or "следобед" or "следобяд": part = "afternoon"; break;
                case "evening" or "вечерта" or "вечер" or "night" or "нощта": part = "evening"; break;
                case "noon" or "midday" or "обед": part = "noon"; break;
                case "midnight" or "полунощ": part = "midnight"; break;
                default:
                    if (Weekdays.TryGetValue(word, out DayOfWeek target))
                    {
                        int ahead = ((int)target - (int)now.DayOfWeek + 7) % 7;
                        day = now.Date.AddDays(ahead);
                        weekday = true;
                    }
                    else if (!Joiners.Contains(word) && word is not ("in" or "the" or "day" or "after"))
                    {
                        return null; // a word we don't understand: better no reminder than a wrong one
                    }
                    break;
            }
        }
        // "the day after tomorrow"
        if (text.Contains("day after tomorrow", StringComparison.Ordinal)) day = now.Date.AddDays(2);

        if (hour is null)
        {
            (hour, minute) = part switch
            {
                "morning" => (9, 0),
                "afternoon" => (15, 0),
                "evening" => (20, 0),
                "noon" => (12, 0),
                "midnight" => (0, 0),
                _ => ((int?)null, (int?)null),
            };
            if (hour is null && day is null) return null;
            hour ??= 9; // "tomorrow" with no time: 9 in the morning
            minute ??= 0;
            if (part == "midnight" && day is null) day = now.Date.AddDays(1);
        }
        else
        {
            if (meridiem == "pm" && hour < 12) hour += 12;
            else if (meridiem == "am" && hour == 12) hour = 0;
            else if (meridiem is null && part is "afternoon" or "evening" && hour < 12) hour += 12;
        }

        DateTime result = (day ?? now.Date).AddHours(hour!.Value).AddMinutes(minute!.Value);

        // "at 6" or "today at 9" said in the evening means later today (18:00, 21:00), not the morning.
        bool later = result <= now && meridiem is null && part is null && hour < 12 && (day is null || day == now.Date)
                     && now.Date.AddHours(hour.Value + 12).AddMinutes(minute.Value) > now;
        if (later)
            result = result.AddHours(12);
        else if (day is null && result <= now)
            result = result.AddDays(1); // "at 7" when 7 has passed for today: tomorrow
        else if (weekday && result <= now)
        {
            result = result.AddDays(7); // "on friday" said on Friday evening: next week
        }
        return result;
    }

    static DateTime? Relative(string text, DateTime now)
    {
        Match match = InEnglish().Match(text);
        if (!match.Success) match = InBulgarian().Match(text);
        if (!match.Success) return null;

        string amountText = match.Groups["n"].Value;
        double amount = amountText switch
        {
            "" or "a" or "an" or "one" or "един" or "една" or "едно" => 1,
            "half an" or "half a" or "половин" => 0.5,
            _ => double.TryParse(amountText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : -1,
        };
        if (amount <= 0) return null;

        string unit = match.Groups["u"].Value;
        TimeSpan length = unit[0] switch
        {
            'h' or 'ч' => TimeSpan.FromHours(amount),
            'd' or 'д' => TimeSpan.FromDays(amount),
            'w' or 'с' => TimeSpan.FromDays(7 * amount),
            _ => TimeSpan.FromMinutes(amount),
        };
        return length > TimeSpan.FromDays(366) ? null : now + length;
    }

    // 18:00, 18.30, 6pm, 6:30 pm, 9 a.m., 9 (a bare number counts as an hour)
    [GeneratedRegex(@"(?<![\d:.])(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?\s*(?<ap>am|pm|a\.m\.|p\.m\.)?(?![\d])")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^in (?<n>\d+(?:[.,]\d+)?|an?|one|half an?)\s*(?<u>hours?|hrs?|h|minutes?|mins?|m|days?|d|weeks?|w)$")]
    private static partial Regex InEnglish();

    [GeneratedRegex(@"^след (?:(?<n>\d+(?:[.,]\d+)?|един|една|едно|половин)\s*)?(?<u>часа|часове|час|ч|минути|минута|мин|дни|дена|ден|седмици|седмица)$")]
    private static partial Regex InBulgarian();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
