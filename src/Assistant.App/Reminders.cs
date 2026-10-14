using System.Windows.Threading;
using Assistant.Core;

namespace Assistant.App;

/// <summary>
/// Timers set by asking ("set a timer for 5 minutes"). They live in memory only, so quitting the app
/// cancels them. When one is due, Due is raised on the UI thread with the text to show and say.
/// </summary>
public sealed class Reminders : IReminders
{
    const int MaxRunning = 5;

    readonly List<(DispatcherTimer Timer, string Label, DateTime DueAt)> running = [];

    public event Action<string>? Due;

    public bool Start(TimeSpan after, string label)
    {
        if (running.Count >= MaxRunning) return false;

        var timer = new DispatcherTimer { Interval = after };
        var entry = (timer, label, DateTime.Now + after);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            running.Remove(entry);
            Due?.Invoke(label.Length > 0 ? $"Time's up: {label}." : "Time's up!");
        };
        running.Add(entry);
        timer.Start();
        return true;
    }

    public IReadOnlyList<(string Label, TimeSpan Left)> Running =>
        running.OrderBy(entry => entry.DueAt)
               .Select(entry => (entry.Label, Max(TimeSpan.Zero, entry.DueAt - DateTime.Now)))
               .ToList();

    public int CancelAll()
    {
        int count = running.Count;
        foreach (var entry in running) entry.Timer.Stop();
        running.Clear();
        return count;
    }

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
