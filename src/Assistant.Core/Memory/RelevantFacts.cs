using System.Text.RegularExpressions;

namespace Assistant.Core.Memory;

/// <summary>
/// Picks the few saved lines (facts, conversation notes) that share words with a message, so a small model gets the
/// ones that matter instead of all or none. Plain word matching: no extra model, instant, and the same every time.
/// Words match when they're equal or share a long enough start ("study" and "studies", "училище" and "училището").
/// </summary>
public static partial class RelevantFacts
{
    // Common words that say nothing about the topic (short ones are skipped anyway).
    static readonly HashSet<string> Common =
    [
        "have", "does", "would", "could", "should", "there", "their", "they", "them", "when", "where", "which", "will",
        "just", "like", "want", "need", "please", "think", "some", "more", "very", "much", "really", "tell", "make",
        "help", "good", "also", "been", "were", "what", "whats", "here", "then", "than", "into", "over", "only",
        "това", "тази", "този", "тези", "какво", "какъв", "каква", "кога", "къде", "защо", "като", "само", "много",
        "моят", "моята", "моите", "моето", "твоя", "може", "искам", "имам", "трябва", "мога", "сега", "днес", "вчера",
    ];

    /// <summary>Up to <paramref name="max"/> lines sharing the most words with the message, best first, within maxChars.</summary>
    public static IReadOnlyList<string> Pick(string message, IEnumerable<string> lines, int max, int maxChars)
    {
        string[] words = Significant(message);
        if (words.Length == 0) return [];

        var picked = new List<string>();
        int length = 0;
        foreach (string line in lines
                     .Select((line, index) => (line, index, score: Score(words, Significant(line))))
                     .Where(item => item.score > 0)
                     .OrderByDescending(item => item.score)
                     .ThenBy(item => item.index)
                     .Select(item => item.line))
        {
            if (picked.Count == max) break;
            if (length + line.Length > maxChars) continue;
            picked.Add(line);
            length += line.Length;
        }
        return picked;
    }

    /// <summary>A fact that names the user ("My name is Poni", "Казвам се …"): useful in every chat.</summary>
    public static bool IsName(string fact) => NameFact().IsMatch(fact.ToLowerInvariant());

    static int Score(string[] query, string[] line) => query.Count(word => line.Any(other => Similar(word, other)));

    static bool Similar(string a, string b)
    {
        if (a == b) return true;
        int shorter = Math.Min(a.Length, b.Length);
        if (shorter < 4) return false;
        int common = 0;
        while (common < shorter && a[common] == b[common]) common++;
        return common >= Math.Max(4, shorter - 2);
    }

    static string[] Significant(string text) =>
        MemoryStore.Words(text).Where(word => word.Length >= 3 && !Common.Contains(word)).ToArray();

    [GeneratedRegex(@"\b(my name|i'?m called|call me|name is|goes by)\b|казвам се|името ми|наричай ме")]
    private static partial Regex NameFact();
}
