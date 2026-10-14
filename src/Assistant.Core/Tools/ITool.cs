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
}
