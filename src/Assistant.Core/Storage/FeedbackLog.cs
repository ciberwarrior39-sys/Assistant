using System.Text.Encodings.Web;
using System.Text.Json;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Storage;

/// <summary>One hand-raise and what the user did with it. No search text or window titles.</summary>
public sealed record HandRaiseRecord(DateTime Time, string Kind, string App, string Outcome, int Threshold);

/// <summary>feedback\yyyy-MM-dd.jsonl: every hand-raise, accepted or ignored. Deleted after 7 days like the other logs.</summary>
public sealed class FeedbackLog(string folder)
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Append(HandRaiseRecord record)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, $"{record.Time:yyyy-MM-dd}.jsonl"),
                JsonSerializer.Serialize(record, Options) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Could not write the feedback log", ex);
        }
    }
}
