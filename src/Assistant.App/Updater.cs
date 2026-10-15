using System.Reflection;
using System.Windows.Threading;
using Assistant.Core.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace Assistant.App;

/// <summary>
/// Automatic updates from the project's GitHub Releases (Velopack). Once a day it looks for a newer version
/// and downloads it quietly; the update is installed the next time the app starts, or right away from the
/// tray's "Restart to update". Does nothing when running from source or when no repository is configured.
/// </summary>
public sealed class Updater
{
    static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(1); // don't slow down startup
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    readonly UpdateManager? manager;
    readonly DispatcherTimer timer = new();
    bool checking;

    /// <summary>Raised on the UI thread when an update has been downloaded, with its version.</summary>
    public event Action<string>? Ready;

    public Updater()
    {
        // build-release.ps1 writes the repository into the app as assembly metadata.
        string repo = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "UpdateRepo")?.Value ?? "";
        if (repo.Length == 0) return;

        try
        {
            var candidate = new UpdateManager(new GithubSource(repo, null, false, null));
            if (candidate.IsInstalled) manager = candidate; // not when running from source (dotnet run)
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Error("Updates unavailable", ex);
        }
    }

    /// <summary>The installed version, or null when running from source.</summary>
    public string? CurrentVersion => manager?.CurrentVersion?.ToString();

    public bool HasPendingUpdate => manager?.UpdatePendingRestart is not null;

    public void Start(Func<bool> enabled)
    {
        if (manager is null) return;
        timer.Interval = FirstCheckAfter;
        timer.Tick += async (_, _) =>
        {
            timer.Interval = CheckEvery;
            if (enabled()) await CheckAsync();
        };
        timer.Start();
    }

    async Task CheckAsync()
    {
        if (manager is null || checking || HasPendingUpdate) return;
        checking = true;
        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return;

            DiagnosticLog.Info($"Downloading update {update.TargetFullRelease.Version}");
            await manager.DownloadUpdatesAsync(update);
            Ready?.Invoke(update.TargetFullRelease.Version.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Offline, GitHub rate limits, no releases yet: try again tomorrow.
            DiagnosticLog.Error("Update check failed", ex);
        }
        finally
        {
            checking = false;
        }
    }

    /// <summary>Installs the downloaded update now and starts the new version.</summary>
    public void RestartToUpdate()
    {
        if (manager?.UpdatePendingRestart is { } pending) manager.ApplyUpdatesAndRestart(pending);
    }
}
