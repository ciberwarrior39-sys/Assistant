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
