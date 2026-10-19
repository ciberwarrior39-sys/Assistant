using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core.Memory;
using Assistant.Core.Tools;

namespace Assistant.Core;

/// <summary>A tool to run straight away, with its arguments, for a request whose meaning is clear.</summary>
public sealed record RoutedCall(string Tool, JsonElement Arguments);

/// <summary>
/// Clear requests go straight to the right tool, decided by code: no model call, so it's instant, works the same on
/// any PC and can't be skipped or misread by a small model. Anything not clearly matched still goes to the model.
/// The rules are deliberately narrow: a missed request only costs a model call, a wrong match gives a wrong answer.
/// </summary>
public static partial class IntentRouter
{
    public static RoutedCall? Route(string message, MemoryStore? memory)
    {
        string text = Normalize(message);
        if (text.Length == 0 || text.Length > 200) return null; // long messages are conversation, not commands

        if (memory is not null)
        {
            // "remember that ...": saved in the user's own words.
            if (RememberTool.FromMessage(message) is { } fact)
                return Call("remember", new { fact = fact.Fact, kind = KindName(fact.Kind) });

            if (ForgetTool.IsRequest(message) && ForgetTool.WhatToForget(message) is { Length: > 0 } forget)
                return Call("forget", new { fact = forget });

            // "where's my CV?" when a saved fact says where it is: the fact answers it, with a card to open it.
            if (WhereIsMine().IsMatch(text) && HasMatchingFact(memory, text))
                return Call("recall", new { topic = message });

            if (RecallTool.ShouldLookUp(message, memory))
                return Call("recall", new { topic = message });
        }

        if (LookAtScreen().IsMatch(text)) return Call("look_at_screen", new { question = message });

        if (Activity().IsMatch(text)) return Call("get_recent_activity", new { period = Period(text) });

        if (FileSearch(text) is { } search) return search;
        return null;
    }

    // ---- Files --------------------------------------------------------------------------------------------

    // Only with a clear file signal: "my", a kind of file, an extension or a when ("find a recipe" is a question).
    static RoutedCall? FileSearch(string text)
    {
        Match verb = FindVerb().Match(text);
        if (!verb.Success) return null;
        string rest = text[(verb.Index + verb.Length)..];

        string[] words = Words().Matches(rest).Select(match => match.Value).ToArray();
        var extensions = new List<string>();
        var keywords = new List<string>();
        foreach (string word in words)
        {
            if (SearchFilesTool.KindsOfFile.TryGetValue(word, out string[]? kind)) extensions.AddRange(kind);
            else if (!SearchFilesTool.RequestWords.Contains(word) && !Filler.Contains(word)) keywords.Add(word);
        }
        string period = Modified(rest);

        bool fileSignal = rest.Contains("my ", StringComparison.Ordinal) || rest.StartsWith("my", StringComparison.Ordinal)
                          || extensions.Count > 0 || period != "any" || words.Any(word => word is "file" or "files" or "folder");
        if (!fileSignal || (keywords.Count == 0 && extensions.Count == 0 && period == "any")) return null;
        if (keywords.Count > 4) return null; // a sentence, not a file name

        return Call("search_files", new { keywords = keywords.Count > 0 ? new[] { string.Join(' ', keywords) } : Array.Empty<string>(), extensions, modified = period });
    }

    static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "is", "are", "was", "where", "please", "can", "you", "for", "me", "that", "this", "those", "these", "of", "in", "on",
        "from", "with", "about", "some", "all", "any", "it", "its", "called", "named", "name", "s", "did", "do", "have", "had",
        "week", "month", "year", "past", "this", "last", "ago", "earlier", "folder", "folders",
    };

    static string Modified(string text) =>
        text.Contains("today") ? "today"
        : Regex.IsMatch(text, @"\b(this|last|past) week\b|\bfew days\b|\byesterday\b") ? "past_week"
        : Regex.IsMatch(text, @"\b(this|last|past) month\b") ? "past_month"
        : Regex.IsMatch(text, @"\b(this|last|past) year\b") ? "past_year"
        : "any";

    // ---- Activity -----------------------------------------------------------------------------------------

    static string Period(string text) =>
        Regex.IsMatch(text, @"\b(last|past) hour\b|\bthis hour\b") ? "last_hour"
        : text.Contains("yesterday") ? "yesterday"
        : Regex.IsMatch(text, @"\b(this|last|past) week\b|\blast 7 days\b") ? "past_week"
        : "today";

    // ---- Helpers ------------------------------------------------------------------------------------------

    static bool HasMatchingFact(MemoryStore memory, string text)
    {
        string[] words = MemoryStore.Words(text).Where(word => word is not ("where" or "whats" or "wheres")).ToArray();
        return words.Length > 0 && memory.AllFacts().Any(fact => MemoryStore.Overlap(words, MemoryStore.Words(fact.Text)) > 0);
    }

    static string KindName(MemoryKind kind) => kind switch
    {
        MemoryKind.Preferences => "preference",
        MemoryKind.Places => "place",
        _ => "about_me",
    };

    static RoutedCall Call(string tool, object arguments) => new(tool, JsonSerializer.SerializeToElement(arguments));

    // Lower case, curly apostrophes made straight, no trailing punctuation, single spaces.
    static string Normalize(string message) =>
        string.Join(' ', message.ToLowerInvariant().Replace('’', '\'').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd('?', '!', '.', ' ');

    [GeneratedRegex(@"\b(where('s| is| are| did i (put|save|leave))) (my|the)\b")]
    private static partial Regex WhereIsMine();

    [GeneratedRegex(@"^(can you |could you |please |hey |ok )?(find|locate|search for|look for|where('s| is| are)|open|show me|get me)\b")]
    private static partial Regex FindVerb();

    [GeneratedRegex(@"\b(look at (this|my screen|the screen|that)|what'?s on my screen|what am i looking at|what does this (error|message|say|mean)|read (this|my screen)|can you see (this|my screen)|explain this (error|message))\b")]
    private static partial Regex LookAtScreen();

    [GeneratedRegex(@"\bwhat (was|have|did) i (been )?(working on|doing|do|done|work on)\b|\bhow (long|much time) (did|have) i (spend|spent|been)\b|\btime (did i spend|i spent|spent) (in|on)\b|\bmy (screen time|activity)\b|\bwhich apps did i\b")]
    private static partial Regex Activity();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}._-]*")]
    private static partial Regex Words();
}
