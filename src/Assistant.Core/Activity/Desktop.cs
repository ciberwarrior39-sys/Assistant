using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Assistant.Core.Activity;

/// <summary>What is in front of the user right now, read once per second.</summary>
public sealed record ForegroundSnapshot(
    DateTime Time,
    int ProcessId,
    string App,
    string Title,
    TimeSpan Idle,
    bool FullscreenGame,
    bool PresentationMode,
    bool BorderlessFullscreen,
    IntPtr Window = default); // the window itself, so "look at this" can capture it

/// <summary>Reads the foreground window, the user's idle time and Windows' fullscreen state.</summary>
public static class Desktop
{
    const int MaxTitleLength = 200;
    static readonly Dictionary<int, string> ProcessNames = new();

    /// <summary>Call from one thread only (the app calls it from the UI timer).</summary>
    public static ForegroundSnapshot Capture()
    {
        DateTime now = DateTime.Now;
        TimeSpan idle = IdleTime();

        // Windows' own notion of "don't disturb": a full-screen Direct3D game, or presentation mode.
        int state = SHQueryUserNotificationState(out int quns) == 0 ? quns : 0;
        bool fullscreenGame = state == QUNS_RUNNING_D3D_FULL_SCREEN;
        bool presentation = state == QUNS_PRESENTATION_MODE;

        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return new ForegroundSnapshot(now, 0, "", "", idle, fullscreenGame, presentation, false);

        GetWindowThreadProcessId(window, out uint processId);
        return new ForegroundSnapshot(
            now,
            (int)processId,
            ProcessName((int)processId),
            WindowTitle(window),
            idle,
            fullscreenGame,
            presentation,
            IsBorderlessFullscreen(window),
            window);
    }

    /// <summary>Time since the last mouse or keyboard input anywhere.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime); // handles the 49-day wraparound
        return TimeSpan.FromMilliseconds(idleMs);
    }

    static string WindowTitle(IntPtr window)
    {
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        string title = text.ToString().Trim();
        return title.Length > MaxTitleLength ? title[..MaxTitleLength] : title;
    }

    static string ProcessName(int processId)
    {
        if (ProcessNames.TryGetValue(processId, out string? cached)) return cached;

        string name;
        try
        {
            using Process process = Process.GetProcessById(processId);
            name = process.ProcessName + ".exe";
        }
        catch (ArgumentException)
        {
            name = ""; // the process already exited
        }
        catch (InvalidOperationException)
        {
            name = "";
        }

        if (ProcessNames.Count > 500) ProcessNames.Clear();
        ProcessNames[processId] = name;
        return name;
    }

    // A window without a title bar that covers its whole monitor: borderless-fullscreen games and fullscreen video.
    public static bool IsBorderlessFullscreen(IntPtr window)
    {
        string windowClass = ClassName(window);
        if (windowClass is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // desktop and taskbar

        long style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
        if ((style & WS_CAPTION) == WS_CAPTION) return false;

        if (!GetWindowRect(window, out RECT bounds)) return false;
        var monitor = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), ref monitor)) return false;

        return bounds.Left <= monitor.rcMonitor.Left
            && bounds.Top <= monitor.rcMonitor.Top
            && bounds.Right >= monitor.rcMonitor.Right
            && bounds.Bottom >= monitor.rcMonitor.Bottom;
    }

    static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    const int QUNS_PRESENTATION_MODE = 4;
    const int GWL_STYLE = -16;
    const long WS_CAPTION = 0x00C00000;
    const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}
