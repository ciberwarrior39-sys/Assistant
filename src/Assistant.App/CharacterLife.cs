using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using Assistant.Core.Activity;
using Assistant.Core.Diagnostics;
using Assistant.Core.Memory;
using Assistant.Core.Storage;

namespace Assistant.App;

/// <summary>
/// What makes the character feel alive, all small and silent (a mood and a short label, never the chat popping open):
/// petting and poking, little idle movements, walking (off by default), a finished download, the battery, coming back
/// to the PC, a break reminder, seasonal hats and a birthday greeting (only if the user asked it to remember the date).
/// Nothing in game mode, nothing logged or sent anywhere, and every part can be turned off in Settings > Character.
/// </summary>
public sealed class CharacterLife
{
    static readonly TimeSpan BreakResetsAfter = TimeSpan.FromMinutes(5);
    static readonly TimeSpan AwayLongEnough = TimeSpan.FromMinutes(20);
    static readonly string[] TempEndings = [".crdownload", ".part", ".partial", ".download", ".tmp", ".opdownload", ".!ut"];
    static readonly string[] Pokes = ["hey!", "hehe", "that tickles!", "boop!"];

    readonly CharacterWindow character;
    readonly CharacterMood mood;
    readonly AppConfig config;
    readonly string configFile;
    readonly Func<bool> gaming;
    readonly Func<IEnumerable<string>> aboutMe;
    readonly Action<string> say;

    readonly DispatcherTimer tricks = new();
    readonly DispatcherTimer wander = new();
    readonly DispatcherTimer daily = new() { Interval = TimeSpan.FromMinutes(10) };
    readonly DispatcherTimer battery = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly FileSystemWatcher? downloads;

    TimeSpan active;          // time at the PC since the last real pause
    TimeSpan lastIdle;
    DateTime lastDownload = DateTime.MinValue;
    bool? wasPluggedIn;
    bool lowBatteryShown;

    Appearance Look => config.Appearance;

    /// <summary>Quiet: during a game, or while the character is hidden.</summary>
    bool Quiet => gaming() || !character.IsVisible;

    /// <summary>A reaction's mood only replaces a resting one, never "thinking", a raised hand or a timer.</summary>
    bool Resting => mood.Current is Mood.Idle or Mood.Sleepy;

    public CharacterLife(CharacterWindow character, CharacterMood mood, AppConfig config, string configFile,
                         Func<bool> gaming, Func<IEnumerable<string>> aboutMe, Action<string> say)
    {
        this.character = character;
        this.mood = mood;
        this.config = config;
        this.configFile = configFile;
        this.gaming = gaming;
        this.aboutMe = aboutMe;
        this.say = say;

        character.Petted += OnPetted;
        character.Poked += OnPoked;

        tricks.Tick += (_, _) => IdleTrick();
        Again(tricks, 25, 70);
        wander.Tick += (_, _) => Wander();
        Again(wander, 120, 300);
        battery.Tick += (_, _) => CheckBattery();
        battery.Start();
        daily.Tick += (_, _) => CheckDay();
        daily.Start();
        downloads = WatchDownloads();
        CheckDay();
    }

    /// <summary>Settings changed: the hat follows at once.</summary>
    public void SettingsChanged() => CheckDay();

    // ---- Petting and poking --------------------------------------------------------------------------------------

    void OnPetted()
    {
        if (gaming()) return;
        mood.Touch();
        if (Resting) mood.Set(Mood.Happy, TimeSpan.FromSeconds(3));
        character.Hearts();
    }

    void OnPoked()
    {
        if (gaming()) return;
        mood.Touch();
        character.Play(CharacterWindow.Trick.Hop);
        character.ShowNote(Pokes[Random.Shared.Next(Pokes.Length)], TimeSpan.FromSeconds(2));
    }

    // ---- Idle life -----------------------------------------------------------------------------------------------

    void IdleTrick()
    {
        Again(tricks, 25, 70);
        if (!Look.IdleTricks || Quiet || character.StillWhenIdle || mood.Current != Mood.Idle) return;

        // Late at night it yawns now and then.
        if (DateTime.Now.Hour < 5 && Random.Shared.Next(3) == 0)
        {
            character.Play(CharacterWindow.Trick.Yawn);
            return;
        }
        int roll = Random.Shared.Next(100);
        character.Play(roll switch
        {
            < 40 => CharacterWindow.Trick.Blink,
            < 65 => CharacterWindow.Trick.LookAround,
            < 80 => CharacterWindow.Trick.Stretch,
            _ => CharacterWindow.Trick.Hop,
        });
    }

    void Wander()
    {
        Again(wander, 120, 300);
        // Only while the user is at the PC: when they're away the screensaver may be moving it.
        if (!Look.Wander || Quiet || character.StillWhenIdle || Look.CalmAnimations || mood.Current != Mood.Idle
            || lastIdle > TimeSpan.FromMinutes(1)) return;
        character.Walk();
    }

    static void Again(DispatcherTimer timer, int fromSeconds, int toSeconds)
    {
        timer.Stop();
        timer.Interval = TimeSpan.FromSeconds(Random.Shared.Next(fromSeconds, toSeconds));
        timer.Start();
    }

    // ---- Coming back, and breaks (from the once-a-second look at the foreground window) ---------------------------

