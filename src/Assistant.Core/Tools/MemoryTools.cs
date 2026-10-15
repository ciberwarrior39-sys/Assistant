using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Assistant.Core.Llm;
using Assistant.Core.Memory;
using Assistant.Core.Search;

namespace Assistant.Core.Tools;

/// <summary>
/// remember: saves a fact, but only when the user's own message asks for it (spec: explicit saves only;
/// anything the assistant infers goes to the weekly review instead). The check is in code, not left to the model.
/// </summary>
public sealed class RememberTool(MemoryStore memory, Func<string> currentUserText) : ITool
{
    static readonly string[] Cues =
        ["remember", "don't forget", "dont forget", "do not forget", "note that", "make a note", "keep in mind", "save that", "memorize"];

    // Questions about memory, not requests to save: these go to recall.
    static readonly string[] Questions = ["do you remember", "what do you remember", "did i tell you", "what did i"];

    public ToolDefinition Definition { get; } = new(
        "remember",
        "Save a fact the user explicitly asked you to remember.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["fact"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The fact as one short sentence, e.g. \"Keeps invoices in the Finance folder\".",
                },
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("about_me", "preference", "place"),
                    ["description"] = "about_me: a fact about the user. preference: how you should behave. place: a folder or file.",
                },
            },
            ["required"] = new JsonArray("fact", "kind"),
        });

    /// <summary>The user asked to save something ("remember that...", not "do you remember...?").</summary>
    public static bool IsRequest(string userText) =>
        MemoryArguments.Mentions(userText, Cues) && !MemoryArguments.Mentions(userText, Questions);

    // Facts about how the assistant should behave. Only these go to preferences.md, the one file sent to the cloud,
    // so anything unclear stays in about-me.md, which never leaves this computer.
    static readonly Regex PreferenceWords = new(
        @"\b(call me|talk to me|speak to me|answers?|repl(y|ies)|respond)\b|^(you should|you must|always|never|don't|do not|be)\b",
        RegexOptions.IgnoreCase);
    static readonly Regex PlaceWords = new(@"[A-Za-z]:\\|\b(folder|file|drive)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// The fact in the user's own words, for when the model didn't call remember:
    /// "Please remember that my name is Poni" -> "my name is Poni"; "my name is Poni, remember that" -> "my name is Poni".
    /// </summary>
    public static (string Fact, MemoryKind Kind)? FromMessage(string userText)
    {
        if (!IsRequest(userText)) return null;

        // Lower-casing keeps the length, so positions match the original text.
        string lower = userText.ToLowerInvariant().Replace('’', '\'');
        (int index, string cue) = Cues.Select(word => (index: lower.IndexOf(word, StringComparison.Ordinal), word))
            .Where(found => found.index >= 0)
            .MinBy(found => found.index);

        string after = Tidy(userText[(index + cue.Length)..]);
        foreach (string lead in new[] { "that ", "this: ", "this " })
        {
            if (after.StartsWith(lead, StringComparison.OrdinalIgnoreCase)) after = after[lead.Length..].TrimStart();
        }
        string fact = after.Length >= 3 && !after.Equals("this", StringComparison.OrdinalIgnoreCase)
                      && !after.Equals("that", StringComparison.OrdinalIgnoreCase)
            ? after
            : Tidy(userText[..index]);
        if (fact.Length < 3) return null;

        MemoryKind kind = PlaceWords.IsMatch(fact) ? MemoryKind.Places
            : PreferenceWords.IsMatch(fact) ? MemoryKind.Preferences
            : MemoryKind.AboutMe;
        return (fact, kind);
    }

    // Drops punctuation and filler around the fact: ", please", "and", "can you", trailing "?".
    static string Tidy(string text)
    {
        string result = text.Trim().Trim(',', ':', ';', '-', '.', '!', '?', ' ');
        foreach (string filler in new[] { "please", "can you", "could you", "and", "so", "ok", "okay", "hey" })
        {
            if (result.StartsWith(filler + " ", StringComparison.OrdinalIgnoreCase)) result = result[filler.Length..];
            if (result.EndsWith(" " + filler, StringComparison.OrdinalIgnoreCase)) result = result[..^filler.Length];
            if (result.Equals(filler, StringComparison.OrdinalIgnoreCase)) result = "";
            result = result.Trim().Trim(',', ':', ';', '-', '.', '!', '?', ' ');
        }
        return result;
    }

    public Task<ToolOutcome> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!IsRequest(currentUserText()))
            return Task.FromResult(new ToolOutcome("Not saved: only save when the user asks you to remember something. Answer normally."));

        string fact = MemoryArguments.Text(arguments, "fact");
        if (fact.Length == 0) return Task.FromResult(new ToolOutcome("Not saved: the fact was empty. Ask what to remember."));

        MemoryKind kind = MemoryArguments.Kind(arguments);
        bool added = memory.Add(kind, fact, "you asked me to remember it");
        string file = Path.GetFileName(memory.FileFor(kind));

        return Task.FromResult(added
            ? new ToolOutcome($"Saved to {file}. Confirm in a few words.") { Notes = [$"Saved to {file}: {fact}"] }
            : new ToolOutcome("This was already saved. Say so in a few words."));
    }
}

/// <summary>recall: the saved facts most related to the topic. Memory is small, so the rest follow, up to a cap.</summary>
public sealed class RecallTool(MemoryStore memory) : ITool
{
    const int MaxFacts = 15;

