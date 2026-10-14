using System.Text.Encodings.Web;
using System.Text.Json;
using Assistant.Core.Diagnostics;
using Assistant.Core.Llm;

namespace Assistant.Core.Storage;

/// <summary>
/// sent\yyyy-MM-dd.jsonl: exactly what was sent to a cloud model, one line per request,
/// so the user can always check what left their computer.
/// </summary>
public sealed class SentLog(string folder)
{
    static readonly Lock Gate = new();

    // Readable in Notepad: no \u escapes for accents, Cyrillic or quotes.
    static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Record(string provider, string? model, IReadOnlyList<ChatMessage> messages, LlmUsage? usage, string outcome)
    {
        var entry = new
        {
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            provider,
            model,
            outcome,
            promptTokens = usage?.PromptTokens,
            replyTokens = usage?.CompletionTokens,
            messages = messages
                .Where(message => message.Role != ChatRole.Tool && message.ToolCalls.Count == 0)
                .Select(message => new { role = message.Role.ToString().ToLowerInvariant(), content = message.Content }),
        };

        try
        {
            string line = JsonSerializer.Serialize(entry, Options);
            lock (Gate)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd}.jsonl"), line + Environment.NewLine);
            }
        }
        catch (IOException ex)
        {
            DiagnosticLog.Error("Could not write the sent log", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            DiagnosticLog.Error("Could not write the sent log", ex);
        }
    }
}
