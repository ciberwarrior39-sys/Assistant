using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Assistant.Core;

/// <summary>
/// Timers and reminders the user sets by asking ("set a timer for 5 minutes", "remind me tomorrow at 9 to ...").
/// The app shows and says them when they're due, and keeps them in reminders.json so they survive a restart.
/// </summary>
public interface IReminders
{
    /// <summary>Starts a timer; false if too many are running.</summary>
    bool Start(TimeSpan after, string label);

    /// <summary>A reminder at a set time; false if too many are waiting.</summary>
    bool At(DateTime due, string label);

    IReadOnlyList<(string Label, TimeSpan Left)> Running { get; }

    /// <summary>Everything waiting, soonest first.</summary>
    IReadOnlyList<(string Label, DateTime Due, bool Timer)> Upcoming { get; }

    int CancelAll();

    /// <summary>Cancels the one reminder matching the words; returns the labels that matched (one means it was cancelled).</summary>
    IReadOnlyList<string> Cancel(string words);
}

/// <summary>
/// Small requests answered by code, instantly, before any model: coin flips, dice, random numbers, picking
/// between options, the time and date, arithmetic and timers. A language model can't be random, doesn't know
/// the time and gets sums wrong, so these never reach it. Patterns are strict so ordinary chat isn't caught.
/// </summary>
public static partial class QuickAnswers
{
    const int MaxDice = 10;
    const int MaxSides = 1000;
    static readonly TimeSpan MaxTimer = TimeSpan.FromHours(24);

    public static string? TryAnswer(string message, DateTime now, Random random, IReminders? reminders)
    {
        string text = Normalize(message);
        if (text.Length == 0 || text.Length > 120) return null;

        if (Coin().IsMatch(text))
            return random.Next(2) == 0 ? "Heads!" : "Tails!";

        if (Dice().Match(text) is { Success: true } dice) return RollDice(dice, random);
        if (Number().Match(text) is { Success: true } number) return PickNumber(number, random);
        if (Choice().Match(text) is { Success: true } choice) return PickOption(choice.Groups["options"].Value, random);

        if (Time().IsMatch(text)) return $"It's {now:HH:mm}.";
        if (Date().IsMatch(text)) return $"Today is {now.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture)}.";

        if (reminders is not null)
        {
            if (TimerStart().Match(text) is { Success: true } timer) return StartTimer(timer, reminders);
            if (TimerCancel().IsMatch(text))
            {
                int cancelled = reminders.CancelAll();
                return cancelled == 0 ? "No timers are running." : cancelled == 1 ? "Timer cancelled." : $"{cancelled} timers cancelled.";
            }
            if (TimerLeft().IsMatch(text)) return TimeLeft(reminders);
        }

        if (Percent().Match(text) is { Success: true } percent) return PercentOf(percent);
        return Arithmetic(text);
    }

    // "Hey, can you please flip a coin?" -> "flip a coin"
    static string Normalize(string message)
    {
        string text = message.Trim().ToLowerInvariant().Replace('’', '\'');
        text = Lead().Replace(text, "");
        return text.TrimEnd('?', '!', '.', ' ');
    }

    static string RollDice(Match match, Random random)
    {
        int count = ParseCount(match.Groups["count"].Value);
        int sides = match.Groups["sides"].Success ? int.Parse(match.Groups["sides"].Value, CultureInfo.InvariantCulture) : 6;
        if (count < 1 || count > MaxDice) return $"I can roll 1 to {MaxDice} dice at a time.";
        if (sides < 2 || sides > MaxSides) return $"Dice need 2 to {MaxSides} sides.";

        int[] rolls = Enumerable.Range(0, count).Select(_ => random.Next(1, sides + 1)).ToArray();
        if (count == 1) return $"You rolled {rolls[0]}.";
        return $"You rolled {JoinAnd(rolls.Select(roll => roll.ToString(CultureInfo.InvariantCulture)))} (total {rolls.Sum()}).";
    }

    static string PickNumber(Match match, Random random)
    {
        if (!match.Groups["low"].Success) return $"{random.Next(1, 101)}.";
        if (!long.TryParse(match.Groups["low"].Value, out long low) || !long.TryParse(match.Groups["high"].Value, out long high))
            return "Those numbers are too big for me.";
        if (low > high) (low, high) = (high, low);
        return $"{random.NextInt64(low, high + 1)}.";
    }

    static string? PickOption(string options, Random random)
    {
        string[] choices = OptionSplit().Split(options)
            .Select(option => option.Trim(' ', ',', '"', '\''))
            .Where(option => option.Length > 0)
            .Distinct()
            .ToArray();
        return choices.Length < 2 ? null : $"I pick {choices[random.Next(choices.Length)]}.";
    }

