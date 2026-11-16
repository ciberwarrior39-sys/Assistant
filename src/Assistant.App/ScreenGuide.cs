using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Assistant.Core.Diagnostics;
using Assistant.Core.Guide;

namespace Assistant.App;

/// <summary>
/// "Show me where to click": reads the controls of the window the user was in through Windows' accessibility interface
/// (UI Automation, what screen readers use), and marks one with a ring or by sending the character over.
/// It only looks and draws. Nothing is clicked, typed or focused, and the ring lets every click through.
/// The same windows are refused as for screenshots: games, fullscreen windows and anything in exclusions.json.
/// </summary>
public sealed class ScreenGuide(WindowCapture windows, CharacterWindow character) : IScreenGuide
{
    const int MaxElements = 1500;
    static readonly TimeSpan MaxScan = TimeSpan.FromSeconds(2);
    static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(5);     // a window that doesn't answer at all
    static readonly TimeSpan RingTime = TimeSpan.FromSeconds(12);
    static readonly TimeSpan PointTime = TimeSpan.FromSeconds(10);

    GuideRing? ring;

    public string WindowApp => windows.GameMode ? "" : windows.TextTarget() is { Problem: null } target ? target.Name : "";

    public async Task<UiScan> ScanAsync(CancellationToken ct)
    {
        if (windows.GameMode) return new UiScan(null, "Game mode is on, so I don't look at other windows until the game is closed.");
        TextSource target = windows.TextTarget();
        if (target.Window == IntPtr.Zero)
            return new UiScan(null, "I don't know which window you mean. Click the window you need help with, then ask me again.");
        if (target.Problem is not null)
            return new UiScan(null, "I don't look at that window: it's a game or a fullscreen window, or it's in exclusions.json.");

        // UI Automation asks the other program, which can be slow or not answer at all: off the UI thread, with a limit.
        Task<UiScan> scan = Task.Run(() => Scan(target.Window, target.Name), ct);
        if (await Task.WhenAny(scan, Task.Delay(GiveUp, ct)) != scan)
        {
            DiagnosticLog.Info("Guide: the window didn't answer in time");
            return new UiScan(null, $"{target.Name} isn't answering right now, so I couldn't read its buttons. Try again in a moment.");
        }
        return await scan;
    }

    // Walks the window's controls level by level (so toolbars and menus come before deep content), until the count or
    // the time runs out. One request per control fetches everything needed, so nothing is asked twice.
    static UiScan Scan(IntPtr window, string app)
    {
        var elements = new List<UiElement>();
        bool complete = true;
        try
        {
            if (!GetWindowRect(window, out RECT frame)) return new UiScan(null, "I couldn't look at that window.");
            var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.IsEnabledProperty);
            request.Add(AutomationElement.IsOffscreenProperty);

            var timer = Stopwatch.StartNew();
            TreeWalker walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<AutomationElement>();
            using (request.Activate())
            {
                queue.Enqueue(AutomationElement.FromHandle(window));
            }
            int visited = 0;
            while (queue.Count > 0)
            {
                if (visited >= MaxElements || timer.Elapsed > MaxScan)
                {
                    complete = false;
                    break;
                }
                AutomationElement parent = queue.Dequeue();
                AutomationElement? child;
                try
                {
                    child = walker.GetFirstChild(parent, request);
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
                {
                    continue; // that part of the window changed meanwhile
                }
                while (child is not null && visited < MaxElements && timer.Elapsed <= MaxScan)
                {
                    visited++;
                    if (Read(child) is { } element) elements.Add(element);
                    // What's scrolled out of view has nothing under it worth showing.
                    if (!IsOffscreen(child)) queue.Enqueue(child);
                    try
                    {
                        child = walker.GetNextSibling(child, request);
                    }
                    catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
                    {
                        child = null;
                    }
                }
            }
            // Counts and timing only: never the names.
            DiagnosticLog.Info($"Guide: looked at {visited} controls in {timer.ElapsedMilliseconds} ms, {elements.Count} with a name on screen");
            return new UiScan(new UiWindow(app, frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top, elements, complete), null);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException
                                       or Win32Exception or UnauthorizedAccessException or TimeoutException)
        {
            DiagnosticLog.Error("Guide: reading the window failed", ex);
            return elements.Count > 0
                ? new UiScan(new UiWindow(app, 0, 0, 0, 0, elements, Complete: false), null)
                : new UiScan(null, $"I couldn't read the buttons in {app}. A program running as administrator doesn't let other programs look.");
        }
    }

