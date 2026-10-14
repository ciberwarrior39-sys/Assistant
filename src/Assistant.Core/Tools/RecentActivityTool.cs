using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core.Activity;
using Assistant.Core.Llm;

namespace Assistant.Core.Tools;

/// <summary>
/// get_recent_activity: which apps and windows the user spent time in. The model picks a period from a
/// fixed list and the code does the time math (rules from the model test). The answer stays on this
/// computer: a turn that uses a tool is never sent to the cloud.
/// </summary>
public sealed class RecentActivityTool(ActivityTracker tracker) : ITool
{
    const int MaxApps = 6;
    const int MaxTitlesPerApp = 3;
    const int MaxTitleLength = 70;

    static readonly string[] Periods = ["last_hour", "today", "yesterday", "past_week"];

    public ToolDefinition Definition { get; } = new(
        "get_recent_activity",
        "What the user did on this computer: which apps and windows were active, and for how long.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["period"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("last_hour", "today", "yesterday", "past_week"),
                    ["description"] = "Which stretch of time to look at.",
                },
            },
            ["required"] = new JsonArray("period"),
        });

    public Task<ToolOutcome> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (!tracker.Enabled)
            return Task.FromResult(new ToolOutcome("Activity logging is turned off (activityLogging in config.json). Tell the user."));

        string period = arguments.ValueKind == JsonValueKind.Object
                        && arguments.TryGetProperty("period", out JsonElement value)
                        && value.ValueKind == JsonValueKind.String
                        && Periods.Contains(value.GetString())
            ? value.GetString()!
            : "today";

        DateTime now = DateTime.Now;
        (DateTime from, DateTime to, string label) = period switch
        {
            "last_hour" => (now.AddHours(-1), now, "in the last hour"),
            "yesterday" => (now.Date.AddDays(-1), now.Date, "yesterday"),
            "past_week" => (now.Date.AddDays(-6), now, "over the past 7 days"),
            _ => (now.Date, now, "today"),
        };

        IReadOnlyList<ActivityEntry> entries = tracker.Read(from, to);
        return Task.FromResult(new ToolOutcome(Summarize(entries, from, to, label)));
    }

    // Compact on purpose: the local model has a 2048-token context.
    static string Summarize(IReadOnlyList<ActivityEntry> entries, DateTime from, DateTime to, string label)
    {
        var stretches = entries
            .Select(entry => (entry.App, entry.Title,
                              Seconds: (Min(entry.End, to) - Max(entry.Start, from)).TotalSeconds))
            .Where(stretch => stretch.Seconds > 0)
            .ToList();

        if (stretches.Count == 0) return $"Nothing was recorded {label}.";

        var apps = stretches
            .GroupBy(stretch => stretch.App, StringComparer.OrdinalIgnoreCase)
            .Select(app => (
                Name: AppName(app.Key),
                Seconds: app.Sum(stretch => stretch.Seconds),
                Titles: app.GroupBy(stretch => stretch.Title)
                           .Select(title => (Title: title.Key, Seconds: title.Sum(stretch => stretch.Seconds)))
                           .OrderByDescending(title => title.Seconds)
                           .Take(MaxTitlesPerApp)
                           .ToList()))
            .OrderByDescending(app => app.Seconds)
            .Take(MaxApps);

        var text = new StringBuilder($"Active time {label} (idle time not counted), total {Duration(stretches.Sum(s => s.Seconds))}:\n");
        foreach (var app in apps)
        {
            text.AppendLine($"- {app.Name}: {Duration(app.Seconds)}");
            foreach (var title in app.Titles.Where(title => title.Title.Length > 0))
                text.AppendLine($"  - \"{Shorten(title.Title)}\" {Duration(title.Seconds)}");
        }
        return text.ToString();
    }

    static string AppName(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    static string Shorten(string title) => title.Length > MaxTitleLength ? title[..MaxTitleLength] + "…" : title;

    static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalMinutes < 1) return "under 1 min";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min";
        return $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
