using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Assistant.Core.Activity;
using Assistant.Core.Tools;

namespace Assistant.App;

/// <summary>The window to read marked text from, or why not (Problem).</summary>
public sealed record TextSource(IntPtr Window, string Name, string? Problem, bool ClipboardAllowed);

/// <summary>
/// Remembers which window the user was using (not the assistant's own) and captures just that window
/// when asked. Excluded and minimized windows are refused. The picture is scaled down so it fits the
/// small model's context, kept in memory only, and never written to disk.
///
/// Games are never captured: nothing during game mode, and no window that looks like a game (fullscreen, or in
/// the game list), even when the chat bubble is open over it. Anti-cheat software watches for programs that
/// reach into a game's window, and PrintWindow asks the window itself to draw.
/// </summary>
public sealed class WindowCapture(Exclusions exclusions, IReadOnlyCollection<string> gameExecutables)
{
    /// <summary>Set by game mode: while a game is in front, nothing is captured at all.</summary>
    public bool GameMode { get; set; }

    const int MaxSide = 1024; // about 600 image tokens: room left in a 2048-token context
    const uint PwRenderFullContent = 2; // also captures GPU-drawn windows like browsers

    ForegroundSnapshot? lastWindow;

    /// <summary>Call with every snapshot; the assistant's own windows are skipped.</summary>
    public void Observe(ForegroundSnapshot snapshot)
    {
        if (snapshot.ProcessId != Environment.ProcessId && snapshot.Window != IntPtr.Zero && snapshot.App.Length > 0)
            lastWindow = snapshot;
    }

    public WindowShot Capture()
    {
        if (GameMode)
            return new WindowShot(null, "", "Game mode is on, so I don't look at the screen until the game is closed.");
        if (lastWindow is not { } target || !IsWindow(target.Window))
            return new WindowShot(null, "", "I don't know which window you mean. Click the window, then ask me again.");

        string name = $"{AppNames.Name(target.App)} - {Shorten(target.Title)}";
        if (exclusions.IsExcluded(target.App, target.Title))
            return new WindowShot(null, name, "That window is in exclusions.json, so I won't look at it.");
        if (target.FullscreenGame || Desktop.IsBorderlessFullscreen(target.Window)
            || gameExecutables.Contains(target.App, StringComparer.OrdinalIgnoreCase))
            return new WindowShot(null, name, "That window fills the whole screen like a game or a video, so I won't look at it. " +
                                              "If it isn't a game, take it out of fullscreen and ask again.");
        if (IsIconic(target.Window))
            return new WindowShot(null, name, "That window is minimized. Open it, then ask me again.");
        if (!GetWindowRect(target.Window, out RECT rect) || rect.Width <= 0 || rect.Height <= 0)
            return new WindowShot(null, name, "I couldn't capture that window.");

        using var full = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(full))
        {
            IntPtr dc = graphics.GetHdc();
            bool printed = PrintWindow(target.Window, dc, PwRenderFullContent);
            graphics.ReleaseHdc(dc);
            // Some windows refuse to draw themselves; take what's on screen at that spot instead.
            if (!printed) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, full.Size);
        }

        double scale = Math.Min(1.0, (double)MaxSide / Math.Max(rect.Width, rect.Height));
        var size = new Size(Math.Max(1, (int)(rect.Width * scale)), Math.Max(1, (int)(rect.Height * scale)));
        using var small = new Bitmap(size.Width, size.Height);
        using (Graphics graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(full, 0, 0, size.Width, size.Height);
        }

        using var jpeg = new MemoryStream();
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(encoder => encoder.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
        small.Save(jpeg, codec, quality);
        return new WindowShot(jpeg.ToArray(), name);
    }

    /// <summary>
    /// "Help with selected text": the window whose marked text may be read, under the same rules as screenshots
    /// (no games, nothing in exclusions.json). <see cref="TextSource.ClipboardAllowed"/>: when the marked text can't be
    /// read, the last copied text may be offered instead (not for a refused window).
    /// </summary>
    public TextSource TextTarget()
    {
        if (GameMode)
            return new(IntPtr.Zero, "", "Game mode is on, so I don't read other windows until the game is closed.", false);
        if (lastWindow is not { } target || !IsWindow(target.Window))
            return new(IntPtr.Zero, "", "I don't know which window you mean. Mark the text, then right-click me again.", true);

        string name = AppNames.Name(target.App);
        if (exclusions.IsExcluded(target.App, target.Title))
            return new(target.Window, name, "That window is in exclusions.json, so I won't read it.", false);
        if (target.FullscreenGame || Desktop.IsBorderlessFullscreen(target.Window)
            || gameExecutables.Contains(target.App, StringComparer.OrdinalIgnoreCase))
            return new(target.Window, name, "That window fills the whole screen like a game or a video, so I won't read it.", false);
        return new(target.Window, name, null, true);
    }

    static string Shorten(string title) => title.Length > 60 ? title[..60] + "…" : title;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
