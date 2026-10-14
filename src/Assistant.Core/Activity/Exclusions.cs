using System.Text.Json;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Activity;

/// <summary>The contents of exclusions.json.</summary>
public sealed class ExclusionRules
{
    public string[] Apps { get; set; } = [];
    public string[] WindowTitlesContaining { get; set; } = [];
}

/// <summary>
/// Apps and window titles that are never logged, from exclusions.json.
/// Edits to the file take effect within seconds, without a restart.
/// </summary>
public sealed class Exclusions(string path)
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

    ExclusionRules rules = new();
    DateTime loadedStamp = DateTime.MinValue;
    DateTime lastCheck = DateTime.MinValue;

    public bool IsExcluded(string app, string title)
    {
        ReloadIfChanged();
        return rules.Apps.Any(excluded => string.Equals(excluded, app, StringComparison.OrdinalIgnoreCase))
            || rules.WindowTitlesContaining.Any(part => part.Length > 0 && title.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    void ReloadIfChanged()
    {
        if (DateTime.UtcNow - lastCheck < CheckEvery) return;
        lastCheck = DateTime.UtcNow;

        DateTime stamp;
        try
        {
            if (!File.Exists(path)) return;
            stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == loadedStamp) return;
            rules = JsonSerializer.Deserialize<ExclusionRules>(File.ReadAllText(path), Options) ?? new ExclusionRules();
        }
        catch (IOException)
        {
            return; // the file is being saved; try again next time
        }
        catch (JsonException ex)
        {
            // Keep the previous rules rather than logging everything because of a typo.
            DiagnosticLog.Error("exclusions.json is not valid JSON; keeping the previous rules", ex);
            stamp = File.GetLastWriteTimeUtc(path);
        }
        loadedStamp = stamp;
    }
}
