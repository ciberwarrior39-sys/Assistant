namespace Assistant.Core.Activity;

/// <summary>
/// Decides when a game (or fullscreen video, or a presentation) is in front, so the assistant can
/// free the GPU and get out of the way. Waits a couple of seconds before switching either way,
/// so a quick alt-tab doesn't make the character flicker.
/// </summary>
public sealed class GameModeDetector(IReadOnlyCollection<string> gameExecutables)
{
    const int SecondsToEnter = 2;
    const int SecondsToLeave = 5;

    int onStreak;
    int offStreak;

    public bool Active { get; private set; }
    public event Action<bool>? Changed;

    /// <summary>Call once per second with the latest snapshot.</summary>
    public void Observe(ForegroundSnapshot snapshot)
    {
        bool looksLikeGame = snapshot.ProcessId != Environment.ProcessId
            && (snapshot.FullscreenGame
                || snapshot.PresentationMode
                || snapshot.BorderlessFullscreen
                || gameExecutables.Contains(snapshot.App, StringComparer.OrdinalIgnoreCase));

        if (looksLikeGame)
        {
            onStreak++;
            offStreak = 0;
        }
        else
        {
            offStreak++;
            onStreak = 0;
        }

        if (!Active && onStreak >= SecondsToEnter)
        {
            Active = true;
            Changed?.Invoke(true);
        }
        else if (Active && offStreak >= SecondsToLeave)
        {
            Active = false;
            Changed?.Invoke(false);
        }
    }
}
