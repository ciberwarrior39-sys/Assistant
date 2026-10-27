using System.Net.Http;
using Assistant.Core.Diagnostics;
using Assistant.Core.Storage;

namespace Assistant.Core.Llm;

public sealed record CloudReply(string Text, string Provider);

/// <summary>
/// Tries each configured cloud provider in order (Groq first, then OpenRouter).
/// Every attempt is written to the sent\ log, including failed ones, because the data left the computer either way.
/// </summary>
public sealed class CloudChat(IReadOnlyList<OpenAiCompatibleProvider> providers, SentLog sentLog)
{
    public bool IsConfigured => providers.Count > 0;

    /// <summary>Returns null when no provider answered; the caller then uses the local model's reply.</summary>
    public Task<CloudReply?> AskAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct) => AskAsync(messages, null, ct);

    /// <param name="longAnswer">Room and temperature for a worked answer (solving an exercise); null for plain chat.</param>
    public async Task<CloudReply?> AskAsync(IReadOnlyList<ChatMessage> messages, (int MaxTokens, double Temperature)? longAnswer, CancellationToken ct)
    {
        foreach (OpenAiCompatibleProvider provider in providers)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    LlmResponse response = longAnswer is { } room
                        ? await provider.ChatAsync(messages, room.MaxTokens, room.Temperature, ct)
                        : await provider.ChatAsync(messages, [], ct);
                    sentLog.Record(provider.Name, response.Model ?? provider.ModelNow, messages, response.Usage, "ok");
                    if (response.Content.Length > 0) return new CloudReply(response.Content, provider.Name);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or LlmException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    sentLog.Record(provider.Name, provider.ModelNow, messages, null, $"failed: {ex.Message}");
                    DiagnosticLog.Error($"{provider.Name} failed", ex);

                    // 404 usually means the model was retired or isn't allowed for this key: switch to one the key
                    // can use and try once more. 429 means this model's per-minute quota is used up: another model
                    // (with its own quota) answers for a couple of minutes. Anything else (401 = bad key, offline)
                    // moves on to the next provider.
                    bool retry = attempt == 1
                        && ex is LlmException { StatusCode: 404 or 429 } failure
                        && (failure.StatusCode == 404
                            ? await provider.SwitchToAvailableModelAsync(ct)
                            : await provider.StandInAfterRateLimitAsync(ct));
                    if (!retry) break;
                }
            }
        }
        return null;
    }
}
