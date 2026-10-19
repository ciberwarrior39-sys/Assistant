using System.IO;
using Microsoft.Win32;
using Assistant.Core.Diagnostics;

namespace Assistant.App;

/// <summary>
/// "Start with Windows": an entry under the current user's Run key (no admin rights, visible and switchable in
/// Task Manager > Startup apps). Only for the installed app: a copy run from source would register a build folder.
/// </summary>
static class StartWithWindows
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Assistant";

    /// <summary>
    /// Installed by the setup file: Velopack keeps the app in ...\DesktopAssistant\current\, with Update.exe one level up.
    /// "current" keeps its name across updates, so the registered path stays valid.
    /// </summary>
    public static bool IsInstalled =>
        Environment.ProcessPath is { } exe
        && Path.GetDirectoryName(Path.GetDirectoryName(exe)) is { } root
        && File.Exists(Path.Combine(root, "Update.exe"));

    /// <summary>Makes the Run entry match the setting. Does nothing when running from source.</summary>
    public static void Sync(bool on)
    {
        if (!IsInstalled || Environment.ProcessPath is not { } exe) return;
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            string wanted = $"\"{exe}\"";
            if (!on) key.DeleteValue(ValueName, throwOnMissingValue: false);
            else if (key.GetValue(ValueName) as string != wanted) key.SetValue(ValueName, wanted);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            DiagnosticLog.Error("Could not change Start with Windows", ex);
        }
    }

    /// <summary>Uninstall: no entry pointing at a deleted app is left behind.</summary>
    public static void Remove()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Uninstalling must not fail because of it.
        }
    }
}
