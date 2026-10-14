using System.Diagnostics;

namespace Assistant.App;

/// <summary>Opening folders and files through Windows Explorer.</summary>
static class Shell
{
    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public static void RevealFile(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    /// <summary>Opens a file with its default app. Throws Win32Exception when Windows can't open it.</summary>
    public static void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