    // Always about saved facts.
    static readonly string[] MemoryQuestions =
        ["do you remember", "what do you remember", "what do you know about me", "did i tell you", "what did i tell you", "who am i"];

    // "What's my name?": about saved facts only when one matches; otherwise ordinary chat ("what's my IP?").
    static readonly string[] MyQuestions =
        ["what's my", "whats my", "what is my", "what are my", "when is my", "when's my", "who is my", "who's my",
         "how old am i", "where do i", "where am i", "do i like", "what do i"];

    /// <summary>A question the saved facts answer, for when the model didn't call recall itself.</summary>
    public static bool ShouldLookUp(string userText, MemoryStore memory)
    {
        if (RememberTool.IsRequest(userText)) return false;
        if (MemoryArguments.Mentions(userText, MemoryQuestions)) return true;
        if (!MemoryArguments.Mentions(userText, MyQuestions)) return false;

        string[] words = MemoryStore.Words(userText);
        return memory.AllFacts().Any(fact => MemoryStore.Overlap(words, MemoryStore.Words(fact.Text)) > 0);
    }

    public static JsonElement TopicArguments(string userText) => JsonSerializer.SerializeToElement(new { topic = userText });

    public ToolDefinition Definition { get; } = new(
        "recall",
        "Look up what the user told you to remember: facts about them, their preferences and their folders.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["topic"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "What to look up, e.g. \"name\" or \"CV folder\". Empty for everything.",
                },
            },
        });

    public Task<ToolOutcome> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        IReadOnlyList<MemoryFact> all = memory.AllFacts();
        if (all.Count == 0)
            return Task.FromResult(new ToolOutcome("Nothing is saved yet. Tell the user they can say \"remember that...\"."));

        string topic = MemoryArguments.Text(arguments, "topic");
        IReadOnlyList<MemoryFact> facts = memory.Search(topic, all).Take(MaxFacts).ToList();

        var text = new StringBuilder("Saved facts, most related first (\"I\" and \"my\" in a fact mean the user). "
                                     + "Answer only from these; if none fits, say you don't know:\n");
        foreach (MemoryFact fact in facts) text.AppendLine($"- {fact.Text}");

        // The files themselves, so the user can open and edit what is stored.
        IReadOnlyList<FileHit> files = facts.Select(fact => fact.Kind).Distinct()
            .Select(kind => memory.FileFor(kind))
            .Where(File.Exists)
            .Select(path => new FileHit(path, File.GetLastWriteTime(path)))
            .ToList();

        return Task.FromResult(new ToolOutcome(text.ToString()) { Files = files });
    }
}

/// <summary>
/// forget: deletes saved lines that clearly match what the user named, at most two per request,
/// and the app shows exactly which lines went.
/// </summary>
public sealed class ForgetTool(MemoryStore memory, Func<string> currentUserText) : ITool
{
    const int MaxRemoved = 2;

    static readonly string[] Cues = ["forget", "delete", "remove", "erase", "don't remember", "stop remembering"];

    /// <summary>The user mentioned forgetting, so the message is about memory and stays local.</summary>
    public static bool IsRequest(string userText) => MemoryArguments.Mentions(userText, ["forget"]);

    public ToolDefinition Definition { get; } = new(
        "forget",
        "Delete a saved fact the user asks you to forget.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["fact"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Words from the saved fact to delete, e.g. \"CV folder\".",
                },
            },
            ["required"] = new JsonArray("fact"),
        });

    public Task<ToolOutcome> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!MemoryArguments.Mentions(currentUserText(), Cues))
            return Task.FromResult(new ToolOutcome("Nothing deleted: only delete when the user asks you to forget something."));

        string[] words = MemoryStore.Words(MemoryArguments.Text(arguments, "fact"));
        if (words.Length == 0) return Task.FromResult(new ToolOutcome("Nothing deleted. Ask which saved fact to forget."));

        // A clear match: most of the user's words appear in the line.
        var matches = memory.AllFacts()
            .Select(fact => (fact, score: MemoryStore.Overlap(words, MemoryStore.Words(fact.Text))))
            .Where(item => item.score * 2 > words.Length)
            .OrderByDescending(item => item.score)
            .Select(item => item.fact)
            .ToList();

        if (matches.Count == 0)
            return Task.FromResult(new ToolOutcome("No saved fact matches. Nothing was deleted. Say so."));
        if (matches.Count > MaxRemoved)
            return Task.FromResult(new ToolOutcome(
                $"{matches.Count} saved facts match, so nothing was deleted. Ask the user which one, or to edit the memory files."));

        IReadOnlyList<MemoryFact> removed = memory.Remove(matches);
        return Task.FromResult(new ToolOutcome("Deleted. Confirm in a few words.")
        {
            Notes = removed.Select(fact => $"Forgot ({Path.GetFileName(memory.FileFor(fact.Kind))}): {fact.Text}").ToList(),
        });
    }
}

static class MemoryArguments
{
    public static string Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    public static MemoryKind Kind(JsonElement arguments) => Text(arguments, "kind").ToLowerInvariant() switch
    {
        "preference" or "preferences" => MemoryKind.Preferences,
        "place" or "places" or "folder" or "file" => MemoryKind.Places,
        _ => MemoryKind.AboutMe,
    };

    public static bool Mentions(string userText, string[] cues)
    {
        string text = userText.ToLowerInvariant().Replace('’', '\'');
        return cues.Any(cue => text.Contains(cue, StringComparison.Ordinal));
    }
}
