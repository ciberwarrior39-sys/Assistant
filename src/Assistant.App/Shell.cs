using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Assistant.App;

/// <summary>Opening folders and files through Windows Explorer, and the app's icon.</summary>
static class Shell
{
    static readonly Uri IconUri = new("pack://application:,,,/assistant.ico");

    /// <summary>The blob icon for windows that show in the taskbar (Settings, the setup wizard).</summary>
    public static ImageSource? WindowIcon
    {
        get
        {
            try
            {
                return BitmapFrame.Create(IconUri);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>The blob icon at the tray's size; Windows' generic one if it can't be read.</summary>
    public static System.Drawing.Icon TrayIcon()
    {
        try
        {
            if (Application.GetResourceStream(IconUri) is { } resource)
            {
                using Stream stream = resource.Stream;
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
        }
        return System.Drawing.SystemIcons.Application;
    }

    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public static void RevealFile(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    /// <summary>Opens a file with its default app. Throws Win32Exception when Windows can't open it.</summary>
    public static void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
