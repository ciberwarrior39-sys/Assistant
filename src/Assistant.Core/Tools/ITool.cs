using System.Text.Json;
using Assistant.Core.Llm;
using Assistant.Core.Search;

namespace Assistant.Core.Tools;

/// <summary>A capability the model can call. Adding a feature means adding a tool.</summary>
public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolOutcome> ExecuteAsync(JsonElement arguments, CancellationToken ct);
}

/// <summary>What a tool tells the model, plus anything the app shows directly (like found files).</summary>
public sealed record ToolOutcome(string TextForModel)
{
    public IReadOnlyList<FileHit> Files { get; init; } = [];

    /// <summary>Lines the app shows under the reply, word for word (for example what was saved to memory).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// When set, the app says this instead of the model's own words. The small model rambles and
    /// invents paths after a search, while the file cards already show the real ones.
    /// </summary>
    public string? Reply { get; init; }

    /// <summary>The tool looked and found nothing (no file, no saved fact): the character shows "hmm?".</summary>
    public bool NothingFound { get; init; }

    /// <summary>
    /// look_at_screen answered this question from a screenshot: the chat offers "Solve with cloud chat",
    /// which sends only the text read from the screen (shown to the user first), never the image.
    /// </summary>
    public string? ScreenQuestion { get; init; }
}