    static bool IsOffscreen(AutomationElement element)
    {
        try
        {
            return element.Cached.IsOffscreen;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return true;
        }
    }

    static UiElement? Read(AutomationElement element)
    {
        try
        {
            AutomationElement.AutomationElementInformation info = element.Cached;
            string name = info.Name ?? "";
            Rect box = info.BoundingRectangle;
            if (name.Trim().Length == 0 || name.Length > 120 || info.IsOffscreen || box.IsEmpty || box.Width < 2 || box.Height < 2
                || double.IsInfinity(box.X) || double.IsInfinity(box.Y))
                return null;
            return new UiElement(name.Trim(), Kind(info.ControlType), (int)box.X, (int)box.Y, (int)box.Width, (int)box.Height, info.IsEnabled);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return null;
        }
    }

    // Windows' control types in the words GuideMatch and people use.
    static string Kind(ControlType type)
    {
        if (type == ControlType.Button) return "button";
        if (type == ControlType.MenuItem) return "menu item";
        if (type == ControlType.Menu || type == ControlType.MenuBar) return "menu";
        if (type == ControlType.TabItem) return "tab";
        if (type == ControlType.Hyperlink) return "link";
        if (type == ControlType.CheckBox) return "checkbox";
        if (type == ControlType.RadioButton) return "radio button";
        if (type == ControlType.ComboBox) return "combo box";
        if (type == ControlType.Edit) return "text field";
        if (type == ControlType.ListItem || type == ControlType.DataItem) return "list item";
        if (type == ControlType.TreeItem) return "tree item";
        if (type == ControlType.SplitButton) return "split button";
        if (type == ControlType.Slider) return "slider";
        if (type == ControlType.Spinner) return "spinner";
        if (type == ControlType.Text) return "text";
        if (type == ControlType.Image) return "image";
        if (type == ControlType.ToolBar) return "toolbar";
        if (type == ControlType.Document) return "document";
        if (type == ControlType.Group) return "group";
        if (type == ControlType.Pane || type == ControlType.Window) return "pane";
        return "other";
    }

    public void Show(UiElement element) => OnUi(() =>
    {
        ring ??= new GuideRing();
        ring.ShowAround(element, RingTime);
    });

    public void Point(UiElement element) => OnUi(() =>
    {
        ring ??= new GuideRing();
        ring.ShowAround(element, PointTime);
        // The control's place is in screen pixels; the character moves in WPF units.
        DpiScale dpi = VisualTreeHelper.GetDpi(character);
        character.PointAt(new Rect(element.X / dpi.DpiScaleX, element.Y / dpi.DpiScaleY, element.Width / dpi.DpiScaleX, element.Height / dpi.DpiScaleY),
                          PointTime);
    });

    static void OnUi(Action action)
    {
        Dispatcher dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
}

/// <summary>
/// The ring around a control: a small see-through window just bigger than the control, always on top, that never
/// takes the keyboard and lets every click through to what's under it. It fades out by itself.
/// </summary>
sealed class GuideRing : Window
{
    const int Padding = 8; // screen pixels around the control
    const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExLayered = 0x80000;
    const int GwlExStyle = -20;
    const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
    static readonly IntPtr HwndTopmost = new(-1);

    readonly Border mark;
    readonly DispatcherTimer hide = new();

    public GuideRing()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Orange inside a white edge: visible on light and dark windows alike.
        mark = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6A, 0x2B)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(2),
        };
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(9),
            Child = mark,
        };
        hide.Tick += (_, _) =>
        {
            hide.Stop();
            Hide();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        // Click-through, never active, not in Alt+Tab.
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered);
    }

    public void ShowAround(UiElement element, TimeSpan time)
    {
        IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
        // A first guess in WPF units, so it doesn't flash somewhere else; the exact place follows in screen pixels.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Left = (element.X - Padding) / dpi.DpiScaleX;
        Top = (element.Y - Padding) / dpi.DpiScaleY;
        Width = (element.Width + 2 * Padding) / dpi.DpiScaleX;
        Height = (element.Height + 2 * Padding) / dpi.DpiScaleY;
        if (!IsVisible) Show();
        SetWindowPos(handle, HwndTopmost, element.X - Padding, element.Y - Padding, element.Width + 2 * Padding, element.Height + 2 * Padding,
                     SwpNoActivate | SwpShowWindow);

        var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(pulse, Motion.FramesPerSecond);
        mark.BeginAnimation(OpacityProperty, pulse);

        hide.Stop();
        hide.Interval = time;
        hide.Start();
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
