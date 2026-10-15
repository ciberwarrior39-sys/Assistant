using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Setup;

/// <summary>
/// Everything (voidtools) is a separate app with its own background service, so it's offered, never
/// installed silently: the wizard downloads the official installer and runs it, and the user clicks through
/// it (one Windows admin prompt). If the download link has moved, the downloads page opens instead.
/// </summary>
public static class EverythingSetup
{
    const string Installer = "https://www.voidtools.com/Everything-1.4.1.1032.x64-Setup.exe";
    public const string DownloadsPage = "https://www.voidtools.com/downloads/";

    /// <summary>True if the installer ran and finished; false if the user cancelled it or the page was opened instead.</summary>
    public static async Task<bool> InstallAsync(HttpClient http, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        string target = Path.Combine(Path.GetTempPath(), "Assistant-setup", "Everything-Setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            await Downloads.FileAsync(http, new Uri(Installer), target, "Downloading Everything", progress, ct);
        }
        catch (HttpRequestException ex)
        {
            DiagnosticLog.Error("Everything installer download failed; opening the downloads page", ex);
            Process.Start(new ProcessStartInfo(DownloadsPage) { UseShellExecute = true });
            return false;
        }

        progress.Report(new SetupProgress("Waiting for the Everything installer to finish", 0, 0));
        try
        {
            using Process? installer = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            if (installer is null) return false;
            await installer.WaitForExitAsync(ct);
            return installer.ExitCode == 0;
        }
        catch (Win32Exception ex)
        {
            DiagnosticLog.Error("Everything installer was cancelled", ex); // e.g. "No" on the admin prompt
            return false;
        }
        finally
        {
            try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
