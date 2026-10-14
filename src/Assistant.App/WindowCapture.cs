using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Assistant.Core.Activity;
using Assistant.Core.Tools;

namespace Assistant.App;

/// <summary>
/// Remembers which window the user was using (not the assistant's own) and captures just that window
/// when asked. Excluded and minimized windows are refused. The picture is scaled down so it fits the
/// small model's context, kept in memory only, and never written to disk.
/// </summary>
public sealed class WindowCapture(Exclusions exclusions)
{
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
        if (lastWindow is not { } target || !IsWindow(target.Window))
            return new WindowShot(null, "", "I don't know which window you mean. Click the window, then ask me again.");

        string name = $"{AppNames.Name(target.App)} - {Shorten(target.Title)}";
        if (exclusions.IsExcluded(target.App, target.Title))
            return new WindowShot(null, name, "That window is in exclusions.json, so I won't look at it.");
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
