using System.Text.RegularExpressions;

namespace Assistant.Core.Memory;

/// <summary>
/// Memory before the encyclopedia: a short question whose every key word is something the user saved a fact about
/// ("who is Kris?" with "Kris is my best friend" saved, "кога е изпитът?" with the exam's date saved) is answered from
/// those facts, by code, on this computer. The reply gets a "Look it up" button for when the world was meant after all.
/// Definitions ("what is a dog?") are left to the lookup even when a fact mentions the word.
/// </summary>
public static partial class MemoryMatch
{
    const int MaxQuestionChars = 120; // longer is conversation, not a lookup in memory
    const int MaxKeyWords = 4;
    const int MaxShown = 3;

    // Words that say nothing about the topic: question words, helpers and the like, English and Bulgarian.
    static readonly HashSet<string> NotKey =
    [
        "who", "whom", "whose", "what", "whats", "when", "where", "wheres", "which", "why", "how", "the", "and", "are", "was",
        "were", "does", "did", "doing", "done", "can", "could", "should", "would", "will", "has", "have", "had", "for", "from",
        "with", "about", "into", "tell", "explain", "describe", "please", "you", "your", "know", "its", "this", "that", "these",
        "those", "there", "here", "many", "much", "some", "any", "not", "but", "his", "her", "him", "she", "they", "them",
        "their", "our", "mine", "myself", "called", "mean", "means", "again", "now", "today", "say", "said",
        "кой", "коя", "кое", "кои", "какво", "какъв", "каква", "какви", "кога", "къде", "защо", "как", "колко", "чий", "чия",
        "чие", "чии", "дали", "съм", "сме", "сте", "бил", "била", "било", "били", "беше", "бяха", "във", "със", "или", "има",
        "няма", "това", "тази", "този", "тези", "той", "тя", "него", "нея", "тях", "мой", "моя", "моят", "мое", "моето", "мои",
        "моите", "кажи", "разкажи", "обясни", "опиши", "знаеш", "моля", "прави", "правят", "става", "казва", "като", "още",
        "вече", "също", "след", "преди", "при", "над", "под", "без", "през", "между", "сега", "днес",
    ];

    /// <summary>The words of a question that name its topic.</summary>
    public static string[] KeyWords(string question) =>
        Word().Matches(question.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(word => word.Length >= 3 && !NotKey.Contains(word))
            .Distinct()
            .ToArray();

    /// <summary>
    /// The saved facts (about me and places) that answer the question, best first, when every key word of it appears
    /// in them; empty otherwise.
    /// </summary>
    public static IReadOnlyList<MemoryFact> Find(string question, MemoryStore memory)
    {
        if (question.Length > MaxQuestionChars || Definition().IsMatch(question.Trim().ToLowerInvariant().Replace('’', '\''))) return [];
        string[] keys = KeyWords(question);
        if (keys.Length == 0 || keys.Length > MaxKeyWords) return [];

        var facts = memory.Facts(MemoryKind.AboutMe).Concat(memory.Facts(MemoryKind.Places))
            .Select(fact => (Fact: fact, Words: Word().Matches(fact.Text.ToLowerInvariant()).Select(match => match.Value).ToArray()))
            .ToList();
        if (!keys.All(key => facts.Any(fact => Has(fact.Words, key)))) return [];

        return facts
            .Select((fact, index) => (fact.Fact, Index: index, Score: keys.Count(key => Has(fact.Words, key))))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Select(item => item.Fact)
            .Take(MaxShown)
            .ToList();
    }

    static bool Has(string[] words, string key) => words.Any(word => RelevantFacts.Similar(key, word));

    // "what is a dog?", "what are black holes?", "what does X mean?", "какво е куче?": asks what a thing is, which is the
    // world's business. "what is the wifi password?", "what is Kris doing?" and "what is Mila's number?" are not definitions.
    [GeneratedRegex(@"^(?:what's|whats|what\s+is|what\s+are|what\s+was|what\s+were)\s+(?!the\b)(?!.*\b(?:doing|up\s+to)\b)(?!.*\p{L}'s\b)|^what\s+does\s+.+\s+mean\b|^define\b|^(?:какво|що)\s+(?:е|са|представлява|значи|означава)(?![\p{L}])")]
    private static partial Regex Definition();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
