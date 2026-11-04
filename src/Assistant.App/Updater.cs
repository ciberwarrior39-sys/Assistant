using System.Reflection;
using System.Windows.Threading;
using Assistant.Core.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace Assistant.App;

/// <summary>
/// Updates from the project's GitHub Releases (Velopack). Once a day it looks for a newer version. In "auto" mode it
/// downloads it quietly, and the update is installed the next time the app starts (or right away from the tray's
/// "Restart to update"); in "notify" mode it only says a version is out, and installs it when the user asks; "off"
/// doesn't look. Does nothing when running from source or when no repository is configured.
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

    /// <summary>"notify" mode: a newer version is out (not downloaded yet), with its version.</summary>
    public event Action<string>? Available;

    /// <summary>A newer version found in "notify" mode, until it's installed.</summary>
    public string? AvailableVersion { get; private set; }

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

    /// <summary>The downloaded version waiting for a restart, or null.</summary>
    public string? PendingVersion => manager?.UpdatePendingRestart?.Version?.ToString();

    /// <param name="mode">The current setting, read at each daily check: auto, notify or off.</param>
    public void Start(Func<string> mode)
    {
        if (manager is null) return;
        timer.Interval = FirstCheckAfter;
        timer.Tick += async (_, _) =>
        {
            timer.Interval = CheckEvery;
            switch (mode())
            {
                case "auto": await CheckAsync(); break;
                case "notify": await LookAsync(); break;
            }
        };
        timer.Start();
    }

    // "notify": only looks; nothing is downloaded until the user says Install.
    async Task LookAsync()
    {
        if (manager is null || checking || HasPendingUpdate) return;
        checking = true;
        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return;
            string version = update.TargetFullRelease.Version.ToString();
            if (version == AvailableVersion) return; // already said
            AvailableVersion = version;
            Available?.Invoke(version);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Error("Update check failed", ex);
        }
        finally
        {
            checking = false;
        }
    }

    /// <summary>"Install version …" (tray, notify mode): downloads it and restarts into it. Returns a problem, or null.</summary>
    public async Task<string?> InstallNowAsync()
    {
        string result = await CheckNowAsync();
        if (!HasPendingUpdate) return result;
        RestartToUpdate();
        return null;
    }

    /// <summary>
    /// "Check for updates" in the tray and Settings: checks now (even with automatic updates off),
    /// downloads a newer version, and says what happened in a sentence for the user.
    /// </summary>
    public async Task<string> CheckNowAsync()
    {
        if (manager is null)
            return "Updates work in the installed app only (made with build-release.ps1 and -Repo), not when running from source.";
        if (PendingVersion is { } ready) return $"Version {ready} is downloaded. Choose Restart to update to install it.";
        if (checking) return "Already checking for updates…";
        return await CheckAsync() ?? $"You have the newest version ({CurrentVersion}).";
    }

    // Returns what happened, or null when there's nothing new.
    async Task<string?> CheckAsync()
    {
        if (manager is null || checking || HasPendingUpdate) return null;
        checking = true;
        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return null;

            string version = update.TargetFullRelease.Version.ToString();
            DiagnosticLog.Info($"Downloading update {version}");
            await manager.DownloadUpdatesAsync(update);
            Ready?.Invoke(version);
            return $"Version {version} is downloaded. Choose Restart to update to install it.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Offline, GitHub rate limits, no releases yet: the daily check tries again tomorrow.
            DiagnosticLog.Error("Update check failed", ex);
            return "Couldn't check for updates (offline, or GitHub didn't answer). Try again later.";
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
