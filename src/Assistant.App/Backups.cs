using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core.Actions;
using Assistant.Core.Backup;
using Assistant.Core.Diagnostics;
using Assistant.Core.Storage;

namespace Assistant.App;

/// <summary>
/// Backups in the app: runs BackupEngine in the background (one at a time), once a day by itself when the destination
/// is there (a USB drive plugged in), answers the chat's backup requests, and reminds the user after 7 days without one.
/// What to back up and where are settings (config.json); when it last ran is kept apart in backup-state.json.
/// </summary>
public sealed class Backups(AppConfig config, string configPath, string statePath, Action openSetup, Action<string, Action?> showNote)
{
    static readonly TimeSpan Daily = TimeSpan.FromHours(20);   // "once a day", without drifting later each day
    static readonly TimeSpan NudgeAfter = TimeSpan.FromDays(7);

    readonly BackupState state = BackupState.Load(statePath);
    CancellationTokenSource? running;

    /// <summary>A backup started or finished (Settings shows the status).</summary>
    public event Action? Changed;

    public bool Running => running is not null;
    public bool IsSetUp => config.BackupDestination.Length > 0;

    /// <summary>The folders backed up: the user's list, or Documents, Desktop and Pictures.</summary>
    public IReadOnlyList<string> Folders => config.BackupFolders.Count > 0 ? config.BackupFolders : BackupEngine.DefaultFolders();

    public string StatusText()
    {
        if (!IsSetUp) return "Backups aren't set up yet: nothing is copied anywhere.";
        if (Running) return "A backup is running now.";
        string where = $"To {config.BackupDestination}.";
        if (state.LastBackupUtc is not { } last) return $"Set up, but no backup has finished yet. {where}";
        DateTime local = last.ToLocalTime();
        string when = local.Date == DateTime.Today ? $"today at {local:HH:mm}"
                    : local.Date == DateTime.Today.AddDays(-1) ? $"yesterday at {local:HH:mm}"
                    : $"{(DateTime.Today - local.Date).Days} days ago ({local:d MMM})";
        return $"Last backup {when}: {state.LastSummary} {where}";
    }

    /// <summary>
    /// Sets where backups go (filling in the default folders and an id the first time). Saved straight away unless the
    /// Settings window is the one changing it (its Save writes the file, and its Cancel can undo it).
    /// </summary>
    public void SetDestination(string path, bool save)
    {
        config.BackupDestination = path;
        if (config.BackupId.Length == 0) config.BackupId = Guid.NewGuid().ToString("N");
        if (config.BackupFolders.Count == 0) config.BackupFolders = [.. BackupEngine.DefaultFolders()];
        if (state.SetUpUtc is null)
        {
            state.SetUpUtc = DateTime.UtcNow;
            state.Save(statePath);
        }
        if (save) config.Save(configPath);
    }

    /// <summary>Starts a backup in the background; the result shows as a note on the character. False if one is running.</summary>
    public bool StartInBackground()
    {
        if (Running || !IsSetUp) return false;
        _ = RunAndTellAsync();
        return true;
    }

    async Task RunAndTellAsync()
    {
        BackupOutcome outcome = await RunAsync();
        showNote(outcome.Finished ? "backup done" : "backup stopped", openSetup);
    }

