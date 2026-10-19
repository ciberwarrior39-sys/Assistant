using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Assistant.Core.Characters;
using Assistant.Core.Storage;

namespace Assistant.App;

/// <summary>
/// The character: a transparent, always-on-top window whose look follows the mood.
/// Click it to open the chat bubble, drag it anywhere, right-click for the menu.
/// The figure itself is a CharacterLook (blob, robot, cat, or the user's own animations); this window owns
/// the label, the size, the bounce and the wobble, from the appearance settings.
/// </summary>
public sealed class CharacterWindow : Window
{
    const double SnapDistance = 24;
    const double BaseWidth = 170, BaseHeight = 160;
    static readonly TimeSpan ClickAfterAutoHide = TimeSpan.FromMilliseconds(400);

    // Eyes follow the cursor when it comes within this distance (before the Size setting).
    const double GazeDistance = 260;
    TimeSpan gazeFast = TimeSpan.FromMilliseconds(33);                  // cursor near: smooth (one step per frame)
    static readonly TimeSpan GazeSlow = TimeSpan.FromMilliseconds(250); // cursor far: just checking

    readonly CharacterMood mood;
    readonly ChatBubbleWindow bubble;
    readonly string placementFile;
    readonly CharacterPacks packs;
    readonly Grid stage; // holds the look's figure and carries the bounce and wobble
    readonly TranslateTransform bob = new();
    readonly RotateTransform tilt = new();
    readonly TextBlock badgeText = new() { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold };
    readonly Border badge;
    readonly DispatcherTimer unhide = new();
    readonly ScaleTransform size = new(1, 1);
    readonly DispatcherTimer gazeTimer = new() { Interval = GazeSlow };
    Vector gaze; // where the eyes look now, eased toward the cursor
    bool cursorNear;
    bool stillWhenIdle; // Lite: no movement while nothing is happening
    DateTime cursorLastNear;
    static readonly TimeSpan CursorLeaveDelay = TimeSpan.FromSeconds(0.6); // no flicker at the edge of the range
    Appearance appearance = new();
    CharacterLook look = new BlobLook();
    string lookName = "blob";
    string? colorOverride; // the screensaver's color of the moment
    bool inGameMode;

    /// <summary>The menu's "Settings…" was chosen.</summary>
    public event Action? SettingsRequested;

    public CharacterWindow(CharacterMood mood, ChatBubbleWindow bubble, CharacterPacks packs, string placementFile, string dataRoot, Action quit)
    {
        this.mood = mood;
        this.bubble = bubble;
        this.packs = packs;
        this.placementFile = placementFile;

        Title = "Assistant";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;  // fully transparent pixels are click-through
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = BaseWidth;
        Height = BaseHeight;

        Point? saved = CharacterPlacement.Load(placementFile);
        Left = saved?.X ?? SystemParameters.WorkArea.Right - Width - 16;
        Top = saved?.Y ?? SystemParameters.WorkArea.Bottom - Height - 8;

        var motion = new TransformGroup();
        motion.Children.Add(tilt);
        motion.Children.Add(bob);
        stage = new Grid { RenderTransform = motion, RenderTransformOrigin = new Point(0.5, 0.5), HorizontalAlignment = HorizontalAlignment.Center };
        stage.Children.Add(look.Figure);

        badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = badgeText,
            Visibility = Visibility.Hidden,
        };

