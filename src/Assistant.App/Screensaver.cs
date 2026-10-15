using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Assistant.Core.Activity;
using Assistant.Core.Diagnostics;
using Assistant.Core.Storage;

namespace Assistant.App;

/// <summary>
/// The screensaver: a few minutes before Windows turns the screen off, the character bounces around the
/// main screen over the desktop, changing color at every edge like the old DVD logo. Any mouse or keyboard
/// input sends it back to its spot. It never changes Windows' power settings and doesn't keep the PC awake.
/// </summary>
public sealed class Screensaver
{
    const double Speed = 170;                           // screen units per second
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33); // about 30 fps, like the rest of the character
    static readonly TimeSpan RecheckPower = TimeSpan.FromMinutes(1);
    static readonly TimeSpan MinimumIdle = TimeSpan.FromSeconds(30);

    readonly CharacterWindow character;
    readonly CharacterMood mood;
    readonly ChatBubbleWindow bubble;
    readonly AppConfig config;
    readonly DispatcherTimer frame = new(DispatcherPriority.Render) { Interval = Frame };
    readonly Stopwatch clock = new();
    TimeSpan? screenOff;
    DateTime powerReadAt = DateTime.MinValue;
    Point home;
    Vector velocity;
    DateTime startedAt;
    int colorIndex;

    public Screensaver(CharacterWindow character, CharacterMood mood, ChatBubbleWindow bubble, AppConfig config)
    {
        this.character = character;
        this.mood = mood;
        this.bubble = bubble;
        this.config = config;
        frame.Tick += (_, _) => Step();
    }

    public bool Running { get; private set; }

    /// <summary>Called once a second with the latest snapshot; starts when the time has come.</summary>
    public void Observe(ForegroundSnapshot snapshot, bool gaming)
    {
        if (Running || !config.ScreensaverEnabled || gaming) return;
        if (snapshot.FullscreenGame || snapshot.PresentationMode || snapshot.BorderlessFullscreen) return; // a movie or a game
        if (!character.IsVisible || bubble.IsVisible || mood.Current is not (Mood.Idle or Mood.Sleepy)) return;
        if (snapshot.Idle < StartAfter()) return;
        if (Power.ScreenKeptOn()) return; // a video player (or similar) asked Windows to keep the screen on

        Start(DateTime.Now - snapshot.Idle);
    }

    /// <summary>"Try it now" in Settings: starts in 2 seconds, so the click itself doesn't stop it.</summary>
    public void Try()
    {
        if (Running) return;
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            bubble.Hide();
            character.ShowAgain();
            Start(DateTime.Now);
        };
        delay.Start();
    }

    /// <summary>When to start: a few minutes before the screen turns off, or the fallback if it never does.</summary>
    public TimeSpan StartAfter()
    {
        if (DateTime.Now - powerReadAt > RecheckPower)
        {
            screenOff = Power.ScreenOffAfter();
            powerReadAt = DateTime.Now;
        }
        if (screenOff is not { } off) return TimeSpan.FromMinutes(Math.Max(1, config.ScreensaverFallbackMinutes));

        // Short screen-off times get half of the time instead, so there's still something to see.
        var lead = TimeSpan.FromMinutes(Math.Clamp(config.ScreensaverMinutesBeforeScreenOff, 1, 30));
        TimeSpan start = off - lead < off / 2 ? off / 2 : off - lead;
        return start < MinimumIdle ? MinimumIdle : start;
    }

    void Start(DateTime lastInput)
    {
        Running = true;
        startedAt = DateTime.Now > lastInput ? DateTime.Now : lastInput;
        home = new Point(character.Left, character.Top);
        mood.Set(Mood.Idle);

        // Off in a random diagonal direction from where it stands.
        double angle = Random.Shared.Next(4) * Math.PI / 2 + Math.PI / 4 + (Random.Shared.NextDouble() - 0.5) * 0.4;
        velocity = new Vector(Math.Cos(angle), Math.Sin(angle)) * Speed;
        colorIndex = 0;
        clock.Restart();
        frame.Start();
        DiagnosticLog.Info("Screensaver started");
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        frame.Stop();
        character.SetColorOverride(null);
        character.Left = home.X;
        character.Top = home.Y;
        DiagnosticLog.Info("Screensaver stopped");
    }

    void Step()
    {
        // Any input since it started ends it.
        if (DateTime.Now - Desktop.IdleTime() > startedAt.AddMilliseconds(250))
        {
            Stop();
            return;
        }

        double seconds = clock.Elapsed.TotalSeconds;
        clock.Restart();

        // Once Windows has turned the screen off, there's nothing to draw: keep only the input check.
        if (screenOff is { } off && Desktop.IdleTime() > off + TimeSpan.FromSeconds(5)) return;

        double right = SystemParameters.PrimaryScreenWidth - character.Width;
        double bottom = SystemParameters.PrimaryScreenHeight - character.Height;
        double x = character.Left + velocity.X * seconds;
        double y = character.Top + velocity.Y * seconds;

        bool bounced = false;
        if (x <= 0 || x >= right)
        {
            velocity.X = -velocity.X;
            x = Math.Clamp(x, 0, Math.Max(0, right));
            bounced = true;
        }
        if (y <= 0 || y >= bottom)
        {
            velocity.Y = -velocity.Y;
            y = Math.Clamp(y, 0, Math.Max(0, bottom));
            bounced = true;
        }
        character.Left = x;
        character.Top = y;

        if (bounced) NextColor();
    }

    // Like the DVD logo: a new color at every edge, taken from the character's own mood colors.
    void NextColor()
    {
        var colors = config.Appearance.MoodColors.Values.Distinct().ToList();
        if (colors.Count == 0) return;
        colorIndex = (colorIndex + 1) % colors.Count;
        character.SetColorOverride(colors[colorIndex]);
    }
}
