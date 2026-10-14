using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        var text = new StringBuilder("Saved facts, most related first. Answer only from these; if none fits, say you don't know:\n");
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
