using System.Globalization;
using System.Text.RegularExpressions;

namespace Assistant.Core.Memory;

/// <summary>
/// The user's birthday, only from a fact they asked to remember ("remember my birthday is 5 March"). Day before month
/// for numbers ("05.03", "5/3"), as written in Bulgaria and most of Europe; month names in English or Bulgarian.
/// </summary>
public static partial class Birthday
{
    static readonly string[] English = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];
    static readonly string[] Bulgarian = ["януари", "февруари", "март", "април", "май", "юни", "юли", "август", "септември", "октомври", "ноември", "декември"];

    public static (int Month, int Day)? Find(IEnumerable<string> facts)
    {
        foreach (string fact in facts)
        {
            string text = fact.ToLowerInvariant();
            if (!Cue().IsMatch(text)) continue;
            if (Parse(text) is { } date) return date;
        }
        return null;
    }

    static (int Month, int Day)? Parse(string text)
    {
        Match iso = Iso().Match(text); // 2005-03-14
        if (iso.Success)
            return Valid(int.Parse(iso.Groups["m"].Value, CultureInfo.InvariantCulture), int.Parse(iso.Groups["d"].Value, CultureInfo.InvariantCulture));
        Match numbers = Numeric().Match(text);
        if (numbers.Success)
            return Valid(int.Parse(numbers.Groups["m"].Value, CultureInfo.InvariantCulture), int.Parse(numbers.Groups["d"].Value, CultureInfo.InvariantCulture));

        for (int month = 0; month < 12; month++)
        {
            foreach (string name in new[] { English[month], Bulgarian[month] })
            {
                // "5 march", "5th of march", "march 5", "march 5th"
                Match match = Regex.Match(text, $@"(?<!\d)(?<d>\d{{1,2}})(?:st|nd|rd|th)?\s+(?:of\s+)?{name}\b|\b{name}\s+(?<d>\d{{1,2}})(?:st|nd|rd|th)?(?!\d)");
                if (match.Success) return Valid(month + 1, int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture));
            }
        }
        return null;
    }

    static (int Month, int Day)? Valid(int month, int day) =>
        month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2024, month) ? (month, day) : null;

    [GeneratedRegex(@"birthday|born|рожден\s+ден|роден|родена|рожденият")]
    private static partial Regex Cue();

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}-(?<m>\d{1,2})-(?<d>\d{1,2})(?!\d)")]
    private static partial Regex Iso();

    // 05.03, 5/3, 5-3 (day first), optionally with a year
    [GeneratedRegex(@"(?<![\d.])(?<d>\d{1,2})[./-](?<m>\d{1,2})(?:[./-]\d{2,4})?(?![\d])")]
    private static partial Regex Numeric();
}
