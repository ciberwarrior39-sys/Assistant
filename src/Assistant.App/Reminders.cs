using System.Globalization;
using System.Windows.Threading;
using Assistant.Core;
using Assistant.Core.Organizer;

namespace Assistant.App;

/// <summary>
/// Timers ("set a timer for 5 minutes") and reminders ("remind me tomorrow at 9 to call the bank"), kept in
/// reminders.json so they survive quitting and restarting. A clock checks once a second; when one is due, Due is raised
/// on the UI thread with the text to show and say. Whatever fell due while the app was closed is collected as missed.
/// </summary>
public sealed class Reminders : IReminders
{
    const int MaxWaiting = 50;

    readonly ReminderStore store;
    readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<ReminderEntry> missed;

    public event Action<string>? Due;

    public Reminders(ReminderStore store)
    {
        this.store = store;
        missed = store.TakeDue(DateTime.Now).ToList();
        clock.Tick += (_, _) =>
        {
            foreach (ReminderEntry entry in store.TakeDue(DateTime.Now)) Due?.Invoke(Message(entry));
        };
        clock.Start();
    }

    /// <summary>
    /// What fell due while the app wasn't running, as one sentence ("While I was off, you missed: …"), or null.
    /// Given once; afterwards it's empty.
    /// </summary>
    public string? TakeMissed()
    {
        if (missed.Count == 0) return null;
        string list = string.Join("; ", missed.Select(entry =>
            $"{(entry.Text.Length > 0 ? entry.Text : entry.Timer ? "a timer" : "a reminder")} " +
            $"(due {entry.Due.ToString(entry.Due.Date == DateTime.Today ? "HH:mm" : "ddd d MMM HH:mm", CultureInfo.InvariantCulture)})"));
        missed.Clear();
        return $"While I was off, you missed: {list}.";
    }

    public bool Start(TimeSpan after, string label) => Add(new ReminderEntry(DateTime.Now + after, label, Timer: true));

    public bool At(DateTime due, string label) => Add(new ReminderEntry(due, label, Timer: false));

    bool Add(ReminderEntry entry)
    {
        if (store.All.Count >= MaxWaiting) return false;
        store.Add(entry);
        return true;
    }

    public IReadOnlyList<(string Label, TimeSpan Left)> Running =>
        store.All.Select(entry => (entry.Text, Max(TimeSpan.Zero, entry.Due - DateTime.Now))).ToList();

    public IReadOnlyList<(string Label, DateTime Due, bool Timer)> Upcoming =>
        store.All.Select(entry => (entry.Text, entry.Due, entry.Timer)).ToList();

    public int CancelAll() => store.Clear();

    public IReadOnlyList<string> Cancel(string words) => store.Remove(words).Select(entry => entry.Text).ToList();

    static string Message(ReminderEntry entry) =>
        entry.Timer
            ? entry.Text.Length > 0 ? $"Time's up: {entry.Text}." : "Time's up!"
            : entry.Text.Length > 0 ? $"Reminder: {entry.Text}." : "Reminder!";

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
