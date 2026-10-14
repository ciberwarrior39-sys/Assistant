using System.IO;
using System.Windows;
using System.Windows.Threading;
using Assistant.Core.Activity;
using Assistant.Core.Diagnostics;
using Assistant.Core.Storage;

namespace Assistant.App;

/// <summary>
/// The silent hand-raise: a "!" over the character, nothing more. Opening the chat while it's up shows
/// why the hand went up. Ignored for 30 seconds, the hand lowers and that's logged; three ignores in a row
/// of one kind make that kind harder to trigger (the new threshold is saved in config.json).
/// </summary>
public sealed class HandRaiser
{
    static readonly TimeSpan LowerAfter = TimeSpan.FromSeconds(30);
    static readonly TimeSpan QuietAfterAny = TimeSpan.FromMinutes(10);
    static readonly TimeSpan QuietAfterSameKind = TimeSpan.FromMinutes(30);
    const int IgnoresBeforeTuning = 3;
    const int MaxSearchLoopTrips = 10;
    const int MaxStuckSwitches = 30;

    readonly CharacterMood mood;
    readonly ChatBubbleWindow bubble;
    readonly Window character;
    readonly StruggleDetector detector;
    readonly FeedbackLog feedback;
    readonly AppConfig config;
    readonly string configPath;
    readonly DispatcherTimer lower = new() { Interval = LowerAfter };
    readonly Dictionary<StruggleKind, DateTime> lastRaised = [];
    readonly Dictionary<StruggleKind, int> ignoredInARow = [];
    DateTime lastAny = DateTime.MinValue;
    StruggleSignal? raised;

    public HandRaiser(CharacterMood mood, ChatBubbleWindow bubble, Window character, StruggleDetector detector,
                      FeedbackLog feedback, AppConfig config, string configPath)
    {
        this.mood = mood;
        this.bubble = bubble;
        this.character = character;
        this.detector = detector;
        this.feedback = feedback;
        this.config = config;
        this.configPath = configPath;

        detector.SearchLoopTrips = config.SearchLoopTrips;
        detector.StuckSwitches = config.StuckSwitches;

        lower.Tick += (_, _) => Lower();
        bubble.IsVisibleChanged += (_, _) =>
        {
            if (bubble.IsVisible && raised is { } signal) Accept(signal);
        };
    }

    /// <summary>Game mode: no hand-raises, and one that is up goes down without counting as ignored.</summary>
    public bool Paused { get; set; }

    public void Observe(ForegroundSnapshot snapshot)
    {
        if (!config.HandRaiseEnabled) return;
        StruggleSignal? signal = detector.Observe(snapshot);
        if (signal is not null) Offer(signal, snapshot.Time);
    }

    void Offer(StruggleSignal signal, DateTime now)
    {
        // Never interrupt: not in a game, not while chatting or hidden, not over another expression.
        if (Paused || raised is not null || bubble.IsVisible || !character.IsVisible) return;
        if (mood.Current is not (Mood.Idle or Mood.Sleepy)) return;
        if (now - lastAny < QuietAfterAny) return;
        if (lastRaised.TryGetValue(signal.Kind, out DateTime last) && now - last < QuietAfterSameKind) return;

        raised = signal;
        lastAny = now;
        lastRaised[signal.Kind] = now;
        mood.Set(Mood.HandRaised);
        lower.Start();
        DiagnosticLog.Info($"Hand raised: {signal.Kind}");
    }

    void Accept(StruggleSignal signal)
    {
        lower.Stop();
        raised = null;
        if (mood.Current == Mood.HandRaised) mood.Set(Mood.Idle);
        ignoredInARow[signal.Kind] = 0;
        Log(signal, "accepted");
        bubble.Say(Message(signal));
    }

    void Lower()
    {
        lower.Stop();
        if (raised is not { } signal) return;
        raised = null;
        if (mood.Current == Mood.HandRaised) mood.Set(Mood.Idle);
        if (Paused || !character.IsVisible) return; // a game started or it was hidden: the user never saw it

        Log(signal, "ignored");
        int ignored = ignoredInARow.GetValueOrDefault(signal.Kind) + 1;
        ignoredInARow[signal.Kind] = ignored;
        if (ignored >= IgnoresBeforeTuning)
        {
            ignoredInARow[signal.Kind] = 0;
            RaiseThreshold(signal.Kind);
        }
    }

    /// <summary>Lowers a raised hand now, for example when a game starts.</summary>
    public void LowerNow()
    {
        if (raised is not null) Lower();
    }

    void RaiseThreshold(StruggleKind kind)
    {
        if (kind == StruggleKind.SearchLoop)
            config.SearchLoopTrips = detector.SearchLoopTrips = Math.Min(MaxSearchLoopTrips, detector.SearchLoopTrips + 1);
        else
            config.StuckSwitches = detector.StuckSwitches = Math.Min(MaxStuckSwitches, detector.StuckSwitches + 1);

        try
        {
            config.Save(configPath);
            DiagnosticLog.Info($"Hand-raise ignored {IgnoresBeforeTuning} times in a row; {kind} threshold raised");
        }
        catch (IOException ex)
        {
            DiagnosticLog.Error("Could not save the new hand-raise threshold", ex);
        }
    }

    void Log(StruggleSignal signal, string outcome) =>
        feedback.Append(new HandRaiseRecord(DateTime.Now, signal.Kind.ToString(), signal.App, outcome,
            signal.Kind == StruggleKind.SearchLoop ? detector.SearchLoopTrips : detector.StuckSwitches));

    static string Message(StruggleSignal signal)
    {
        string app = AppNames.Name(signal.App);
        return signal.Kind switch
        {
            StruggleKind.SearchLoop when signal.Detail.Length > 0 =>
                $"You've been going back and forth between {app} and searches, last for \"{signal.Detail}\". " +
                "Stuck on something? Tell me about it and I'll try to help.",
            StruggleKind.SearchLoop =>
                $"You've been going back and forth between {app} and searches. Stuck on something? Tell me about it.",
            _ => $"You were switching around a lot and now seem stuck in {app}. Want to talk it through?",
        };
    }
}
