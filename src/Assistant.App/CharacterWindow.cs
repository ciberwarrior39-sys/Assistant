using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Assistant.Core.Storage;

namespace Assistant.App;

/// <summary>
/// The placeholder character: a transparent, always-on-top window whose look follows the mood.
/// Click it to open the chat bubble, drag it anywhere, right-click for the menu.
/// This is the default character; its colors, size, labels and calmness come from the appearance settings.
/// Character packs with their own art (build step 11) replace only the visuals built in this class.
/// </summary>
public sealed class CharacterWindow : Window
{
    const int FramesPerSecond = 30;  // prototype: 0.3% CPU at 30 fps vs 0.9% at 60
    const double SnapDistance = 24;
    const double BaseWidth = 170, BaseHeight = 160;
    static readonly TimeSpan ClickAfterAutoHide = TimeSpan.FromMilliseconds(400);

    readonly CharacterMood mood;
    readonly ChatBubbleWindow bubble;
    readonly string placementFile;
    readonly SolidColorBrush bodyBrush = new(Color.FromRgb(0x4A, 0x90, 0xE2));
    readonly TranslateTransform bob = new();
    readonly RotateTransform tilt = new();
    readonly ScaleTransform eyelids = new();
    readonly TextBlock badgeText = new() { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold };
    readonly Border badge;
    readonly DispatcherTimer unhide = new();
    readonly ScaleTransform size = new(1, 1);
    Appearance appearance = new();
    string? colorOverride; // the screensaver's color of the moment
    bool inGameMode;

    /// <summary>The menu's "Settings…" was chosen.</summary>
    public event Action? SettingsRequested;

    public CharacterWindow(CharacterMood mood, ChatBubbleWindow bubble, string placementFile, string dataRoot, Action quit)
    {
        this.mood = mood;
        this.bubble = bubble;
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

        // Placeholder figure: a round body with two eyes.
        var body = new Ellipse { Width = 96, Height = 96, Fill = bodyBrush, Stroke = Brushes.White, StrokeThickness = 3 };
        var eyes = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
            RenderTransform = eyelids,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        eyes.Children.Add(Eye());
        eyes.Children.Add(Eye());

        var motion = new TransformGroup();
        motion.Children.Add(tilt);
        motion.Children.Add(bob);
        var figure = new Grid { Width = 96, Height = 96, RenderTransform = motion, RenderTransformOrigin = new Point(0.5, 0.5) };
        figure.Children.Add(body);
        figure.Children.Add(eyes);

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
        layout.Children.Add(figure);
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
        Loaded += (_, _) => ApplyMood(mood.Current);
    }

    /// <summary>Applies colors, size, labels and calmness right away (the Settings window's live preview).</summary>
    public void ApplyAppearance(Appearance settings)
    {
        appearance = settings;

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

    static Grid Eye()
    {
        var eye = new Grid { Width = 18, Height = 18, Margin = new Thickness(5, 0, 5, 0) };
        eye.Children.Add(new Ellipse { Fill = Brushes.White });
        eye.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)) });
        return eye;
    }

    // Placeholder look per mood: body color, badge text, bounce height and speed, wobble, how open the eyes are.
    // The colors here are only fallbacks; the appearance settings hold the real ones.
    static (Color Color, string Label, double BobHeight, double BobSeconds, double Tilt, double EyesOpen) DefaultLook(Mood mood) => mood switch
    {
        Mood.Thinking => (Color.FromRgb(0x8E, 0x6C, 0xEF), "thinking…", 8.0, 0.5, 0.0, 1.0),
        Mood.FoundIt => (Color.FromRgb(0x2E, 0xB8, 0x72), "found it!", 22.0, 0.35, 0.0, 1.0),
        Mood.Confused => (Color.FromRgb(0xF0, 0x9A, 0x3E), "hmm?", 4.0, 1.0, 10.0, 1.0),
        Mood.Happy => (Color.FromRgb(0x7C, 0xC5, 0x42), "yay!", 16.0, 0.35, 0.0, 1.0),
        Mood.Tired => (Color.FromRgb(0x8A, 0x94, 0xA3), "tired: cloud unavailable", 3.0, 2.0, 0.0, 0.5),
        Mood.Sleepy => (Color.FromRgb(0x3F, 0x5A, 0x9E), "zzz", 3.0, 3.0, 0.0, 0.15),
        Mood.Review => (Color.FromRgb(0xE8, 0xB3, 0x3A), "! weekly review", 5.0, 1.6, 0.0, 1.0),
        Mood.HandRaised => (Color.FromRgb(0xF5, 0xA6, 0x23), "!", 12.0, 0.6, 0.0, 1.0),
        Mood.Listening => (Color.FromRgb(0xE2, 0x4A, 0x6A), "listening…", 2.0, 0.4, 0.0, 1.0),
        Mood.Speaking => (Color.FromRgb(0x3E, 0xA8, 0xD8), "speaking", 10.0, 0.3, 0.0, 1.0),
        Mood.TimeUp => (Color.FromRgb(0xF0, 0x5A, 0x28), "time's up!", 18.0, 0.3, 6.0, 1.0),
        _ => (Color.FromRgb(0x4A, 0x90, 0xE2), "", 6.0, 1.2, 0.0, 1.0),
    };

    // The default look with the user's color for this mood, and calmer movement if they asked for it.
    (Color Color, string Label, double BobHeight, double BobSeconds, double Tilt, double EyesOpen) Look(Mood current)
    {
        var look = DefaultLook(current);
        string? hex = colorOverride;
        if ((hex is not null || appearance.MoodColors.TryGetValue(current.ToString(), out hex))
            && Appearance.IsColor(hex)
            && ColorConverter.ConvertFromString(hex) is Color custom)
        {
            look.Color = custom;
        }
        if (appearance.CalmAnimations)
        {
            look.BobHeight *= 0.35;
            look.BobSeconds *= 1.5;
            look.Tilt = 0;
        }
        // Without labels, the ones that ask for attention stay: the hand-raise, the weekly review, a timer.
        if (!appearance.ShowLabels && current is not (Mood.HandRaised or Mood.Review or Mood.TimeUp)) look.Label = "";
        return look;
    }

    void ApplyMood(Mood current)
    {
        if (!IsVisible) return; // hidden: no animations, nothing to draw

        var look = Look(current);

        var colorChange = new ColorAnimation(look.Color, TimeSpan.FromMilliseconds(300));
        Timeline.SetDesiredFrameRate(colorChange, FramesPerSecond);
        bodyBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorChange);

        badgeText.Text = look.Label;
        badge.Visibility = look.Label.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        eyelids.ScaleY = look.EyesOpen;

        Loop(bob, TranslateTransform.YProperty, 0, -look.BobHeight, look.BobSeconds);
        if (look.Tilt > 0)
        {
            Loop(tilt, RotateTransform.AngleProperty, -look.Tilt, look.Tilt, 0.8);
        }
        else
        {
            tilt.BeginAnimation(RotateTransform.AngleProperty, null);
            tilt.Angle = 0;
        }
    }

    void StopAnimations()
    {
        bob.BeginAnimation(TranslateTransform.YProperty, null);
        tilt.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    static void Loop(Animatable target, DependencyProperty property, double from, double to, double seconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds / 2))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(animation, FramesPerSecond);
        target.BeginAnimation(property, animation);
    }
}