    static string StartTimer(Match match, IReminders reminders)
    {
        double amount = double.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture);
        string unit = match.Groups["unit"].Value;
        TimeSpan after = unit[0] switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromMinutes(amount),
        };
        if (after < TimeSpan.FromSeconds(1) || after > MaxTimer) return "Timers can be from 1 second to 24 hours.";

        string label = match.Groups["label"].Success ? match.Groups["label"].Value.Trim() : "";
        if (!reminders.Start(after, label)) return "That's a lot of timers already. Cancel some first.";

        string length = Describe(after);
        return label.Length > 0 ? $"OK, I'll remind you to {label} in {length}." : $"Timer set for {length}.";
    }

    static string TimeLeft(IReminders reminders)
    {
        var running = reminders.Running;
        if (running.Count == 0) return "No timers are running.";
        return string.Join(" ", running.Select(timer =>
            (timer.Label.Length > 0 ? $"\"{timer.Label}\": " : "Timer: ") + $"{Describe(timer.Left)} left."));
    }

    static string PercentOf(Match match)
    {
        double percent = double.Parse(match.Groups["percent"].Value, CultureInfo.InvariantCulture);
        double of = double.Parse(match.Groups["of"].Value, CultureInfo.InvariantCulture);
        return $"{Format(percent)}% of {Format(of)} is {Format(percent / 100 * of)}.";
    }

    // "what's 12 times 7" -> "12 × 7 = 84". Only digits, brackets and + - * / ever reach the evaluator.
    static string? Arithmetic(string text)
    {
        string expression = MathLead().Replace(text, "");
        expression = expression
            .Replace("multiplied by", "*").Replace("times", "*").Replace(" x ", " * ").Replace("×", "*")
            .Replace("divided by", "/").Replace("over", "/").Replace("÷", "/")
            .Replace("plus", "+").Replace("minus", "-")
            .Replace(",", "");
        if (!MathOnly().IsMatch(expression) || !MathOperator().IsMatch(expression)) return null;

        try
        {
            object result = new DataTable { Locale = CultureInfo.InvariantCulture }.Compute(expression, "");
            double value = Convert.ToDouble(result, CultureInfo.InvariantCulture);
            if (double.IsInfinity(value) || double.IsNaN(value)) return "That can't be worked out (dividing by zero?).";
            string shown = Spaces().Replace(expression.Replace("*", " × ").Replace("/", " ÷ ").Replace("+", " + ").Replace(" - ", " - "), " ").Trim();
            return $"{shown} = {Format(value)}";
        }
        catch (DivideByZeroException)
        {
            return "You can't divide by zero.";
        }
        catch (Exception ex) when (ex is EvaluateException or SyntaxErrorException or OverflowException)
        {
            return null; // not a sum after all: let the model have it
        }
    }

    static int ParseCount(string word) => word switch
    {
        "" or "a" or "an" or "one" or "the" => 1,
        "two" or "pair of" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        _ => int.TryParse(word, out int count) ? count : 1,
    };

    static string Describe(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return Plural((int)Math.Ceiling(span.TotalSeconds), "second");
        if (span.TotalMinutes < 60)
        {
            int seconds = span.Seconds;
            return Plural((int)span.TotalMinutes, "minute") + (seconds > 0 ? $" {Plural(seconds, "second")}" : "");
        }
        return Plural((int)span.TotalHours, "hour") + (span.Minutes > 0 ? $" {Plural(span.Minutes, "minute")}" : "");
    }

    static string Plural(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";

    static string Format(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);

    static string JoinAnd(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 1 ? list[0] : string.Join(", ", list[..^1]) + " and " + list[^1];
    }

    [GeneratedRegex(@"^((hey|hi|ok|okay)[, ]+)?((can|could|would) you |will you |please |let's |lets )*")]
    private static partial Regex Lead();

    [GeneratedRegex(@"^(flip|toss)( me)? a coin$|^(flip|toss)( a)? coin$|^coin (flip|toss)$|^heads or tails$|^tails or heads$")]
    private static partial Regex Coin();

    [GeneratedRegex(@"^roll(?: me)?(?: (?<count>a|an|one|the|two|three|four|five|pair of|\d+))? ?(?:(?:dice|die)|d(?<sides>\d+)|(?:(?:dice|die) with (?<sides>\d+) sides)|(?<sides>\d+)[- ]sided (?:dice|die))$")]
    private static partial Regex Dice();

    [GeneratedRegex(@"^(pick|choose|give me|generate|say)( me)? a (random )?number( between (?<low>-?\d+) and (?<high>-?\d+)| from (?<low>-?\d+) to (?<high>-?\d+))?$|^random number( between (?<low>-?\d+) and (?<high>-?\d+)| from (?<low>-?\d+) to (?<high>-?\d+))?$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^(pick|choose|decide)( one)?( for me)?( between)?:? (?<options>.+ or .+)$")]
    private static partial Regex Choice();

    [GeneratedRegex(@"\s*,\s*or\s+|\s+or\s+|\s*,\s*")]
    private static partial Regex OptionSplit();

    [GeneratedRegex(@"^(what('s| is) the time( now)?|what time is it( now)?|tell me the time|time)$")]
    private static partial Regex Time();

    [GeneratedRegex(@"^(what('s| is) (today's date|the date( today)?|today)|what day is (it|today)( today)?|what's the day today|today's date|date)$")]
    private static partial Regex Date();

    [GeneratedRegex(@"^(set (a |an )?timer|start (a |an )?timer|timer|remind me)( for| in)? (?<amount>\d+(\.\d+)?) ?(?<unit>seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h)( to (?<label>.{1,60}))?$")]
    private static partial Regex TimerStart();

    [GeneratedRegex(@"^(cancel|stop|clear|delete)( the| my| all| all the| all my)? (timers?|reminders?)$")]
    private static partial Regex TimerCancel();

    [GeneratedRegex(@"^(how (long|much time)( is)? left( on (the|my) timers?)?|how much longer|time left|timers?)$")]
    private static partial Regex TimerLeft();

    [GeneratedRegex(@"^(what('s| is) |calculate )?(?<percent>\d+(\.\d+)?) ?(%|percent) of (?<of>\d+(\.\d+)?)$")]
    private static partial Regex Percent();

    [GeneratedRegex(@"^(what('s| is)|calculate|compute|how much is)\s+")]
    private static partial Regex MathLead();

    [GeneratedRegex(@"^[\d\s\.\+\-\*/\(\)]+$")]
    private static partial Regex MathOnly();

    [GeneratedRegex(@"\d\s*[\+\-\*/]\s*[\d\(]|\)\s*[\+\-\*/]")]
    private static partial Regex MathOperator();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
