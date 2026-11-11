using Assistant.Core.Search;

namespace Assistant.Core.Actions;

/// <summary>Something the user asked the assistant to do on the PC ("open Spotify", "volume to 30", "lock my PC").</summary>
public abstract record ActionIntent;

/// <summary>What "open X" should look for first, from words like "website", "folder" or "app".</summary>
public enum OpenKind { Any, App, Folder, Site }

/// <summary>"open Spotify", "open Downloads", "open youtube.com": an app, a folder or a website, whichever matches.</summary>
public sealed record OpenTarget(string Name, OpenKind Kind) : ActionIntent;

/// <summary>A specific app, chosen from several that matched.</summary>
public sealed record LaunchApp(string Name, string Id) : ActionIntent;

public sealed record WebSearch(string Query) : ActionIntent;

public enum VolumeOp { Up, Down, Set, Mute, Unmute, Query }

public sealed record VolumeChange(VolumeOp Op, int Percent = 0) : ActionIntent;

public enum MediaOp { PlayPause, Next, Previous }

public sealed record MediaCommand(MediaOp Op) : ActionIntent;

/// <summary>A page of Windows Settings, by its ms-settings: link.</summary>
public sealed record OpenSettings(string Page, string Link) : ActionIntent;

public sealed record LockPc : ActionIntent;

public sealed record SetTheme(bool Dark) : ActionIntent;

public sealed record TakeScreenshot : ActionIntent;

/// <summary>Asks first; Confirmed is set by the "Empty the Recycle Bin" button.</summary>
public sealed record EmptyRecycleBin(bool Confirmed) : ActionIntent;

/// <summary>
/// "Why is my PC slow?" (Speed), "my disk is full" (Space) or "my internet isn't working" (Internet): a check-up of this
/// PC, with fixes as buttons.
/// </summary>
public enum CheckUpKind { Speed, Space, Internet }

/// <summary>Empties Windows' list of remembered website addresses (the DNS cache), like "ipconfig /flushdns".</summary>
public sealed record FlushDns : ActionIntent;

/// <summary>
/// Measures the download and upload speed with Cloudflare's speed test ("speed test", or the check-up's button).
/// </summary>
public sealed record SpeedTest : ActionIntent;

/// <summary>A web page by its address, such as a café's Wi-Fi login page.</summary>
public sealed record OpenWebPage(string Url) : ActionIntent;

public sealed record PcCheckUp(CheckUpKind Kind) : ActionIntent;

/// <summary>Closes an app's windows the polite way, like clicking X on each (it may still ask to save).</summary>
public sealed record CloseApp(string Name, string ProcessName) : ActionIntent;

/// <summary>
/// Turns an app's start with Windows off or back on, the way Task Manager does (nothing is deleted).
/// InStartupFolder: a shortcut in the Startup folder rather than a Run entry.
/// </summary>
public sealed record StartupApp(string Name, string Entry, bool InStartupFolder, bool Enable) : ActionIntent;

public enum CleanUpKind { TempFiles, OldInstallers }

/// <summary>
/// Temporary files are deleted; old installers in Downloads go to the Recycle Bin. Asked for in the chat ("delete my temp
/// files") it says how much first, and its button (Confirmed) does it.
/// </summary>
public sealed record CleanUp(CleanUpKind Kind, bool Confirmed = true) : ActionIntent;

/// <summary>
/// "Make a backup of my CV on the desktop", "copy report.docx to the USB": find the file, copy it into that folder.
/// The runner passes (null) when it doesn't know the place, so other help can answer.
/// </summary>
public sealed record CopyRequest(string What, string Where) : ActionIntent;

/// <summary>From the buttons when several files matched: copy this one into that folder.</summary>
public sealed record CopyFile(string Source, string Folder) : ActionIntent;

/// <summary>Backups (the app's Backups class handles all of these).</summary>
public abstract record BackupIntent : ActionIntent;

/// <summary>"Back up my files": starts a backup in the background, or offers to set one up.</summary>
public sealed record BackupNow : BackupIntent;

/// <summary>"When was my last backup?"</summary>
public sealed record BackupStatus : BackupIntent;

/// <summary>"Set up backups": Settings > Backups.</summary>
public sealed record SetUpBackups : BackupIntent;

/// <summary>From the set-up buttons: back up to this drive or folder from now on, starting now.</summary>
public sealed record SetBackupDestination(string Path) : BackupIntent;

/// <summary>"Also back up D:\Projects".</summary>
public sealed record AddBackupFolder(string Path) : BackupIntent;

/// <summary>Opens this PC's folder in the backup, where the copies can be taken back.</summary>
public sealed record OpenBackup : BackupIntent;

/// <summary>A button under the reply: run this intent, or (null) cancel.</summary>
public sealed record ActionOption(string Label, ActionIntent? Intent);

/// <summary>What an action did, in words; file cards (a screenshot) and buttons (choices, confirmations) under it.</summary>
public sealed record ActionReply(string Text)
{
    public IReadOnlyList<FileHit> Files { get; init; } = [];
    public IReadOnlyList<ActionOption> Options { get; init; } = [];
}

/// <summary>Carries out actions; implemented by the app, which has the windows and Windows itself at hand.</summary>
public interface IActionRunner
{
    /// <summary>Null when it isn't something it can do (for example "open my CV", which file search answers instead).</summary>
    Task<ActionReply?> RunAsync(ActionIntent intent, CancellationToken ct);
}
