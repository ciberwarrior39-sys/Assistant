using System.Windows.Threading;

namespace Assistant.App;

// HandRaised: the silent "!" (it lowers after 30 seconds). Review: the weekly review's hand-raise,
// which stays up until the bubble is opened because the review waits up to 2 days.
// Listening: push-to-talk is held. Speaking: a reply is being read aloud.
public enum Mood { Idle, Thinking, FoundIt, Confused, Happy, Tired, Sleepy, Review, HandRaised, Listening, Speaking }

/// <summary>
/// The character's state machine. Every expression has a cause, and timed moods
/// return to idle on their own. The visuals only listen to Changed, so the real art can replace them.
/// </summary>
public sealed class CharacterMood
{
    static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(10);

    readonly DispatcherTimer revert = new();
    readonly DispatcherTimer sleepCheck = new() { Interval = TimeSpan.FromMinutes(1) };
    DateTime lastInteraction = DateTime.Now;

    public Mood Current { get; private set; } = Mood.Idle;
    public event Action<Mood>? Changed;

    public CharacterMood()
    {
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            Set(Mood.Idle);
        };
        sleepCheck.Tick += (_, _) =>
        {
            if (Current == Mood.Idle && DateTime.Now - lastInteraction > SleepAfter) Set(Mood.Sleepy);
        };
        sleepCheck.Start();
    }

    /// <summary>Sets a mood. With holdFor, the character returns to idle afterwards.</summary>
    public void Set(Mood mood, TimeSpan? holdFor = null)
    {
        revert.Stop();
        if (mood != Current)
        {
            Current = mood;
            Changed?.Invoke(mood);
        }
        if (holdFor is { } duration)
        {
            revert.Interval = duration;
            revert.Start();
        }
    }

    /// <summary>The user did something with the assistant: wake up if asleep.</summary>
    public void Touch()
    {
        lastInteraction = DateTime.Now;
        if (Current == Mood.Sleepy) Set(Mood.Idle);
    }
}
