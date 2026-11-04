using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Llm;

/// <summary>
/// Groq, OpenRouter and other OpenAI-compatible cloud APIs. Used for plain conversation only: it never gets the app's
/// own tools (files, memory, the screen), only the provider's built-in web search and code runner where offered,
/// and the caller decides which messages may leave the computer.
/// </summary>
public sealed partial class OpenAiCompatibleProvider(CloudEndpoint endpoint, HttpClient http) : ILlmProvider
{
    const int MaxSources = 5;

    // Set when the provider refuses a request with web search and code (not offered on this key or plan): from then
    // on, until the app restarts, requests go without them, so every answer still arrives.
    bool webToolsRefused;

    // Reasoning models think before answering, and that thinking needs room too.
    const int MaxReasoningModelTokens = 1024;

    public string Name => endpoint.Name;

    /// <summary>The model in use; changes when the configured one turns out to be unavailable.</summary>
    public string CurrentModel { get; private set; } = endpoint.Model;

    // After a "too many requests" (each model has its own per-minute quota), another model answers for a while.
    string? standIn;
    DateTime standInUntil;
    static readonly TimeSpan StandInFor = TimeSpan.FromMinutes(2);

    /// <summary>The model the next request goes to: the stand-in while one is active, otherwise the current one.</summary>
    public string ModelNow => standIn is not null && DateTime.UtcNow < standInUntil ? standIn : CurrentModel;

    public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
        ChatAsync(messages, CloudRequest.Chat, ct);

    /// <summary>A longer, more careful answer than plain chat allows, for example solving an exercise step by step.</summary>
    public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, double temperature, CancellationToken ct) =>
        ChatAsync(messages, new CloudRequest(maxTokens, temperature), ct);

    /// <summary>
    /// Reasoning models get extra room on top of MaxTokens for their thinking, and more again when they may search the
    /// web or run code (Groq's gpt-oss only; the work happens on Groq's servers and one finished answer comes back).
    /// </summary>
    public async Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, CloudRequest request, CancellationToken ct)
    {
        int maxTokens = request.MaxTokens;
        double temperature = request.Temperature;
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

        string model = ModelNow;
        bool reasoningModel = model.StartsWith("openai/gpt-oss", StringComparison.OrdinalIgnoreCase);
        bool webTools = (request.Search || request.Code) && reasoningModel && !webToolsRefused
                        && endpoint.Name.Equals("Groq", StringComparison.OrdinalIgnoreCase);
        int thinking = request.ReasoningEffort == "low" ? MaxReasoningModelTokens : 2 * MaxReasoningModelTokens;
        bool search = webTools && request.Search;
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messageArray,
            ["max_tokens"] = reasoningModel ? maxTokens + thinking + (search ? 2048 : 0) : maxTokens,
            ["temperature"] = temperature,
        };
        if (reasoningModel)
        {
            // Groq's gpt-oss models: think as much as the request allows, and leave the thinking out of the response.
            body["reasoning_effort"] = request.ReasoningEffort;
            body["include_reasoning"] = false;
        }
        if (webTools)
        {
            var tools = new JsonArray();
            if (request.Search) tools.Add(new JsonObject { ["type"] = "browser_search" });
            if (request.Code) tools.Add(new JsonObject { ["type"] = "code_interpreter" }); // the model decides whether to run any
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseAddress, "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

        using HttpResponseMessage response = await http.SendAsync(httpRequest, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // A request with web search and code that's refused as such (not a quota, key or server problem): ask again
            // without them, and don't offer them again until the next start.
            if (webTools && (int)response.StatusCode is 400 or 403 or 422)
            {
                webToolsRefused = true;
                DiagnosticLog.Info($"{endpoint.Name} refused web search and code ({(int)response.StatusCode}: {ExtractError(text)}); answering without them");
                return await ChatAsync(messages, request with { Search = false, Code = false }, ct);
            }
            throw new LlmException($"{endpoint.Name} returned {(int)response.StatusCode}: {ExtractError(text)}", (int)response.StatusCode);
        }

        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;

        string content = "";
        var sources = new List<WebSource>();
        if (root.TryGetProperty("choices", out JsonElement choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out JsonElement reply))
        {
            if (reply.TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.String)
                content = c.GetString() ?? "";
            // What the web search found: anywhere under executed_tools, objects with a title and a web address.
            if (reply.TryGetProperty("executed_tools", out JsonElement executed)) CollectSources(executed, sources);
        }
        // "【3†L10-L14】": the model's markers pointing into its search results; the sources are listed separately.
        content = CitationMarker().Replace(content, "");

        LlmUsage? usage = null;
        if (root.TryGetProperty("usage", out JsonElement u) && u.ValueKind == JsonValueKind.Object)
        {
            usage = new LlmUsage(
                u.TryGetProperty("prompt_tokens", out JsonElement p) && p.TryGetInt32(out int prompt) ? prompt : 0,
                u.TryGetProperty("completion_tokens", out JsonElement o) && o.TryGetInt32(out int completion) ? completion : 0);
        }

        string? answeredBy = root.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        return new LlmResponse(content.Trim(), []) { Usage = usage, Model = answeredBy, Sources = sources };
    }

    static void CollectSources(JsonElement element, List<WebSource> sources)
    {
        if (sources.Count >= MaxSources) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? address)
                    && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp)
                    && sources.All(source => source.Url != address.AbsoluteUri))
                {
                    string title = element.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String
                        ? (t.GetString() ?? "").Trim()
                        : "";
                    sources.Add(new WebSource(title.Length > 0 ? title : address.Host, address.AbsoluteUri));
                }
                foreach (JsonProperty property in element.EnumerateObject()) CollectSources(property.Value, sources);
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray()) CollectSources(item, sources);
                break;
        }
    }

    [GeneratedRegex(@"\s?【[^】]*】")]
    private static partial Regex CitationMarker();

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

    /// <summary>
    /// After a 429 (this model's free per-minute quota is used up): the next fallback model the key may use answers
    /// for the next 2 minutes. Returns false when there is none, or a stand-in is already in use.
    /// </summary>
    public async Task<bool> StandInAfterRateLimitAsync(CancellationToken ct)
    {
        if (endpoint.FallbackModels.Count == 0 || ModelNow != CurrentModel) return false;
        IReadOnlyList<string> available = await ListModelsAsync(ct);
        string? next = endpoint.FallbackModels.FirstOrDefault(model =>
            !string.Equals(model, CurrentModel, StringComparison.OrdinalIgnoreCase) && available.Contains(model));
        if (next is null) return false;

        DiagnosticLog.Info($"{endpoint.Name}: {CurrentModel} is rate limited, {next} answers for {StandInFor.TotalMinutes:0} minutes");
        standIn = next;
        standInUntil = DateTime.UtcNow + StandInFor;
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
