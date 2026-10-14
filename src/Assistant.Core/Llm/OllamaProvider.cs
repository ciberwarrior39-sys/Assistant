using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant.Core.Llm;

/// <summary>Talks to a local Ollama server through its /api/chat endpoint, with tool calling.</summary>
public sealed class OllamaProvider(HttpClient http, string model, int contextSize) : ILlmProvider
{
    // Thinking makes answers slower and adds nothing for short tool-routing replies.
    // Models without a thinking switch reject the flag, so it is dropped after the first such error.
    bool sendThinkFlag = true;

    public string Name => $"Ollama ({model})";

    public async Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        JsonObject body = BuildBody(messages, tools);
        using HttpResponseMessage response = await PostChatAsync(body, ct);

        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            throw new LlmException($"{(int)response.StatusCode}: {ExtractError(error)}");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseResponse(document.RootElement);
    }

    /// <summary>Frees the model's VRAM right away (game mode). It reloads on the next request.</summary>
    public async Task UnloadAsync(CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = model, ["keep_alive"] = 0 };
        try
        {
            using HttpResponseMessage response = await http.PostAsync("api/generate", AsContent(body), ct);
        }
        catch (HttpRequestException)
        {
            // Ollama isn't running, so nothing is loaded.
        }
        catch (TaskCanceledException)
        {
        }
    }

    async Task<HttpResponseMessage> PostChatAsync(JsonObject body, CancellationToken ct)
    {
        if (sendThinkFlag) body["think"] = false;
        HttpResponseMessage response = await http.PostAsync("api/chat", AsContent(body), ct);

        if (response.StatusCode == HttpStatusCode.BadRequest && sendThinkFlag)
        {
            string error = await response.Content.ReadAsStringAsync(ct);
            if (error.Contains("think", StringComparison.OrdinalIgnoreCase))
            {
                sendThinkFlag = false;
                body.Remove("think");
                response.Dispose();
                response = await http.PostAsync("api/chat", AsContent(body), ct);
            }
        }
        return response;
    }

    JsonObject BuildBody(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools)
    {
        var messageArray = new JsonArray();
        foreach (ChatMessage message in messages) messageArray.Add(ToJson(message));

        var toolArray = new JsonArray();
        foreach (ToolDefinition tool in tools)
        {
            toolArray.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.Parameters.DeepClone(),
                },
            });
        }

        return new JsonObject
        {
            ["model"] = model,
            ["messages"] = messageArray,
            ["tools"] = toolArray,
            ["stream"] = false,
            ["options"] = new JsonObject { ["num_ctx"] = contextSize },
        };
    }

    static JsonObject ToJson(ChatMessage message)
    {
        string role = message.Role switch
        {
            ChatRole.System => "system",
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            _ => "tool",
        };
        var json = new JsonObject { ["role"] = role, ["content"] = message.Content };

        if (message.ToolCalls.Count > 0)
        {
            var calls = new JsonArray();
            foreach (ToolCall call in message.ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = JsonNode.Parse(call.Arguments.GetRawText()),
                    },
                });
            }
            json["tool_calls"] = calls;
        }

        if (message.ToolName is not null) json["tool_name"] = message.ToolName;
        if (message.Images.Count > 0) json["images"] = new JsonArray(message.Images.Select(image => (JsonNode?)JsonValue.Create(image)).ToArray());
        return json;
    }

    static LlmResponse ParseResponse(JsonElement root)
    {
        JsonElement message = root.GetProperty("message");
        string content = message.TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? ""
            : "";

        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("function", out JsonElement function)) continue;
                string name = function.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? ""
                    : "";
                JsonElement arguments = function.TryGetProperty("arguments", out JsonElement a)
                    ? NormalizeArguments(a)
                    : EmptyObject();
                calls.Add(new ToolCall(name, arguments));
            }
        }

        return new LlmResponse(content.Trim(), calls);
    }

    // Small models sometimes send arguments as a JSON string instead of an object.
    static JsonElement NormalizeArguments(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) return value.Clone();
        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                using JsonDocument parsed = JsonDocument.Parse(value.GetString() ?? "{}");
                if (parsed.RootElement.ValueKind == JsonValueKind.Object) return parsed.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not JSON: fall through to an empty object, and the tool reports missing arguments.
            }
        }
        return EmptyObject();
    }

    static JsonElement EmptyObject()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    static string ExtractError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
                return error.GetString() ?? body;
        }
        catch (JsonException)
        {
            // Plain-text error body.
        }
        return body.Length > 200 ? body[..200] : body;
    }

    static StringContent AsContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
