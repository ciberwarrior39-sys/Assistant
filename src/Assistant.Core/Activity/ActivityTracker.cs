namespace Assistant.Core.Activity;

/// <summary>
/// Turns the once-a-second snapshots into activity entries: a new entry whenever the app or
/// window title changes. Idle time, excluded windows and the assistant itself are not recorded.
/// </summary>
public sealed class ActivityTracker(ActivityLog log, Exclusions exclusions)
{
    static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);
    static readonly TimeSpan MinimumEntry = TimeSpan.FromSeconds(2); // skips alt-tab flicker

    readonly Lock gate = new();
    (string App, string Title, DateTime Start)? current;

    /// <summary>From config.json (activityLogging). Off means nothing is recorded.</summary>
    public bool Enabled { get; set; } = true;

    public void Observe(ForegroundSnapshot snapshot)
    {
        if (snapshot.ProcessId == Environment.ProcessId) return; // time spent in the assistant itself

        bool idle = snapshot.Idle >= IdleAfter;
        bool record = Enabled && !idle && snapshot.App.Length > 0
                      && !exclusions.IsExcluded(snapshot.App, snapshot.Title);
        DateTime now = WholeSeconds(snapshot.Time);

        lock (gate)
        {
            if (record && current is { } same && same.App == snapshot.App && same.Title == snapshot.Title)
                return; // still in the same window

            // The previous stretch ends now, or when the user went idle.
            Close(idle ? WholeSeconds(snapshot.Time - snapshot.Idle) : now);
            current = record ? (snapshot.App, snapshot.Title, now) : null;
        }
    }

    /// <summary>Writes the stretch in progress, for example when the app quits.</summary>
    public void Flush(DateTime now)
    {
        lock (gate)
        {
            Close(WholeSeconds(now));
            current = null;
        }
    }

    /// <summary>Recorded entries in the period, plus the one in progress.</summary>
    public IReadOnlyList<ActivityEntry> Read(DateTime from, DateTime to)
    {
        var entries = new List<ActivityEntry>(log.Read(from, to));
        lock (gate)
        {
            if (current is { } open && open.Start < to)
                entries.Add(new ActivityEntry(open.Start, WholeSeconds(DateTime.Now), open.App, open.Title));
        }
        return entries;
    }

    void Close(DateTime end)
    {
        if (current is not { } open) return;
        if (end - open.Start >= MinimumEntry) log.Append(new ActivityEntry(open.Start, end, open.App, open.Title));
    }

    static DateTime WholeSeconds(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, time.Kind);
}
