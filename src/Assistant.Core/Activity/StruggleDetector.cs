namespace Assistant.Core.Activity;

public enum StruggleKind { SearchLoop, Stuck }

/// <summary>A reason to raise a hand. App is the .exe the user is working in; Detail is a search query, if any.</summary>
public sealed record StruggleSignal(StruggleKind Kind, string App, string Detail);

/// <summary>
/// Spots two signs that the user might want help, from the same once-a-second foreground snapshots
/// as the activity log (no keyboard hook, no reading other apps' text):
/// - Search loop: going back and forth between an app and search pages in a browser.
/// - Stuck: a burst of window switching, then sitting in one window without touching anything.
/// Excluded apps and windows never count. The thresholds come from config.json and grow when hand-raises are ignored.
/// </summary>
public sealed class StruggleDetector(Exclusions exclusions)
{
    static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(5);
    static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(3);
    static readonly TimeSpan StuckFor = TimeSpan.FromSeconds(60); // in the same window...
    static readonly TimeSpan StillFor = TimeSpan.FromSeconds(45); // ...without input for this long
    static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(5); // longer means away, not stuck
    static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    // Words in a browser tab's title that mean the user is looking something up.
    static readonly string[] SearchMarkers =
    [
        "Google Search", "- Bing", "DuckDuckGo", "Stack Overflow", "Stack Exchange", "Super User",
        "Microsoft Learn", "Unity Discussions", "Unity Forum", "Reddit", "GitHub Issue",
    ];

    sealed record Visit(string App, string Title, DateTime Start);

    readonly List<Visit> visits = [];

    /// <summary>Round trips app → search → app within 5 minutes that raise a hand.</summary>
    public int SearchLoopTrips { get; set; } = 3;

    /// <summary>Window switches within 3 minutes that count as a burst before sitting still.</summary>
    public int StuckSwitches { get; set; } = 10;

    /// <summary>Call once per second. Returns a signal at most once per pattern; the history is cleared after one.</summary>
    public StruggleSignal? Observe(ForegroundSnapshot snapshot)
    {
        if (snapshot.ProcessId == Environment.ProcessId || snapshot.App.Length == 0) return null;
        if (snapshot.Idle >= AwayAfter)
        {
            visits.Clear(); // the user left; whatever happened before doesn't count
            return null;
        }
        if (exclusions.IsExcluded(snapshot.App, snapshot.Title)) return null;

        visits.RemoveAll(visit => snapshot.Time - visit.Start > Keep);
        if (visits.Count == 0 || visits[^1].App != snapshot.App || visits[^1].Title != snapshot.Title)
        {
            visits.Add(new Visit(snapshot.App, snapshot.Title, snapshot.Time));
            if (SearchLoop(snapshot.Time) is { } loop) return Fire(loop);
        }
        return Stuck(snapshot) is { } stuck ? Fire(stuck) : null;
    }

    StruggleSignal Fire(StruggleSignal signal)
    {
        visits.Clear();
        return signal;
    }

    // Counts "app X, then a search page, then app X again" in the last 5 minutes, for the app the user just returned to.
    StruggleSignal? SearchLoop(DateTime now)
    {
        Visit current = visits[^1];
        if (AppNames.IsBrowser(current.App)) return null;

        var recent = visits.Where(visit => now - visit.Start <= LoopWindow).ToList();
        int trips = 0;
        string lastQuery = "";
        int i = 0;
        while (i < recent.Count)
        {
            if (!AppNames.IsBrowser(recent[i].App))
            {
                i++;
                continue;
            }

            // A run of browser tabs (several searches in a row count as one trip), between two visits to the app.
            int start = i;
            while (i < recent.Count && AppNames.IsBrowser(recent[i].App)) i++;
            List<Visit> run = recent.GetRange(start, i - start);
            if (start > 0 && i < recent.Count
                && recent[start - 1].App == current.App && recent[i].App == current.App
                && run.Any(IsSearchPage))
            {
                trips++;
                lastQuery = Query(run.Last(IsSearchPage).Title);
            }
        }
        return trips >= SearchLoopTrips ? new StruggleSignal(StruggleKind.SearchLoop, current.App, lastQuery) : null;
    }

    // A burst of switching before the current window, then a minute in it with no input for 45 seconds.
    StruggleSignal? Stuck(ForegroundSnapshot snapshot)
    {
        Visit current = visits[^1];
        if (snapshot.Time - current.Start < StuckFor || snapshot.Idle < StillFor) return null;

        int switches = visits.Count(visit => visit.Start < current.Start && current.Start - visit.Start <= BurstWindow);
        return switches >= StuckSwitches ? new StruggleSignal(StruggleKind.Stuck, current.App, "") : null;
    }

    static bool IsSearchPage(Visit visit) =>
        AppNames.IsBrowser(visit.App)
        && SearchMarkers.Any(marker => visit.Title.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // "unity rigidbody not moving - Google Search - Google Chrome" -> "unity rigidbody not moving"
    static string Query(string title)
    {
        string[] parts = title.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string query = parts.Length > 0 ? parts[0] : "";
        return query.Length > 60 ? query[..60] + "…" : query;
    }
}