    /// <summary>Runs one backup now (in the background thread) and remembers how it went.</summary>
    public async Task<BackupOutcome> RunAsync()
    {
        if (Running) return new BackupOutcome { Problem = "A backup is already running." };
        if (!IsSetUp) return new BackupOutcome { Problem = "Backups aren't set up yet." };
        string destination = config.BackupDestination, id = config.BackupId;
        string? where = await Task.Run(() => BackupEngine.Resolve(destination, id));
        if (where is null)
            return new BackupOutcome { Problem = $"The backup drive isn't there ({destination}). Plug it in, then try again." };

        running = new CancellationTokenSource();
        Changed?.Invoke();
        try
        {
            List<string> folders = [.. Folders];
            CancellationToken token = running.Token;
            BackupOutcome outcome = await Task.Run(() => BackupEngine.Run(folders, where, id, DateTime.Now, token));
            if (outcome.Finished) state.LastBackupUtc = DateTime.UtcNow;
            state.LastSummary = outcome.Summary();
            state.Save(statePath);
            // Counts only, never file or folder names.
            DiagnosticLog.Info($"Backup: {outcome.Added} added, {outcome.Updated} updated, {outcome.Removed} removed, {outcome.Skipped} skipped, " +
                               $"{outcome.Bytes >> 20} MB in {outcome.Took.TotalSeconds:0} s{(outcome.Problem is null ? "" : ", stopped: " + outcome.Problem)}");
            return outcome;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            DiagnosticLog.Error("Backup failed", ex);
            return new BackupOutcome { Problem = $"The backup stopped: {ex.Message}" };
        }
        finally
        {
            running.Dispose();
            running = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Stops a running backup after the file it's copying (what's done stays done).</summary>
    public void Stop() => running?.Cancel();

    /// <summary>
    /// Every few minutes: the daily backup when it's due and the destination is there, the one-time offer to set backups
    /// up, and the reminder after 7 days without a finished backup (at most once a day). Not during a game.
    /// </summary>
    public async Task TickAsync(bool gaming)
    {
        if (gaming || Running) return;
        if (!IsSetUp)
        {
            if (!state.Offered)
            {
                state.Offered = true;
                state.Save(statePath);
                showNote("your files have no backup", openSetup);
            }
            return;
        }

        DateTime? last = state.LastBackupUtc;
        if (config.BackupAuto && (last is null || DateTime.UtcNow - last > Daily))
        {
            string destination = config.BackupDestination, id = config.BackupId;
            if (await Task.Run(() => BackupEngine.Resolve(destination, id)) is not null)
            {
                BackupOutcome outcome = await RunAsync();
                if (outcome.Problem is not null) showNote("backup didn't finish", openSetup);
                return;
            }
        }

        DateTime since = last ?? state.SetUpUtc ?? DateTime.UtcNow;
        string today = DateTime.Today.ToString("yyyy-MM-dd");
        if (DateTime.UtcNow - since > NudgeAfter && state.LastNudge != today)
        {
            state.LastNudge = today;
            state.Save(statePath);
            showNote($"no backup for {(DateTime.UtcNow - since).Days} days", openSetup);
        }
    }

    // ---- The chat --------------------------------------------------------------------------------------------------

    public ActionReply Handle(BackupIntent intent) => intent switch
    {
        BackupNow => Now(),
        BackupStatus => Status(),
        SetUpBackups => SetUp(),
        SetBackupDestination destination => UseDestination(destination.Path),
        AddBackupFolder folder => AddFolder(folder.Path),
        OpenBackup => Open(),
        _ => new ActionReply("I can't do that with backups."),
    };

    ActionReply Now()
    {
        if (!IsSetUp) return Offer("Backups aren't set up yet. Where should the copies go?");
        if (Running) return new ActionReply("A backup is already running. I'll show a note when it's done.");
        string destination = config.BackupDestination;
        if (BackupEngine.Resolve(destination, config.BackupId) is null)
            return new ActionReply($"The backup drive isn't there ({destination}). Plug it in and ask again; it also runs by itself once it's plugged in.");
        StartInBackground();
        return new ActionReply($"Backing up {Names()} to {destination} in the background. You can keep working; I'll show a note when it's done.");
    }

    ActionReply Status()
    {
        if (!IsSetUp) return Offer("Your files aren't backed up anywhere yet. Where should the copies go?");
        return new ActionReply($"{StatusText()}\n\nBacked up: {Names()}.{(config.BackupAuto ? " It runs by itself once a day when the destination is there." : "")}")
        {
            Options = [new ActionOption("Back up now", new BackupNow()), new ActionOption("Open the backup", new OpenBackup()),
                       new ActionOption("Backup settings", new SetUpBackups())],
        };
    }

    ActionReply SetUp()
    {
        openSetup();
        return new ActionReply("Here are the backup settings: where the copies go, which folders, and whether it runs by itself.");
    }

    // The quick way: a button per drive, plus Settings for any other folder.
    ActionReply Offer(string question)
    {
        IReadOnlyList<BackupDrive> drives = BackupEngine.Drives();
        var options = drives.Take(4)
            .Select(drive => new ActionOption($"{drive.Root.TrimEnd('\\')} {drive.Name} ({BackupEngine.Size(drive.FreeBytes)} free)",
                                              new SetBackupDestination(drive.Root)))
            .ToList();
        options.Add(new ActionOption("Choose a folder…", new SetUpBackups()));
        string text = question + " " + (drives.Any(drive => drive.Removable)
            ? "A USB or external drive is best: it survives if the PC breaks."
            : "Plug in a USB or external drive for the safest backup, or choose any folder (a OneDrive folder works too).");
        return new ActionReply(text) { Options = options };
    }

    ActionReply UseDestination(string path)
    {
        if (!Directory.Exists(path)) return new ActionReply($"{path} isn't there anymore. Plug the drive in and try again.");
        SetDestination(path, save: true);
        string? warning = BackupEngine.SameDriveWarning(Folders, path);
        StartInBackground();
        return new ActionReply($"From now on, {Names()} are backed up to {Path.Combine(path, BackupEngine.FolderName)}" +
                               $"{(config.BackupAuto ? ", once a day whenever it's there" : "")}. The first backup is running now and can take a while; " +
                               "after that only new and changed files are copied. Older versions of changed or deleted files are kept 30 days." +
                               (warning is null ? "" : "\n\n" + warning))
        {
            Options = [new ActionOption("Backup settings", new SetUpBackups())],
        };
    }

    ActionReply AddFolder(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ActionReply($"\"{path}\" isn't a folder path I can use.");
        }
        if (!Directory.Exists(full)) return new ActionReply($"I can't find the folder {full}.");
        if (config.BackupFolders.Count == 0) config.BackupFolders = [.. BackupEngine.DefaultFolders()];
        if (config.BackupFolders.Contains(full, StringComparer.OrdinalIgnoreCase)) return new ActionReply($"{full} is already backed up.");
        config.BackupFolders.Add(full);
        config.Save(configPath);
        return new ActionReply($"Added {full}. Backed up now: {Names()}." + (IsSetUp ? "" : " Backups aren't set up yet, though."))
        {
            Options = new[] { IsSetUp ? new ActionOption("Back up now", new BackupNow()) : new ActionOption("Set up backups", new BackupNow()) },
        };
    }

    ActionReply Open()
    {
        string? where = IsSetUp ? BackupEngine.Resolve(config.BackupDestination, config.BackupId) : null;
        string folder = where is null ? "" : BackupEngine.PcFolder(where);
        if (!Directory.Exists(folder)) return new ActionReply("There's no backup to open yet, or its drive isn't plugged in.");
        Shell.OpenFolder(folder);
        return new ActionReply("Opened the backup. Your files are in Current; earlier versions of changed or deleted files are in Older versions.");
    }

    string Names() => string.Join(", ", BackupEngine.Labels(Folders).Select(item => item.Label));
}
