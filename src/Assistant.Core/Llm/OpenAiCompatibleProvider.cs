using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Llm;

/// <summary>
/// Groq, OpenRouter and other OpenAI-compatible cloud APIs. Used for plain conversation only,
/// so it never receives tools, and the caller decides which messages may leave the computer.
/// </summary>
public sealed class OpenAiCompatibleProvider(CloudEndpoint endpoint, HttpClient http) : ILlmProvider
{
    // Replies appear in a small chat window; this also keeps free token quotas lasting longer.
    const int MaxReplyTokens = 400;

    // Reasoning models think before answering, and that thinking needs room too.
    const int MaxReasoningModelTokens = 1024;

    public string Name => endpoint.Name;

    /// <summary>The model in use; changes when the configured one turns out to be unavailable.</summary>
    public string CurrentModel { get; private set; } = endpoint.Model;

    public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
        ChatAsync(messages, MaxReplyTokens, 0.7, ct);

    /// <summary>
    /// A longer, more careful answer than plain chat allows, for example solving an exercise step by step.
    /// Reasoning models get extra room on top for their thinking.
    /// </summary>
    public async Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, double temperature, CancellationToken ct)
    {
        var messageArray = new JsonArray();
        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.Tool || message.ToolCalls.Count > 0) continue; // never sent to the cloud
            string role = message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.User => "user",
                _ => "assistant",
            };
            messageArray.Add(new JsonObject { ["role"] = role, ["content"] = message.Content });
        }

        bool reasoningModel = CurrentModel.StartsWith("openai/gpt-oss", StringComparison.OrdinalIgnoreCase);
        var body = new JsonObject
        {
            ["model"] = CurrentModel,
            ["messages"] = messageArray,
            ["max_tokens"] = reasoningModel ? Math.Max(MaxReasoningModelTokens, maxTokens + MaxReasoningModelTokens - MaxReplyTokens) : maxTokens,
            ["temperature"] = temperature,
        };
        if (reasoningModel)
        {
            // Groq's gpt-oss models: keep the thinking short and leave it out of the response.
            body["reasoning_effort"] = "low";
            body["include_reasoning"] = false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseAddress, "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

        using HttpResponseMessage response = await http.SendAsync(request, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new LlmException($"{endpoint.Name} returned {(int)response.StatusCode}: {ExtractError(text)}", (int)response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;

        string content = "";
        if (root.TryGetProperty("choices", out JsonElement choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out JsonElement reply)
            && reply.TryGetProperty("content", out JsonElement c)
            && c.ValueKind == JsonValueKind.String)
        {
            content = c.GetString() ?? "";
        }

        LlmUsage? usage = null;
        if (root.TryGetProperty("usage", out JsonElement u) && u.ValueKind == JsonValueKind.Object)
        {
            usage = new LlmUsage(
                u.TryGetProperty("prompt_tokens", out JsonElement p) && p.TryGetInt32(out int prompt) ? prompt : 0,
                u.TryGetProperty("completion_tokens", out JsonElement o) && o.TryGetInt32(out int completion) ? completion : 0);
        }

        string? model = root.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        return new LlmResponse(content.Trim(), []) { Usage = usage, Model = model };
    }

    /// <summary>
    /// After a "model not found": asks the provider which models this key may use and switches to the
    /// first fallback that is available. Returns false when there is nothing to switch to.
    /// </summary>
    public async Task<bool> SwitchToAvailableModelAsync(CancellationToken ct)
    {
        if (endpoint.FallbackModels.Count == 0) return false;

        IReadOnlyList<string> available = await ListModelsAsync(ct);
        string? next = endpoint.FallbackModels.FirstOrDefault(model =>
            !string.Equals(model, CurrentModel, StringComparison.OrdinalIgnoreCase) && available.Contains(model));
        if (next is null) return false;

        DiagnosticLog.Info($"{endpoint.Name}: model {CurrentModel} is unavailable, switching to {next}");
        CurrentModel = next;
        return true;
    }

    async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseAddress, "models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return Array.Empty<string>();
        return ParseModelIds(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Reads the ids from an OpenAI-style model list: {"data": [{"id": "..."}, ...]}.</summary>
    public static IReadOnlyList<string> ParseModelIds(string json)
    {
        var ids = new List<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                        ids.Add(id.GetString() ?? "");
                }
            }
        }
        catch (JsonException)
        {
            // Not a model list.
        }
        return ids;
    }

    // OpenAI-style errors look like {"error": {"message": "..."}}; some servers send {"error": "..."}.
    static string ExtractError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? body;
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String)
                    return message.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Plain-text error body.
        }
        return body.Length > 200 ? body[..200] : body;
    }
}
