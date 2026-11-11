using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Assistant.Core.Actions;
using Assistant.Core.Diagnostics;
using Assistant.Core.Search;

namespace Assistant.App;

/// <summary>
/// "Why is my PC slow?" and "my disk is full": what this PC is doing right now, in plain words, with the fixes as
/// buttons. Everything is read on this computer by code; nothing goes to a model or the internet.
/// Nothing is done without a button press, and every fix can be undone or is harmless: apps are closed the polite way
/// (they can still ask to save), startup apps are switched off the way Task Manager does it (and can be switched back
/// on), old installers go to the Recycle Bin, and only temporary files older than a day are deleted for good.
/// </summary>
static class CheckUp
{
    const int ShownApps = 5;          // the heaviest apps listed
    const int CloseButtons = 3;       // at most this many "Close …" buttons
    const int StartupButtons = 3;     // and "Don't start … with Windows" buttons
    const long HeavyBytes = 500L << 20;
    const double HeavyCpu = 10;       // percent of the whole processor
    static readonly TimeSpan OldInstaller = TimeSpan.FromDays(30);
    static readonly TimeSpan OldTemp = TimeSpan.FromDays(1);

    static readonly string[] InstallerExtensions = [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".iso"];

    // Windows' own processes: listed if they're heavy, but never offered to close.
    static readonly HashSet<string> NeverClose = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "wininit", "services", "lsass", "svchost", "System", "Registry", "smss",
        "Memory Compression", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost",
        "ApplicationFrameHost", "SystemSettings", "LockApp", "sihost", "taskhostw", "ctfmon", "fontdrvhost", "audiodg",
        "MsMpEng", "NisSrv", "SecurityHealthService", "Taskmgr",
    };

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    // ---- Speed --------------------------------------------------------------------------------------------------

    public static async Task<ActionReply> SpeedAsync(bool gameMode, CancellationToken ct)
    {
        (double cpuPercent, List<AppLoad> apps) = await Task.Run(() => MeasureApps(ct), ct);
        Memory memory = Memory.Now();
        DriveInfo? system = SystemDrive();
        List<StartupEntry> startup = StartupEntries();
        TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        var text = new StringBuilder();
        // What stands out first, then the details.
        bool memoryTight = memory.LoadPercent >= 85;
        bool cpuBusy = cpuPercent >= 80;
        bool diskTight = system is not null && system.TotalFreeSpace < Math.Min(10L << 30, system.TotalSize / 10);
        if (memoryTight)
            text.AppendLine($"Memory is the tight spot: {Gb(memory.UsedBytes)} of {Gb(memory.TotalBytes)} is in use ({memory.LoadPercent}%). " +
                            "When it fills up, Windows starts using the disk instead, and everything slows down.");
        if (cpuBusy)
            text.AppendLine($"The processor is busy: {cpuPercent:0}% right now" +
                            (apps.OrderByDescending(app => app.CpuPercent).FirstOrDefault() is { CpuPercent: >= HeavyCpu } top
                                ? $", mostly {top.Name} ({top.CpuPercent:0}%)."
                                : "."));
        if (diskTight)
            text.AppendLine($"The system drive is nearly full ({Gb(system!.TotalFreeSpace)} free). Windows slows down when it has no room to work.");
        if (!memoryTight && !cpuBusy && !diskTight)
            text.AppendLine($"Nothing is overloaded right now: memory {memory.LoadPercent}%, processor {cpuPercent:0}%. " +
                            "If it's slow only at certain moments, ask me again while it's happening.");

        text.AppendLine();
        text.AppendLine("Using the most right now:");
        foreach (AppLoad app in apps.OrderByDescending(app => app.Bytes).Take(ShownApps))
            text.AppendLine($"- {app.Name}: {Mb(app.Bytes)} of memory{(app.Processes > 1 ? $" ({app.Processes} processes)" : "")}" +
                            (app.CpuPercent >= 1 ? $", {app.CpuPercent:0}% processor" : ""));

        List<StartupEntry> starting = startup.Where(entry => entry.Enabled).ToList();
        if (starting.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"{starting.Count} app{(starting.Count == 1 ? "" : "s")} start with Windows: " +
                            string.Join(", ", starting.Select(entry => entry.Name)) + ". " +
                            "Each one slows the start a little and keeps using memory, so switch off the ones you don't need straight away.");
        }

        if (uptime > TimeSpan.FromDays(3))
        {
            text.AppendLine();
            text.AppendLine($"The PC hasn't restarted in {uptime.Days} days. A restart (not shut down, which Windows only half does) " +
                            "clears out apps that slowly take more memory and finishes updates.");
        }

        var options = new List<ActionOption>();
        // Close buttons only for apps with a window, not Windows itself or this app, and not while a game runs.
        if (!gameMode)
        {
            options.AddRange(apps
                .Where(app => app.HasWindow && !NeverClose.Contains(app.ProcessName) && !IsThisApp(app.ProcessName)
                              && (app.Bytes >= HeavyBytes || app.CpuPercent >= HeavyCpu))
                .OrderByDescending(app => app.Bytes)
                .Take(CloseButtons)
                .Select(app => new ActionOption($"Close {app.Name}", new CloseApp(app.Name, app.ProcessName))));
        }
        options.AddRange(starting
            .Where(entry => entry.Changeable)
            .Take(StartupButtons)
            .Select(entry => new ActionOption($"Don't start {entry.Name} with Windows", new StartupApp(entry.Name, entry.Entry, entry.InStartupFolder, false))));
        if (starting.Any(entry => !entry.Changeable) || starting.Count(entry => entry.Changeable) > StartupButtons)
            options.Add(new ActionOption("All startup apps", new OpenSettings("Startup apps", "ms-settings:startupapps")));
        if (diskTight) options.Add(new ActionOption("Find what's taking space", new PcCheckUp(CheckUpKind.Space)));

        DiagnosticLog.Info($"Speed check-up: memory {memory.LoadPercent}%, processor {cpuPercent:0}%, {starting.Count} startup apps");
        return new ActionReply(text.ToString().TrimEnd()) { Options = options };
    }

    // Every process for one second: memory now, and processor time used in that second, grouped by app.
    static (double CpuPercent, List<AppLoad> Apps) MeasureApps(CancellationToken ct)
    {
        Dictionary<int, (string Name, TimeSpan Cpu)> before = Snapshot();
        var stopwatch = Stopwatch.StartNew();
        Thread.Sleep(1000);
        ct.ThrowIfCancellationRequested();

        var groups = new Dictionary<string, AppLoad>(StringComparer.OrdinalIgnoreCase);
        double totalCpu = 0;
        double scale = 100.0 / (stopwatch.Elapsed.TotalMilliseconds * Environment.ProcessorCount);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == 0) continue; // the idle process
                    string name = process.ProcessName;
                    double cpu = 0;
                    if (before.TryGetValue(process.Id, out var earlier) && earlier.Name == name)
                        cpu = Math.Max(0, (process.TotalProcessorTime - earlier.Cpu).TotalMilliseconds * scale);
                    totalCpu += cpu;

                    if (!groups.TryGetValue(name, out AppLoad? app))
                        groups[name] = app = new AppLoad(name);
                    app.Bytes += process.WorkingSet64;
                    app.CpuPercent += cpu;
                    app.Processes++;
                    if (!app.HasWindow && process.MainWindowHandle != IntPtr.Zero)
                    {
                        app.HasWindow = true;
                        app.Name = FriendlyName(process) ?? app.Name;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Exited meanwhile, or protected: skipped.
                }
            }
        }

        // Friendly names for the heavy ones that have no window (background apps).
        foreach (AppLoad app in groups.Values.Where(app => !app.HasWindow).OrderByDescending(app => app.Bytes).Take(ShownApps))
        {
            foreach (Process process in Process.GetProcessesByName(app.ProcessName))
            {
                using (process)
                {
                    if (FriendlyName(process) is { } friendly)
                    {
                        app.Name = friendly;
                        break;
                    }
                }
            }
        }
        return (Math.Min(100, totalCpu), groups.Values.ToList());
    }

    static Dictionary<int, (string Name, TimeSpan Cpu)> Snapshot()
    {
        var times = new Dictionary<int, (string, TimeSpan)>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    times[process.Id] = (process.ProcessName, process.TotalProcessorTime);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected or gone.
                }
            }
        }
        return times;
    }

    // "Google Chrome" instead of "chrome", from the program's own description.
    static string? FriendlyName(Process process)
    {
        try
        {
            string? description = process.MainModule?.FileVersionInfo.FileDescription?.Trim();
            return string.IsNullOrEmpty(description) || description.Length > 40 ? null : description;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    static readonly string ThisApp = CurrentProcessName();

    static string CurrentProcessName()
    {
        using Process self = Process.GetCurrentProcess();
        return self.ProcessName;
    }

    static bool IsThisApp(string processName) => processName.Equals(ThisApp, StringComparison.OrdinalIgnoreCase);

    sealed class AppLoad(string processName)
    {
        public string ProcessName { get; } = processName;
        public string Name { get; set; } = processName;
        public long Bytes { get; set; }
        public double CpuPercent { get; set; }
        public int Processes { get; set; }
        public bool HasWindow { get; set; }
    }

    // ---- Closing an app -----------------------------------------------------------------------------------------

    /// <summary>
    /// Sends each of the app's windows the same "close" Windows sends when X is clicked (WM_CLOSE), then waits a few
    /// seconds. No key presses, no forced ending: an app with unsaved work can still ask.
    /// </summary>
    public static async Task<ActionReply> CloseAsync(CloseApp app, CancellationToken ct)
    {
        Process[] processes = Process.GetProcessesByName(app.ProcessName);
        try
        {
            if (processes.Length == 0) return new ActionReply($"{app.Name} isn't running anymore.");
            if (NeverClose.Contains(app.ProcessName) || IsThisApp(app.ProcessName))
                return new ActionReply($"I'd rather not close {app.Name}: Windows needs it.");

            long freeBefore = Memory.Now().AvailableBytes;
            var ids = processes.Select(process => (uint)process.Id).ToHashSet();
            int closed = 0;
            EnumWindows((window, _) =>
            {
                if (IsWindowVisible(window) && GetWindow(window, GwOwner) == IntPtr.Zero
                    && GetWindowThreadProcessId(window, out uint id) != 0 && ids.Contains(id))
                {
                    PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
                    closed++;
                }
                return true;
            }, IntPtr.Zero);
            if (closed == 0) return new ActionReply($"{app.Name} has no window to close. It runs in the background; its own settings or the tray icon can quit it.");

            for (int wait = 0; wait < 10 && processes.Any(IsRunning); wait++)
                await Task.Delay(500, ct);

            long freed = Math.Max(0, Memory.Now().AvailableBytes - freeBefore);
            DiagnosticLog.Info($"Closed {app.ProcessName} from the check-up");
            if (processes.Any(process => IsRunning(process) && HasVisibleWindow(process)))
                return new ActionReply($"{app.Name} is still open. It may be asking you something, like whether to save. Have a look at its window.");
            return new ActionReply(freed >= 100L << 20
                ? $"Closed {app.Name}. About {Mb(freed)} of memory is free again."
                : $"Closed {app.Name}.");
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    static bool IsRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    static bool HasVisibleWindow(Process process)
    {
        bool found = false;
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && GetWindowThreadProcessId(window, out uint id) != 0 && id == (uint)process.Id)
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---- Startup apps -------------------------------------------------------------------------------------------

    sealed record StartupEntry(string Name, string Entry, bool InStartupFolder, bool Enabled, bool Changeable);

    // The current user's Run entries and Startup folder (which this app may switch), then the ones for all users
    // (listed, but switching those needs administrator rights, so Settings > Startup apps does it).
    static List<StartupEntry> StartupEntries()
    {
        var entries = new List<StartupEntry>();
        try
        {
            using (RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                foreach (string name in run?.GetValueNames() ?? Array.Empty<string>())
                {
                    if (name.Length == 0 || name.Equals("Assistant", StringComparison.OrdinalIgnoreCase)) continue; // this app: its own setting
                    entries.Add(new StartupEntry(name, name, false, Enabled(Registry.CurrentUser, ApprovedRun, name), true));
                }
            }

            string userFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            foreach (string file in Directory.Exists(userFolder) ? Directory.GetFiles(userFolder) : Array.Empty<string>())
            {
                string name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(new StartupEntry(Path.GetFileNameWithoutExtension(file), name, true, Enabled(Registry.CurrentUser, ApprovedFolder, name), true));
            }

            foreach ((string key, string approved) in new[] { (RunKey, ApprovedRun), (@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", ApprovedRun32) })
            {
                using RegistryKey? run = Registry.LocalMachine.OpenSubKey(key);
                foreach (string name in run?.GetValueNames() ?? Array.Empty<string>())
                {
                    if (name.Length == 0) continue;
                    entries.Add(new StartupEntry(name, name, false, Enabled(Registry.LocalMachine, approved, name), false));
                }
            }

            string allFolder = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
            foreach (string file in Directory.Exists(allFolder) ? Directory.GetFiles(allFolder) : Array.Empty<string>())
            {
                string name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(new StartupEntry(Path.GetFileNameWithoutExtension(file), name, true, Enabled(Registry.LocalMachine, ApprovedFolder, name), false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DiagnosticLog.Error("Couldn't read the startup apps", ex);
        }
        return entries;
    }

    // Task Manager keeps its on/off switch in StartupApproved: the first byte is even when on, odd when off.
    // No value there means on.
    static bool Enabled(RegistryKey root, string approvedKey, string name)
    {
        using RegistryKey? key = root.OpenSubKey(approvedKey);
        return key?.GetValue(name) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
    }

    /// <summary>Switches one of the current user's startup apps off or on, exactly as Task Manager does.</summary>
    public static ActionReply SetStartup(StartupApp app)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(app.InStartupFolder ? ApprovedFolder : ApprovedRun, writable: true);
        var flags = new byte[12];
        flags[0] = app.Enable ? (byte)0x02 : (byte)0x03;
        if (!app.Enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(flags, 4); // when it was switched off
        key.SetValue(app.Entry, flags, RegistryValueKind.Binary);
        DiagnosticLog.Info($"Startup app switched {(app.Enable ? "on" : "off")} from the check-up");

        return app.Enable
            ? new ActionReply($"{app.Name} starts with Windows again.")
            : new ActionReply($"{app.Name} won't start with Windows anymore (from the next start). Nothing was uninstalled, and it still opens when you start it yourself.")
            {
                Options = [new ActionOption("Turn it back on", app with { Enable = true })],
            };
    }

    // ---- Space --------------------------------------------------------------------------------------------------

    public static async Task<ActionReply> SpaceAsync(CancellationToken ct)
    {
        Task<IReadOnlyList<(FileHit File, long Bytes)>> biggest = EverythingSearch.BiggestFilesAsync(1L << 30, 5, ct);
        (List<FileInfo> installers, List<FileInfo> temp, long downloadsBytes) = await Task.Run(() =>
            (OldInstallers(), OldTempFiles(), FolderBytes(WindowsActions.DownloadsFolder)), ct);
        long binBytes = RecycleBinBytes();

        var text = new StringBuilder();
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady))
        {
            double freeShare = (double)drive.TotalFreeSpace / drive.TotalSize;
            text.AppendLine($"{drive.Name.TrimEnd('\\')} {Gb(drive.TotalFreeSpace)} free of {Gb(drive.TotalSize)}" +
                            (freeShare < 0.1 ? " (nearly full)" : ""));
        }
        text.AppendLine();

        long installerBytes = installers.Sum(file => file.Length);
        long tempBytes = temp.Sum(file => file.Length);
        text.AppendLine("What can go:");
        bool any = false;
        if (tempBytes >= 50L << 20)
        {
            text.AppendLine($"- Temporary files: {Mb(tempBytes)}. Apps leave these behind; nothing needs them anymore.");
            any = true;
        }
        if (installers.Count > 0)
        {
            text.AppendLine($"- Old installers in Downloads: {installers.Count} ({Mb(installerBytes)}), downloaded over a month ago. " +
                            "Once a program is installed, its installer isn't needed.");
            any = true;
        }
        if (binBytes >= 50L << 20)
        {
            text.AppendLine($"- The Recycle Bin: {Mb(binBytes)}. Deleted files still take space until it's emptied.");
            any = true;
        }
        if (!any) text.AppendLine("- Nothing big: temporary files, old installers and the Recycle Bin are all small.");
        if (downloadsBytes >= 5L << 30)
            text.AppendLine($"- Downloads holds {Gb(downloadsBytes)} altogether. Worth a look for things you've finished with.");

        IReadOnlyList<(FileHit File, long Bytes)> big = await biggest;
        if (big.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Your biggest files (only you can tell if they can go):");
            foreach ((FileHit file, long bytes) in big)
                text.AppendLine($"- {file.FileName}: {Gb(bytes)}");
        }

        text.AppendLine();
        text.Append("Windows' own clean-up (Settings > Storage) can also remove old update files, which need administrator rights.");

        var options = new List<ActionOption>();
        if (tempBytes >= 50L << 20) options.Add(new ActionOption($"Delete temporary files ({Mb(tempBytes)})", new CleanUp(CleanUpKind.TempFiles)));
        if (installers.Count > 0) options.Add(new ActionOption($"Move old installers to the Recycle Bin ({Mb(installerBytes)})", new CleanUp(CleanUpKind.OldInstallers)));
        if (binBytes >= 50L << 20) options.Add(new ActionOption("Empty the Recycle Bin", new EmptyRecycleBin(false)));
        options.Add(new ActionOption("Open Storage settings", new OpenSettings("Storage", "ms-settings:storagesense")));

        DiagnosticLog.Info($"Space check-up: temp {tempBytes >> 20} MB, {installers.Count} old installers, bin {binBytes >> 20} MB");
        return new ActionReply(text.ToString().TrimEnd()) { Files = big.Select(item => item.File).ToList(), Options = options };
    }

    /// <summary>"Delete my temp files" in the chat: how much there is, and a button that does it.</summary>
    public static async Task<ActionReply> AskToCleanAsync(CleanUpKind kind, CancellationToken ct)
    {
        if (kind == CleanUpKind.TempFiles)
        {
            List<FileInfo> temp = await Task.Run(OldTempFiles, ct);
            long bytes = temp.Sum(file => file.Length);
            if (temp.Count == 0) return new ActionReply("There are no old temporary files to delete.");
            return new ActionReply($"There are {Mb(bytes)} of temporary files ({temp.Count:N0} files) that haven't been touched for a day. " +
                                   "Apps leave these behind and nothing needs them anymore. Delete them? This can't be undone, but nothing of yours is in there.")
            {
                Options = [new ActionOption($"Delete temporary files ({Mb(bytes)})", new CleanUp(CleanUpKind.TempFiles)), new ActionOption("Cancel", null)],
            };
        }

        List<FileInfo> installers = await Task.Run(OldInstallers, ct);
        if (installers.Count == 0) return new ActionReply("There are no installers in Downloads older than a month.");
        long size = installers.Sum(file => file.Length);
        string names = string.Join(", ", installers.Take(6).Select(file => file.Name)) + (installers.Count > 6 ? $" and {installers.Count - 6} more" : "");
        return new ActionReply($"{installers.Count} installer{(installers.Count == 1 ? "" : "s")} in Downloads {(installers.Count == 1 ? "is" : "are")} " +
                               $"older than a month ({Mb(size)}): {names}. Move them to the Recycle Bin? They can be restored from it until it's emptied.")
        {
            Options = [new ActionOption($"Move to the Recycle Bin ({Mb(size)})", new CleanUp(CleanUpKind.OldInstallers)), new ActionOption("Cancel", null)],
        };
    }

    /// <summary>Temporary files are deleted for good (they're leftovers); old installers go to the Recycle Bin.</summary>
    public static async Task<ActionReply> CleanAsync(CleanUp cleanUp, CancellationToken ct)
    {
        if (cleanUp.Kind == CleanUpKind.TempFiles)
        {
            (long freed, int skipped) = await Task.Run(() => DeleteTempFiles(), ct);
            DiagnosticLog.Info($"Deleted temporary files from the check-up ({freed >> 20} MB)");
            return new ActionReply($"Deleted {Mb(freed)} of temporary files." +
                                   (skipped > 0 ? $" {skipped:N0} were still in use and stayed; they can go another time." : ""));
        }

        List<FileInfo> installers = await Task.Run(OldInstallers, ct);
        if (installers.Count == 0) return new ActionReply("There are no old installers in Downloads anymore.");
        var moved = new List<FileInfo>();
        foreach (FileInfo file in installers)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.FullName,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                moved.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                DiagnosticLog.Error("Couldn't move an old installer to the Recycle Bin", ex);
            }
        }
        DiagnosticLog.Info($"Moved {moved.Count} old installers to the Recycle Bin from the check-up");
        string names = string.Join(", ", moved.Take(6).Select(file => file.Name)) + (moved.Count > 6 ? $" and {moved.Count - 6} more" : "");
        return new ActionReply($"Moved {moved.Count} old installer{(moved.Count == 1 ? "" : "s")} ({Mb(moved.Sum(file => file.Length))}) " +
                               $"to the Recycle Bin: {names}. They still take space until the Recycle Bin is emptied, and can be restored from it until then.")
        {
            Options = [new ActionOption("Empty the Recycle Bin", new EmptyRecycleBin(false))],
        };
    }

    // Installers and disk images in Downloads (not in folders inside it) not changed for a month.
    static List<FileInfo> OldInstallers()
    {
        string? downloads = WindowsActions.DownloadsFolder;
        if (downloads is null || !Directory.Exists(downloads)) return [];
        DateTime cutoff = DateTime.Now - OldInstaller;
        try
        {
            return new DirectoryInfo(downloads).EnumerateFiles()
                .Where(file => InstallerExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)
                               && file.LastWriteTime < cutoff && file.CreationTime < cutoff)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // The user's temporary folder, files not touched for a day (anything newer may belong to an app that's running).
    static List<FileInfo> OldTempFiles()
    {
        string temp = Path.GetTempPath();
        DateTime cutoff = DateTime.Now - OldTemp;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return new DirectoryInfo(temp).EnumerateFiles("*", options)
                .Where(file => file.LastWriteTime < cutoff && file.LastAccessTime < cutoff)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static (long Freed, int Skipped) DeleteTempFiles()
    {
        long freed = 0;
        int skipped = 0;
        foreach (FileInfo file in OldTempFiles())
        {
            try
            {
                long size = file.Length;
                file.Delete();
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        // Folders left empty by that, deepest first. The temporary folder itself stays.
        string temp = Path.GetTempPath();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string folder in Directory.EnumerateDirectories(temp, "*", options).OrderByDescending(folder => folder.Length).ToList())
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use: it stays.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't list it: the files are gone anyway.
        }
        return (freed, skipped);
    }

    static long FolderBytes(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    static long RecycleBinBytes()
    {
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.Bytes : 0;
    }

    static DriveInfo? SystemDrive()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            return drive.IsReady ? drive : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }

    // ---- Sizes and Windows calls --------------------------------------------------------------------------------

    static string Gb(long bytes) => bytes >= 10L << 30 ? $"{bytes / (double)(1L << 30):0} GB" : $"{bytes / (double)(1L << 30):0.0} GB";

    static string Mb(long bytes) => bytes >= 1L << 30 ? Gb(bytes) : $"{Math.Max(1, bytes >> 20):N0} MB";

    readonly record struct Memory(uint LoadPercent, long TotalBytes, long AvailableBytes)
    {
        public long UsedBytes => TotalBytes - AvailableBytes;

        public static Memory Now()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status)
                ? new Memory(status.MemoryLoad, (long)status.TotalPhys, (long)status.AvailPhys)
                : new Memory(0, 0, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    // SHQUERYRBINFO, with the natural 8-byte packing of 64-bit Windows.
    [StructLayout(LayoutKind.Sequential)]
    struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    const uint WmClose = 0x0010;
    const uint GwOwner = 4;

    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? rootPath, ref RecycleBinInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
