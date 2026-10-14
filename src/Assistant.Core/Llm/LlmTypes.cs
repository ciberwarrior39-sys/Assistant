using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant.Core.Llm;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>A tool call requested by the model. Arguments is always a JSON object.</summary>
public sealed record ToolCall(string Name, JsonElement Arguments);

public sealed record ChatMessage(ChatRole Role, string Content)
{
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public string? ToolName { get; init; }

    /// <summary>Base64 images for a vision model (only the local model ever gets these).</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    public static ChatMessage ForSystem(string text) => new(ChatRole.System, text);
    public static ChatMessage ForUser(string text) => new(ChatRole.User, text);
    public static ChatMessage ForAssistant(string text) => new(ChatRole.Assistant, text);

    public static ChatMessage ForAssistantToolCalls(string text, IReadOnlyList<ToolCall> calls) =>
        new(ChatRole.Assistant, text) { ToolCalls = calls };

    public static ChatMessage ForTool(string toolName, string text) =>
        new(ChatRole.Tool, text) { ToolName = toolName };
}

public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

public sealed record LlmResponse(string Content, IReadOnlyList<ToolCall> ToolCalls)
{
    /// <summary>Token counts, when the server reports them (cloud providers do).</summary>
    public LlmUsage? Usage { get; init; }

    /// <summary>The model that actually answered (OpenRouter's free router picks one).</summary>
    public string? Model { get; init; }
}

public sealed record LlmUsage(int PromptTokens, int CompletionTokens);

/// <summary>A cloud provider with an OpenAI-compatible API. The key is optional; without it the provider is skipped.</summary>
public sealed record CloudEndpoint(string Name, Uri BaseAddress, string ApiKey, string Model, string KeyCheckPath)
{
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Models to switch to, in order, when the configured one is retired or not allowed for this key.
    /// Empty means no switching (OpenRouter's free router picks a model itself).
    /// </summary>
    public IReadOnlyList<string> FallbackModels { get; init; } = [];
}

/// <summary>Any chat model: local Ollama now, Groq and OpenRouter later.</summary>
public interface ILlmProvider
{
    string Name { get; }
    Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct);
}

/// <summary>The model server answered, but with an error (model not downloaded, key rejected, rate limit...).</summary>
public sealed class LlmException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
