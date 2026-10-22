namespace Assistant.Core.Organizer;

/// <summary>Finds notes, tasks and reminders by a few of their words ("the note about wifi" → "wifi").</summary>
public static class TextMatch
{
    // Words that say nothing about which item is meant, in English and Bulgarian.
    static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "my", "about", "that", "says", "with", "for", "on", "of", "to", "it", "one", "note", "task", "item",
        "за", "на", "с", "със", "от", "ми", "моята", "моя", "бележката", "задачата", "че",
    };

    public static IReadOnlyList<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 1 && !Filler.Contains(word))
            .ToList();

    /// <summary>Every meaningful word of the query appears in the text.</summary>
    public static bool Matches(string text, string query)
    {
        IReadOnlyList<string> words = Words(query);
        if (words.Count == 0) return false;
        string lower = text.ToLowerInvariant();
        return words.All(word => lower.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>The items matching the query; an exact match on its own wins over partial ones.</summary>
    public static List<T> Find<T>(IEnumerable<T> items, Func<T, string> text, string query)
    {
        List<T> all = items.ToList();
        List<T> exact = all.Where(item => string.Equals(text(item).Trim(), query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count > 0 ? exact : all.Where(item => Matches(text(item), query)).ToList();
    }

    static readonly char[] Separators = [' ', ',', '.', ':', ';', '!', '?', '"', '\'', '(', ')', '-', '/', '\t', '„', '“', '”'];
}
