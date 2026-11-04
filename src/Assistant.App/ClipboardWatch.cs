using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Assistant.App;

/// <summary>
/// Notes when the clipboard last changed (Windows tells the app; nothing is read), so "Help with selected text" only
/// falls back to copied text that was copied just now, not something from an hour ago.
/// </summary>
public static class ClipboardWatch
{
    const int WmClipboardUpdate = 0x031D;
    static DateTime lastChange = DateTime.MinValue;

    public static bool ChangedWithin(TimeSpan span) => DateTime.UtcNow - lastChange <= span;

    /// <summary>Starts listening, on one of the app's windows (it needs a window handle).</summary>
    public static void Start(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        if (HwndSource.FromHwnd(handle) is not { } source || !AddClipboardFormatListener(handle)) return;
        source.AddHook((IntPtr _, int message, IntPtr _, IntPtr _, ref bool _) =>
        {
            if (message == WmClipboardUpdate) lastChange = DateTime.UtcNow;
            return IntPtr.Zero;
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr window);
}
