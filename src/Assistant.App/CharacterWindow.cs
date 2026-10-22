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

    // Idle life: a hop and a stretch on top of the mood's bounce; hearts and hats drawn over the figure.
    readonly TranslateTransform hop = new();
    readonly ScaleTransform stretch = new(1, 1);
    readonly Canvas effects = new()
    {
        Width = 96, Height = 96, IsHitTestVisible = false, ClipToBounds = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
    };
    readonly DispatcherTimer noteTimer = new();
    readonly DispatcherTimer lookAround = new() { Interval = TimeSpan.FromMilliseconds(900) };
    int lookStep;
    FrameworkElement? hatShown;
    HatKind hat = HatKind.None;
    string? note;      // a short message on the label for a while ("download done: …"), over the mood's label
    Action? noteClick; // what clicking the label does then

    // Petting: the cursor going back and forth over the character, 5 turns within 1.5 seconds.
    double lastCursorX = double.NaN;
    int lastDirection;
    readonly Queue<DateTime> turns = new();
    DateTime lastPet = DateTime.MinValue;
    readonly DispatcherTimer gazeTimer = new() { Interval = GazeSlow };
    Vector gaze; // where the eyes look now, eased toward the cursor
    bool cursorNear;
    bool stillWhenIdle; // Lite: no movement while nothing is happening

    /// <summary>Lite: no movement while nothing is happening (so no idle tricks or walks either).</summary>
    public bool StillWhenIdle => stillWhenIdle;
    DateTime cursorLastNear;
    static readonly TimeSpan CursorLeaveDelay = TimeSpan.FromSeconds(0.6); // no flicker at the edge of the range
    Appearance appearance = new();
    CharacterLook look = new BlobLook();
    string lookName = "blob";
    string? colorOverride; // the screensaver's color of the moment
    bool inGameMode;

    /// <summary>The menu's "Settings…" was chosen.</summary>
    public event Action? SettingsRequested;

    /// <summary>The menu's "Help with selected text" was chosen.</summary>
    public event Action? TextHelpRequested;

    /// <summary>The mouse moved back and forth over the character.</summary>
    public event Action? Petted;

    /// <summary>The character was double-clicked.</summary>
    public event Action? Poked;

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
        motion.Children.Add(stretch);
        motion.Children.Add(tilt);
        motion.Children.Add(bob);
        motion.Children.Add(hop);
        stage = new Grid { RenderTransform = motion, RenderTransformOrigin = new Point(0.5, 0.5), HorizontalAlignment = HorizontalAlignment.Center };
        stage.Children.Add(look.Figure);
        stage.Children.Add(effects);

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
        // A note with something to show ("download done: …") opens it when its label is clicked.
        badge.MouseLeftButtonDown += (_, e) =>
        {
            if (noteClick is not { } click) return;
            e.Handled = true;
            click();
        };
        noteTimer.Tick += (_, _) => ClearNote();
        lookAround.Tick += (_, _) => LookAroundStep();
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
        StopWalking();
        mood.Touch();
        bubble.ShowNear(new Rect(Left, Top, Width, Height));
    }

    public void HideFor(TimeSpan duration)
    {
        StopWalking();
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
            StopWalking();
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
        StopWalking();
        // A double-click is a poke; the first click already opened (or closed) the chat as usual.
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            if (appearance.Reactions) Poked?.Invoke();
            return;
        }

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

    // ---- Life: notes, hearts, hats, little tricks and walking -----------------------------------------------------

    /// <summary>A short message on the label for a while, over the mood's own; with onClick, clicking the label runs it.</summary>
    public void ShowNote(string text, TimeSpan duration, Action? onClick = null)
    {
        note = text;
        noteClick = onClick;
        noteTimer.Stop();
        noteTimer.Interval = duration;
        noteTimer.Start();
        ApplyMood(mood.Current);
    }

    void ClearNote()
    {
        noteTimer.Stop();
        note = null;
        noteClick = null;
        ApplyMood(mood.Current);
    }

    /// <summary>A few hearts float up (petting).</summary>
    public void Hearts()
    {
        if (!IsVisible) return;
        SolidColorBrush pink = Motion.Frozen(Color.FromRgb(0xFF, 0x5C, 0x8A));
        for (int i = 0; i < 3; i++)
        {
            var heart = new TextBlock { Text = "♥", Foreground = pink, FontSize = 15 + i * 3, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(heart, 22 + i * 20);
            Canvas.SetTop(heart, 24);
            effects.Children.Add(heart);
            var start = TimeSpan.FromMilliseconds(i * 160);
            var rise = new DoubleAnimation(24, -28, TimeSpan.FromSeconds(1.2)) { BeginTime = start };
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1.2)) { BeginTime = start };
            Timeline.SetDesiredFrameRate(rise, Motion.FramesPerSecond);
            Timeline.SetDesiredFrameRate(fade, Motion.FramesPerSecond);
            fade.Completed += (_, _) => effects.Children.Remove(heart);
            heart.BeginAnimation(Canvas.TopProperty, rise);
            heart.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>A seasonal hat on the drawn characters (custom ones keep their own look). None takes it off.</summary>
    public void ShowHat(HatKind kind)
    {
        hat = kind;
        if (hatShown is not null) effects.Children.Remove(hatShown);
        hatShown = null;
        if (kind == HatKind.None || look.HatAnchor is not { } anchor) return;

        double scale = look.HatScale;
        FrameworkElement drawn = CharacterHats.Create(kind);
        drawn.RenderTransform = new ScaleTransform(scale, scale);
        Canvas.SetLeft(drawn, anchor.X - CharacterHats.Size / 2 * scale);
        Canvas.SetTop(drawn, anchor.Y - CharacterHats.Size * scale);
        effects.Children.Add(drawn);
        hatShown = drawn;
    }

    public enum Trick { Blink, LookAround, Stretch, Hop, Yawn }

    /// <summary>A little idle movement. Calm animations keep only the blink and looking around.</summary>
    public void Play(Trick trick)
    {
        if (!IsVisible) return;
        if (appearance.CalmAnimations && trick is Trick.Hop or Trick.Stretch or Trick.Yawn) trick = Trick.Blink;
        switch (trick)
        {
            case Trick.Blink:
                look.Blink();
                break;
            case Trick.LookAround:
                if (cursorNear || !look.HasEyes)
                {
                    look.Blink();
                    break;
                }
                lookStep = 0;
                LookAroundStep();
                lookAround.Start();
                break;
            case Trick.Hop:
                Once(hop, TranslateTransform.YProperty, -14, 0.16);
                break;
            case Trick.Stretch:
                Once(stretch, ScaleTransform.ScaleYProperty, 1.12, 0.35);
                Once(stretch, ScaleTransform.ScaleXProperty, 0.94, 0.35);
                break;
            case Trick.Yawn:
                Once(stretch, ScaleTransform.ScaleYProperty, 1.15, 0.9);
                Once(stretch, ScaleTransform.ScaleXProperty, 0.92, 0.9);
                ShowNote("*yawn*", TimeSpan.FromSeconds(2));
                break;
        }
    }

    // Left, right, back to the middle; the cursor coming close takes over at any point.
    void LookAroundStep()
    {
        if (cursorNear || lookStep > 2)
        {
            lookAround.Stop();
            if (!cursorNear) look.LookAt(default);
            return;
        }
        look.LookAt(lookStep switch { 0 => new Vector(-0.8, 0.1), 1 => new Vector(0.8, 0.1), _ => default });
        lookStep++;
    }

    static void Once(IAnimatable target, DependencyProperty property, double to, double seconds)
    {
        var there = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, FillBehavior = FillBehavior.Stop };
        there.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
        Timeline.SetDesiredFrameRate(there, Motion.FramesPerSecond);
        target.BeginAnimation(property, there);
    }

    /// <summary>Walks a little way along the screen with small hops, then stays there. Not while the chat is open.</summary>
    public void Walk()
    {
        if (!IsVisible || bubble.IsVisible) return;
        Rect area = SystemParameters.WorkArea;
        double distance = Random.Shared.Next(120, 320) * (Random.Shared.Next(2) == 0 ? -1 : 1);
        double target = Math.Clamp(Left + distance, area.Left, area.Right - Width);
        if (Math.Abs(target - Left) < 40) target = Math.Clamp(Left - distance, area.Left, area.Right - Width);
        if (Math.Abs(target - Left) < 40) return;

        var walk = new DoubleAnimation(Left, target, TimeSpan.FromSeconds(Math.Abs(target - Left) / 60));
        Timeline.SetDesiredFrameRate(walk, Motion.FramesPerSecond);
        walk.Completed += (_, _) =>
        {
            if (!walking) return;
            StopWalking();
            CharacterPlacement.Save(placementFile, Left, Top);
        };
        walking = true;
        Motion.Loop(hop, TranslateTransform.YProperty, 0, -6, 0.4);
        BeginAnimation(LeftProperty, walk);
    }

    bool walking;

    // Keeps it where it is now (a drag or the chat opening interrupts a walk).
    void StopWalking()
    {
        if (!walking) return;
        walking = false;
        double here = Left;
        BeginAnimation(LeftProperty, null);
        Left = here;
        Motion.Still(hop, TranslateTransform.YProperty, 0);
    }

    ContextMenu BuildMenu(string dataRoot, Action quit)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Chat", ToggleChat));
        menu.Items.Add(MenuEntry("Help with selected text", () => TextHelpRequested?.Invoke()));
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
        bool near = false, inside = false;
        if (GetCursorPos(out CursorPoint cursor))
        {
            try
            {
                Point pointer = PointFromScreen(new Point(cursor.X, cursor.Y));
                Point eyes = look.Figure.TranslatePoint(look.EyeCenter, this);
                Vector toCursor = pointer - eyes;
                double distance = toCursor.Length;
                near = distance < GazeDistance * appearance.Scale;
                inside = pointer.X >= 0 && pointer.Y >= 0 && pointer.X <= ActualWidth && pointer.Y <= ActualHeight;
                DetectPetting(pointer, inside);
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
            gazeTimer.Interval = inside ? gazeFast : GazeSlow; // "cursor nearby" to watch for, and petting
            return;
        }

        Vector next = gaze + (target - gaze) * 0.3;
        if ((target - next).Length < 0.02) next = target;
        if (next != gaze)
        {
            gaze = next;
            look.LookAt(gaze);
        }
        // Fast only while the eyes are on the cursor or still moving (or the cursor is on it: petting).
        gazeTimer.Interval = inside || target != default || gaze != default ? gazeFast : GazeSlow;
    }

    void DetectPetting(Point pointer, bool inside)
    {
        if (!inside || !appearance.Reactions)
        {
            lastCursorX = double.NaN;
            return;
        }
        if (double.IsNaN(lastCursorX))
        {
            lastCursorX = pointer.X;
            return;
        }
        double moved = pointer.X - lastCursorX;
        if (Math.Abs(moved) < 4) return;
        lastCursorX = pointer.X;
        int direction = Math.Sign(moved);
        if (direction == lastDirection) return;
        lastDirection = direction;

        DateTime now = DateTime.UtcNow;
        turns.Enqueue(now);
        while (turns.Count > 0 && now - turns.Peek() > TimeSpan.FromSeconds(1.5)) turns.Dequeue();
        if (turns.Count >= 5 && now - lastPet > TimeSpan.FromSeconds(4))
        {
            turns.Clear();
            lastPet = now;
            Petted?.Invoke();
        }
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
        stage.Children.Add(effects);
        ShowHat(hat);
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

        string label = note ?? shown.Label;
        badgeText.Text = label;
        badge.Visibility = label.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        badge.Cursor = noteClick is null ? null : Cursors.Hand;

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
