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
public sealed class RecentActivityTool(ActivityTracker tracker, Func<string>? currentUserText = null) : ITool
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
            return Task.FromResult(new ToolOutcome("Activity logging is turned off (activityLogging in config.json). Tell the user.")
            {
                Reply = "The activity log is turned off, so I don't know. You can turn it on in Settings > Features.",
            });

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
        List<AppTime> apps = Totals(entries, from, to);
        return Task.FromResult(new ToolOutcome(Summarize(apps, label)) { Reply = Answer(apps, label, currentUserText?.Invoke() ?? "") });
    }

    sealed record AppTime(string Name, double Seconds, List<(string Title, double Seconds)> Titles);

    static List<AppTime> Totals(IReadOnlyList<ActivityEntry> entries, DateTime from, DateTime to)
    {
        var stretches = entries
            .Select(entry => (entry.App, entry.Title,
                              Seconds: (Min(entry.End, to) - Max(entry.Start, from)).TotalSeconds))
            .Where(stretch => stretch.Seconds > 0)
            .ToList();

        return stretches
            .GroupBy(stretch => stretch.App, StringComparer.OrdinalIgnoreCase)
            .Select(app => new AppTime(
                AppName(app.Key),
                app.Sum(stretch => stretch.Seconds),
                app.GroupBy(stretch => stretch.Title)
                   .Select(title => (Title: title.Key, Seconds: title.Sum(stretch => stretch.Seconds)))
                   .OrderByDescending(title => title.Seconds)
                   .Take(MaxTitlesPerApp)
                   .ToList()))
            .OrderByDescending(app => app.Seconds)
            .ToList();
    }

    // Compact on purpose: the local model has a 2048-token context.
    static string Summarize(List<AppTime> all, string label)
    {
        if (all.Count == 0) return $"Nothing was recorded {label}.";
        IEnumerable<AppTime> apps = all.Take(MaxApps);

        var text = new StringBuilder($"Active time {label} (idle time not counted), total {Duration(all.Sum(app => app.Seconds))}:\n");
        foreach (var app in apps)
        {
            text.AppendLine($"- {app.Name}: {Duration(app.Seconds)}");
            foreach (var title in app.Titles.Where(title => title.Title.Length > 0))
                text.AppendLine($"  - \"{Shorten(title.Title)}\" {Duration(title.Seconds)}");
        }
        return text.ToString();
    }

    // The reply, from the numbers (no model call): one app if the question names it, otherwise the top three.
    static string Answer(List<AppTime> apps, string label, string question)
    {
        string when = char.ToUpperInvariant(label[0]) + label[1..];
        if (apps.Count == 0) return $"{when} I didn't see you using anything (idle time isn't counted).";

        AppTime? asked = apps.FirstOrDefault(app => app.Name.Length >= 3 && question.Contains(app.Name, StringComparison.OrdinalIgnoreCase));
        if (asked is not null)
            return $"{when} you spent {Duration(asked.Seconds)} in {asked.Name}, out of {Duration(apps.Sum(app => app.Seconds))} of active time.";

        var top = apps.Take(3).ToList();
        string list = top.Count switch
        {
            1 => $"all of it in {Part(top[0])}",
            2 => $"mostly in {Part(top[0])}, then {Part(top[1])}",
            _ => $"mostly in {Part(top[0])}, then {Part(top[1])} and {Part(top[2])}",
        };
        string reply = $"{when} you were active for {Duration(apps.Sum(app => app.Seconds))}, {list}.";
        if (top[0].Titles.FirstOrDefault(title => title.Title.Length > 0) is { Title.Length: > 0 } main)
            reply += $" In {top[0].Name} it was mostly \"{Shorten(main.Title)}\".";
        return reply;
    }

    static string Part(AppTime app) => $"{app.Name} ({Duration(app.Seconds)})";

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