        var layout = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, LayoutTransform = size };
        layout.Children.Add(stage);
        layout.Children.Add(badge);
        Content = layout;

        MouseLeftButtonDown += OnLeftButtonDown;
        ContextMenu = BuildMenu(dataRoot, quit);
        mood.Changed += ApplyMood;
        unhide.Tick += (_, _) =>
        {
            unhide.Stop();
            if (!inGameMode) ShowAgain(); // during a game, it comes back when the game ends
        };
        gazeTimer.Tick += (_, _) => FollowCursor();
        Loaded += (_, _) =>
        {
            ApplyMood(mood.Current);
            gazeTimer.Start();
        };
    }

    /// <summary>Applies the character, colors, size, labels and calmness right away (the Settings window's live preview).</summary>
    public void ApplyAppearance(Appearance settings)
    {
        appearance = settings;
        SetLook(settings);

        // Once on screen, it grows or shrinks around its bottom-right corner, so it stays where it was docked.
        // (At startup the saved spot already belongs to the saved size.)
        double width = BaseWidth * settings.Scale, height = BaseHeight * settings.Scale;
        bool resized = IsLoaded && (Math.Abs(Width - width) > 0.5 || Math.Abs(Height - height) > 0.5);
        if (resized)
        {
            Left += Width - width;
            Top += Height - height;
        }
        Width = width;
        Height = height;
        Left = Math.Min(Left, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - width);
        Top = Math.Min(Top, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height);
        size.ScaleX = size.ScaleY = settings.Scale;
        if (resized) CharacterPlacement.Save(placementFile, Left, Top);

        ApplyMood(mood.Current);
    }

    /// <summary>The performance profile's share: frame rate for the eyes, and stillness when idle (Lite).</summary>
    public void ApplyPerformance(Assistant.Core.Storage.PerformanceProfile profile)
    {
        stillWhenIdle = profile.StillWhenIdle;
        gazeFast = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, profile.AnimationFps));
        ApplyMood(mood.Current);
    }

    /// <summary>A color that wins over the mood colors until cleared with null (used by the screensaver).</summary>
    public void SetColorOverride(string? hex)
    {
        colorOverride = hex;
        ApplyMood(mood.Current);
    }

    public void ToggleChat()
    {
        mood.Touch();
        if (bubble.IsVisible)
        {
            bubble.Hide();
            return;
        }
        // Clicking the character takes focus from an open bubble, which hides itself a moment
        // before this click arrives; that click meant "close", so don't reopen.
        if (DateTime.UtcNow - bubble.LastAutoHide < ClickAfterAutoHide) return;
        OpenChat();
    }

    public Rect Bounds => new(Left, Top, Width, Height);

    public void OpenChat()
    {
        mood.Touch();
        bubble.ShowNear(new Rect(Left, Top, Width, Height));
    }

    public void HideFor(TimeSpan duration)
    {
        bubble.Hide();
        Hide();
        StopAnimations();
        unhide.Interval = duration;
        unhide.Start();
    }

    /// <summary>Game mode: get out of the way while a game runs, and come back afterwards.</summary>
    public void SetGameMode(bool on)
    {
        inGameMode = on;
        if (on)
        {
            bubble.Hide();
            Hide();
            StopAnimations();
        }
        else if (!unhide.IsEnabled)
        {
            ShowAgain(); // unless the user hid it for an hour, which still applies
        }
    }

    public void ShowAgain()
    {
        unhide.Stop();
        if (!IsVisible) Show();
        ApplyMood(mood.Current);
    }

    void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        double startLeft = Left, startTop = Top;
        try
        {
            DragMove(); // returns when the button is released
        }
        catch (InvalidOperationException)
        {
            return; // the button was already up (for example, touch input)
        }

        bool moved = Math.Abs(Left - startLeft) > 3 || Math.Abs(Top - startTop) > 3;
        if (!moved)
        {
            ToggleChat();
            return;
        }

        SnapToEdges();
        CharacterPlacement.Save(placementFile, Left, Top);
    }

    // Docks to the edges of the main screen's work area when dropped close to one.
    void SnapToEdges()
    {
        Rect area = SystemParameters.WorkArea;
        if (Math.Abs(Left - area.Left) < SnapDistance) Left = area.Left;
        if (Math.Abs(area.Right - (Left + Width)) < SnapDistance) Left = area.Right - Width;
        if (Math.Abs(Top - area.Top) < SnapDistance) Top = area.Top;
        if (Math.Abs(area.Bottom - (Top + Height)) < SnapDistance) Top = area.Bottom - Height;
    }

    ContextMenu BuildMenu(string dataRoot, Action quit)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Chat", ToggleChat));
        menu.Items.Add(MenuEntry("Settings…", () => SettingsRequested?.Invoke()));
        menu.Items.Add(MenuEntry("Open data folder", () => Shell.OpenFolder(dataRoot)));
        menu.Items.Add(MenuEntry("Hide for 1 hour", () => HideFor(TimeSpan.FromHours(1))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Quit", quit));
        return menu;
    }

    static MenuItem MenuEntry(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // The eyes turn toward the cursor when it comes close, and drift back when it leaves.
    // Only the cursor position is read (a cheap call), a few times a second; no mouse hook.
    void FollowCursor()
    {
        if (!IsVisible)
        {
            gazeTimer.Interval = GazeSlow;
            return;
        }

        Vector target = default;
        bool near = false;
        if (GetCursorPos(out CursorPoint cursor))
        {
            try
            {
                Point pointer = PointFromScreen(new Point(cursor.X, cursor.Y));
                Point eyes = look.Figure.TranslatePoint(look.EyeCenter, this);
                Vector toCursor = pointer - eyes;
                double distance = toCursor.Length;
                near = distance < GazeDistance * appearance.Scale;
                // Close to the face the eyes turn part of the way; further out they look straight at it.
                if (near && mood.Current != Mood.Sleepy) target = toCursor / Math.Max(distance, 40 * appearance.Scale);
            }
            catch (InvalidOperationException)
            {
                // Not on screen yet.
            }
        }
        NoticeCursor(near);

        if (!look.HasEyes)
        {
            gazeTimer.Interval = GazeSlow; // only "cursor nearby" to watch for
            return;
        }

        Vector next = gaze + (target - gaze) * 0.3;
        if ((target - next).Length < 0.02) next = target;
        if (next != gaze)
        {
            gaze = next;
            look.LookAt(gaze);
        }
        // Fast only while the eyes are on the cursor or still moving.
        gazeTimer.Interval = target != default || gaze != default ? gazeFast : GazeSlow;
    }

    // Tells the look when the cursor arrives (right away) and leaves (after a moment).
    void NoticeCursor(bool near)
    {
        if (near)
        {
            cursorLastNear = DateTime.UtcNow;
            if (cursorNear) return;
            cursorNear = true;
            look.CursorNear(true);
        }
        else if (cursorNear && DateTime.UtcNow - cursorLastNear > CursorLeaveDelay)
        {
            cursorNear = false;
            look.CursorNear(false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetCursorPos(out CursorPoint point);

    // Swaps the figure when another character is chosen. A custom character whose folder is gone shows the blob.
    void SetLook(Appearance settings)
    {
        string wanted = settings.CustomCharacter is { } name && !packs.Exists(name) ? "blob" : settings.Character;
        if (wanted == lookName) return;

        look.Stop();
        look = wanted switch
        {
            "robot" => new RobotLook(),
            "cat" => new CatLook(),
            _ when wanted.StartsWith(Appearance.CustomPrefix, StringComparison.Ordinal) => new CustomLook(packs, wanted[Appearance.CustomPrefix.Length..]),
            _ => new BlobLook(),
        };
        lookName = wanted;
        gaze = default;
        cursorNear = false;
        stage.Children.Clear();
        stage.Children.Add(look.Figure);
    }

    // Each mood: body color, label, bounce height and speed, wobble, how open the eyes are.
    // The colors here are only fallbacks; the appearance settings hold the real ones.
    static MoodLook DefaultLook(Mood mood) => mood switch
    {
        Mood.Thinking => new(Color.FromRgb(0x8E, 0x6C, 0xEF), "thinking…", 8.0, 0.5, 0.0, 1.0),
        Mood.FoundIt => new(Color.FromRgb(0x2E, 0xB8, 0x72), "found it!", 22.0, 0.35, 0.0, 1.0),
        Mood.Confused => new(Color.FromRgb(0xF0, 0x9A, 0x3E), "hmm?", 4.0, 1.0, 10.0, 1.0),
        Mood.Happy => new(Color.FromRgb(0x7C, 0xC5, 0x42), "yay!", 16.0, 0.35, 0.0, 1.0),
        Mood.Tired => new(Color.FromRgb(0x8A, 0x94, 0xA3), "tired: cloud unavailable", 3.0, 2.0, 0.0, 0.5),
        Mood.Sleepy => new(Color.FromRgb(0x3F, 0x5A, 0x9E), "zzz", 3.0, 3.0, 0.0, 0.15),
        Mood.Review => new(Color.FromRgb(0xE8, 0xB3, 0x3A), "! weekly review", 5.0, 1.6, 0.0, 1.0),
        Mood.HandRaised => new(Color.FromRgb(0xF5, 0xA6, 0x23), "!", 12.0, 0.6, 0.0, 1.0),
        Mood.Listening => new(Color.FromRgb(0xE2, 0x4A, 0x6A), "listening…", 2.0, 0.4, 0.0, 1.0),
        Mood.Speaking => new(Color.FromRgb(0x3E, 0xA8, 0xD8), "speaking", 10.0, 0.3, 0.0, 1.0),
        Mood.TimeUp => new(Color.FromRgb(0xF0, 0x5A, 0x28), "time's up!", 18.0, 0.3, 6.0, 1.0),
        _ => new(Color.FromRgb(0x4A, 0x90, 0xE2), "", 6.0, 1.2, 0.0, 1.0),
    };

    // The mood's look with the user's color for it, and calmer movement if they asked for it.
    MoodLook Look(Mood current)
    {
        MoodLook result = DefaultLook(current);
        string? hex = colorOverride;
        if ((hex is not null || appearance.MoodColors.TryGetValue(current.ToString(), out hex))
            && Appearance.IsColor(hex)
            && ColorConverter.ConvertFromString(hex) is Color custom)
        {
            result = result with { Color = custom };
        }
        if (appearance.CalmAnimations)
            result = result with { BobHeight = result.BobHeight * 0.35, BobSeconds = result.BobSeconds * 1.5, Tilt = 0 };

        // Without labels, the ones that ask for attention stay: the hand-raise, the weekly review, a timer.
        if (!appearance.ShowLabels && current is not (Mood.HandRaised or Mood.Review or Mood.TimeUp))
            result = result with { Label = "" };
        return result;
    }

    void ApplyMood(Mood current)
    {
        if (!IsVisible) return; // hidden: no animations, nothing to draw

        MoodLook shown = Look(current);
        look.Apply(current, shown, appearance.CalmAnimations, colorOverride is not null);

        badgeText.Text = shown.Label;
        badge.Visibility = shown.Label.Length > 0 ? Visibility.Visible : Visibility.Hidden;

        // Custom animations move by themselves; bouncing them too would look wrong.
        // Lite keeps the drawn characters still while idle or asleep, so they cost nothing then.
        bool still = stillWhenIdle && look.UsesMotion && (current is Mood.Idle or Mood.Sleepy);
        if (still) look.Stop();
        if (look.UsesMotion && !still) Motion.Loop(bob, TranslateTransform.YProperty, 0, -shown.BobHeight, shown.BobSeconds);
        else Motion.Still(bob, TranslateTransform.YProperty, 0);

        if (look.UsesMotion && shown.Tilt > 0) Motion.Loop(tilt, RotateTransform.AngleProperty, -shown.Tilt, shown.Tilt, 0.8);
        else Motion.Still(tilt, RotateTransform.AngleProperty, 0);
    }

    void StopAnimations()
    {
        bob.BeginAnimation(TranslateTransform.YProperty, null);
        tilt.BeginAnimation(RotateTransform.AngleProperty, null);
        look.Stop();
    }
}