    public void Observe(ForegroundSnapshot snapshot)
    {
        // Back after 20 minutes or more away: a little wave.
        if (lastIdle >= AwayLongEnough && snapshot.Idle < TimeSpan.FromSeconds(3)) WelcomeBack();
        lastIdle = snapshot.Idle;

        // Time at the PC: only while it's being used. A 5-minute pause, or a game, starts it over.
        if (gaming() || snapshot.Idle >= BreakResetsAfter) active = TimeSpan.Zero;
        else if (snapshot.Idle < TimeSpan.FromMinutes(1)) active += TimeSpan.FromSeconds(1);

        int minutes = config.BreakReminderMinutes;
        if (minutes > 0 && active >= TimeSpan.FromMinutes(minutes))
        {
            active = TimeSpan.Zero; // the next one after another stretch of the same length
            if (Quiet) return;
            character.Play(CharacterWindow.Trick.Stretch);
            character.ShowNote(minutes >= 60 && minutes % 60 == 0
                ? $"time for a break? ({minutes / 60} h)"
                : $"time for a break? ({minutes} min)", TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>After a game or a long time away.</summary>
    public void WelcomeBack()
    {
        if (!Look.EventReactions || Quiet || !Resting) return;
        mood.Touch();
        mood.Set(Mood.Happy, TimeSpan.FromSeconds(3));
        character.Play(CharacterWindow.Trick.Hop);
        character.ShowNote("welcome back!", TimeSpan.FromSeconds(4));
    }

    // ---- Battery -------------------------------------------------------------------------------------------------

    void CheckBattery()
    {
        (bool hasBattery, bool pluggedIn, int percent) = Power.Battery();
        if (!hasBattery) return; // a desktop PC
        bool react = Look.EventReactions && !Quiet && Resting;

        if (wasPluggedIn == false && pluggedIn)
        {
            lowBatteryShown = false;
            if (react)
            {
                mood.Set(Mood.Happy, TimeSpan.FromSeconds(3));
                character.ShowNote("charging!", TimeSpan.FromSeconds(3));
            }
        }
        else if (!pluggedIn && percent <= 20 && !lowBatteryShown && react)
        {
            lowBatteryShown = true; // once until it's plugged in; if it was busy, tried again next minute
            mood.Set(Mood.Tired, TimeSpan.FromSeconds(8));
            character.ShowNote($"battery {percent}%", TimeSpan.FromSeconds(10));
        }
        if (pluggedIn) lowBatteryShown = false;
        wasPluggedIn = pluggedIn;
    }

    // ---- Downloads -----------------------------------------------------------------------------------------------

    // Only new file names in Downloads are seen; nothing is opened or read.
    FileSystemWatcher? WatchDownloads()
    {
        string? folder = WindowsActions.DownloadsFolder;
        if (folder is null || !Directory.Exists(folder)) return null;
        try
        {
            var watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
            // Browsers download to a temporary name and rename it when done; some create the final name first and
            // fill it later, so a new file is checked again after a moment.
            watcher.Renamed += (_, e) => character.Dispatcher.InvokeAsync(() => Downloaded(e.FullPath, waitFirst: false));
            watcher.Created += (_, e) => character.Dispatcher.InvokeAsync(() => Downloaded(e.FullPath, waitFirst: true));
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Couldn't watch the Downloads folder", ex);
            return null;
        }
    }

    async void Downloaded(string path, bool waitFirst)
    {
        if (TempEndings.Any(ending => path.EndsWith(ending, StringComparison.OrdinalIgnoreCase))) return;
        if (Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal)) return;
        if (waitFirst) await Task.Delay(TimeSpan.FromSeconds(2));
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
            if (TempEndings.Any(ending => File.Exists(path + ending))) return; // still downloading
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (!Look.EventReactions || Quiet || !Resting || DateTime.UtcNow - lastDownload < TimeSpan.FromSeconds(10)) return;
        lastDownload = DateTime.UtcNow;
        string name = Path.GetFileName(path);
        if (name.Length > 24) name = name[..21] + "…";
        mood.Set(Mood.FoundIt, TimeSpan.FromSeconds(4));
        character.ShowNote($"download done: {name}", TimeSpan.FromSeconds(10), () => Shell.RevealFile(path));
    }

    // ---- Hats and the birthday -----------------------------------------------------------------------------------

    void CheckDay()
    {
        DateTime today = DateTime.Today;
        bool birthday = IsBirthday(today);
        character.ShowHat(Look.SeasonalLooks ? CharacterHats.For(today, birthday) : HatKind.None);

        string day = today.ToString("yyyy-MM-dd");
        if (!birthday || config.LastBirthdayGreeting == day || Quiet) return; // after the game, if one is running
        config.LastBirthdayGreeting = day;
        try
        {
            config.Save(configFile);
        }
        catch (IOException ex)
        {
            DiagnosticLog.Error("Could not save config.json", ex);
        }
        say("Happy birthday! 🎂 I hope it's a great day.");
        mood.Set(Mood.Happy, TimeSpan.FromSeconds(6));
        character.Hearts();
    }

    // Only from a fact the user asked to remember, in about-me.md.
    bool IsBirthday(DateTime today)
    {
        if (Birthday.Find(aboutMe()) is not { } date) return false;
        // Born on 29 February: celebrated on the 28th in other years.
        int day = date.Month == 2 && date.Day == 29 && !DateTime.IsLeapYear(today.Year) ? 28 : date.Day;
        return today.Month == date.Month && today.Day == day;
    }
}
