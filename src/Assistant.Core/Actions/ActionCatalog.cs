using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core.Agenda;
using Assistant.Core.Guide;
using Assistant.Core.Mail;
using Assistant.Core.Llm;

namespace Assistant.Core.Actions;

/// <summary>One thing the app can do, as cloud chat sees it: a name, what it's for, its parameters, and how they become an intent.</summary>
public sealed record CatalogAction(string Name, string Description, JsonObject Parameters, Func<JsonElement, ActionIntent?> Build);

/// <summary>
/// The list of actions cloud chat is told about, so that "it's too quiet in here" can come back as "volume up" even
/// though no rule in ActionParser knows the phrase. What comes back is only ever an offer: it is checked here, turned
/// into an intent, and shown as a button with a label written here (never the model's words). Nothing runs until the
/// user presses it, and what it did is never sent back.
/// Left out on purpose: anything that takes a path or a process (backup folders, closing apps, startup entries) or a
/// full web address, because those would be the model's guesses about this computer.
/// </summary>
public static class ActionCatalog
{
    /// <summary>Buttons shown under one reply at most.</summary>
    public const int MaxOffers = 3;

    const int MaxText = 80; // an app name or search words, not an essay

    public static IReadOnlyList<CatalogAction> All { get; } =
    [
        new("open", "Open an app, a folder or a website by name (Spotify, Downloads, youtube).",
            Schema(["name"], Text("name", "What to open"), Choice("kind", "Only when the user said which", "app", "folder", "site")),
            arguments => Words(arguments, "name") is { } name
                ? new OpenTarget(name, Word(arguments, "kind") switch
                {
                    "app" => OpenKind.App,
                    "folder" => OpenKind.Folder,
                    "site" => OpenKind.Site,
                    _ => OpenKind.Any,
                })
                : null),

        new("web_search", "Search the web in the user's browser.",
            Schema(["query"], Text("query", "The search words")),
            arguments => Words(arguments, "query") is { } query ? new WebSearch(query) : null),

        new("volume", "Change the PC's sound volume (too quiet: up, too loud: down).",
            Schema(["change"], Choice("change", "What to do", "up", "down", "set", "mute", "unmute"),
                   new JsonObject { ["type"] = "integer", ["description"] = "0 to 100, with set" }.Named("percent")),
            arguments => Word(arguments, "change") switch
            {
                "up" => new VolumeChange(VolumeOp.Up),
                "down" => new VolumeChange(VolumeOp.Down),
                "mute" => new VolumeChange(VolumeOp.Mute),
                "unmute" => new VolumeChange(VolumeOp.Unmute),
                "set" when Number(arguments, "percent") is int percent && percent is >= 0 and <= 100 => new VolumeChange(VolumeOp.Set, percent),
                _ => null,
            }),

        new("media", "Control the music or video that is playing.",
            Schema(["control"], Choice("control", "What to do", "play_pause", "next", "previous")),
            arguments => Word(arguments, "control") switch
            {
                "play_pause" => new MediaCommand(MediaOp.PlayPause),
                "next" => new MediaCommand(MediaOp.Next),
                "previous" => new MediaCommand(MediaOp.Previous),
                _ => null,
            }),

        new("open_settings", "Open a page of Windows Settings.",
            Schema(["page"], Text("page", "One or two words: bluetooth, wi-fi, display, sound, storage, apps, update, mouse, keyboard, language, time, background, privacy, accounts, printers, battery, notifications")),
            arguments => Words(arguments, "page") is { } page ? ActionParser.SettingsPage(page) : null),

        new("show_where", "Show the user where a button, menu or option is in the app window they are using, by putting a ring around it. " +
                          "For \"where is…\", \"I can't find…\", \"point at…\" about something in an app. Never for opening the app.",
            Schema(["what"], Text("what", "The control's name or what it does, in a few words: bold, sum, save as, font size")),
            arguments => Words(arguments, "what") is { } what ? new GuideShow(what) : null),

        new("check_mail", "Look in the user's email inbox: unread messages, or messages from someone. Use this, not open, for anything about their email.",
            Schema([], Text("from", "Only messages from this person or company, when the user named one")),
            arguments => new CheckMail(Words(arguments, "from") ?? "")),

        new("check_calendar", "Look at the user's calendar: what's on a day, or the next event. Use this, not open, for their schedule.",
            Schema(["when"], Choice("when", "Which days", "today", "tomorrow", "this week", "next week", "next")),
            arguments => Word(arguments, "when") is { Length: > 0 } when ? new CheckCalendar(when) : null),

        new("lock_pc", "Lock the PC.", Schema([]), _ => new LockPc()),

        // Asks again before anything happens.
        new("power", "Shut down or restart the PC.",
            Schema(["what"], Choice("what", "What to do", "shut_down", "restart")),
            arguments => Word(arguments, "what") switch
            {
                "shut_down" => new PowerOff(Restart: false, Confirmed: false),
                "restart" => new PowerOff(Restart: true, Confirmed: false),
                _ => null,
            }),

        new("close_app", "Close an app that is open, by name (it can still ask to save).",
            Schema(["name"], Text("name", "The app's name")),
            arguments => Words(arguments, "name") is { } name ? new CloseNamed(name) : null),

        new("battery", "Show the battery level and whether it is charging.", Schema([]), _ => new BatteryLevel()),

        new("set_theme", "Switch Windows to dark or light mode.",
            Schema(["mode"], Choice("mode", "other when the user didn't say which", "dark", "light", "other")),
            arguments => Word(arguments, "mode") switch
            {
                "dark" => (ActionIntent?)new SetTheme(true),
                "light" => new SetTheme(false),
                "other" => new SwitchTheme(),
                _ => null,
            }),

        new("take_screenshot", "Take a screenshot and save it.", Schema([]), _ => new TakeScreenshot()),

        new("check_pc", "Check this PC and suggest fixes: why it is slow, what fills the disk, or why the internet isn't working.",
            Schema(["about"], Choice("about", "What to check", "speed", "space", "internet")),
            arguments => Word(arguments, "about") switch
            {
                "speed" => new PcCheckUp(CheckUpKind.Speed),
                "space" => new PcCheckUp(CheckUpKind.Space),
                "internet" => new PcCheckUp(CheckUpKind.Internet),
                _ => null,
            }),

        new("speed_test", "Measure the internet's download and upload speed.", Schema([]), _ => new SpeedTest()),

        // These two and the Recycle Bin say how much would go and ask again before anything is removed.
        new("clean_up", "Free disk space: delete temporary files, or move old installers in Downloads to the Recycle Bin.",
            Schema(["what"], Choice("what", "What to clean", "temporary_files", "old_installers")),
            arguments => Word(arguments, "what") switch
            {
                "temporary_files" => new CleanUp(CleanUpKind.TempFiles, Confirmed: false),
                "old_installers" => new CleanUp(CleanUpKind.OldInstallers, Confirmed: false),
                _ => null,
            }),

        new("empty_recycle_bin", "Empty the Recycle Bin.", Schema([]), _ => new EmptyRecycleBin(false)),

        new("backup", "The user's file backups: start one now, show when the last one ran, or open the backup settings.",
            Schema(["what"], Choice("what", "What to do", "start", "status", "settings")),
            arguments => Word(arguments, "what") switch
            {
                "start" => (ActionIntent?)new BackupNow(),
                "status" => new BackupStatus(),
                "settings" => new SetUpBackups(),
                _ => null,
            }),

        new("copy_file", "Copy one of the user's files to a folder or drive (the app finds the file by name).",
            Schema(["file", "to"], Text("file", "The file's name or what it is, for example CV"), Text("to", "desktop, documents, downloads, usb, or a drive letter")),
            arguments => Words(arguments, "file") is { } file && Words(arguments, "to") is { } to ? new CopyRequest(file, to) : null),
    ];

    /// <summary>The same list in the shape a chat request carries.</summary>
    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
        All.Select(action => new ToolDefinition(action.Name, action.Description, action.Parameters)).ToList();

    /// <summary>What a call from the model means, or null: an unknown name, or parameters that don't fit.</summary>
    public static ActionIntent? FromCall(ToolCall call)
    {
        CatalogAction? action = All.FirstOrDefault(item => item.Name == call.Name);
        if (action is null || call.Arguments.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return action.Build(call.Arguments);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return null; // parameters of the wrong kind
        }
    }

    /// <summary>The buttons for a reply: each valid call once, in the model's order, at most MaxOffers.</summary>
    public static IReadOnlyList<ActionOption> Offers(IReadOnlyList<ToolCall> calls) =>
        calls.Select(FromCall)
             .OfType<ActionIntent>()
             .Distinct()
             .Take(MaxOffers)
             .Select(intent => new ActionOption(Label(intent), intent))
             .ToList();

    /// <summary>What the button says. Written here so the user reads exactly what pressing it does.</summary>
    public static string Label(ActionIntent intent) => intent switch
    {
        OpenTarget open => $"Open {open.Name}",
        WebSearch search => $"Search the web for \"{search.Query}\"",
        VolumeChange { Op: VolumeOp.Up } => "Volume up",
        VolumeChange { Op: VolumeOp.Down } => "Volume down",
        VolumeChange { Op: VolumeOp.Mute } => "Mute",
        VolumeChange { Op: VolumeOp.Unmute } => "Unmute",
        VolumeChange { Op: VolumeOp.Set } volume => $"Set the volume to {volume.Percent}%",
        MediaCommand { Op: MediaOp.PlayPause } => "Play / pause",
        MediaCommand { Op: MediaOp.Next } => "Next track",
        MediaCommand { Op: MediaOp.Previous } => "Previous track",
        OpenSettings settings => $"Open {settings.Page} settings",
        GuideShow show => $"Show me where \"{show.What}\" is",
        CheckMail { From.Length: > 0 } mail => $"Check emails from {mail.From}",
        CheckMail => "Check my email",
        CheckCalendar { Day: "next" } => "Show my next event",
        CheckCalendar calendar => $"Show my calendar for {calendar.Day}",
        LockPc => "Lock the PC",
        PowerOff { Restart: true } => "Restart the PC…",
        PowerOff => "Shut down the PC…",
        CloseNamed close => $"Close {close.Name}",
        BatteryLevel => "Check the battery",
        SetTheme theme => theme.Dark ? "Switch to dark mode" : "Switch to light mode",
        SwitchTheme => "Switch between dark and light mode",
        TakeScreenshot => "Take a screenshot",
        PcCheckUp { Kind: CheckUpKind.Speed } => "Check why the PC is slow",
        PcCheckUp { Kind: CheckUpKind.Space } => "Check what fills the disk",
        PcCheckUp { Kind: CheckUpKind.Internet } => "Check the internet",
        SpeedTest => "Run a speed test",
        CleanUp { Kind: CleanUpKind.TempFiles } => "Temporary files: see how much",
        CleanUp { Kind: CleanUpKind.OldInstallers } => "Old installers: see how much",
        EmptyRecycleBin => "Recycle Bin: see how much",
        BackupNow => "Back up now",
        BackupStatus => "Show the backup status",
        SetUpBackups => "Backup settings",
        CopyRequest copy => $"Copy {copy.What} to {copy.Where}",
        _ => "Do it",
    };

    // ---- Reading parameters -----------------------------------------------------------------------------------------

    // Free text from the model: one line, trimmed, not long. Null when missing or empty.
    static string? Words(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String) return null;
        string text = string.Join(' ', (value.GetString() ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = new string(text.Where(letter => !char.IsControl(letter)).ToArray());
        if (text.Length > MaxText) text = text[..MaxText].TrimEnd();
        return text.Length == 0 ? null : text;
    }

    // One of a fixed set of words.
    static string Word(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim().ToLowerInvariant()
            : "";

    // A whole number, also when it arrives as "30".
    static int? Number(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse((value.GetString() ?? "").Trim().TrimEnd('%'), out int typed)) return typed;
        return null;
    }

    // ---- Describing parameters (JSON Schema) ------------------------------------------------------------------------

    static JsonObject Schema(string[] required, params (string Name, JsonObject Shape)[] properties)
    {
        var shapes = new JsonObject();
        foreach ((string name, JsonObject shape) in properties) shapes[name] = shape;
        var names = new JsonArray();
        foreach (string name in required) names.Add(name);
        return new JsonObject { ["type"] = "object", ["properties"] = shapes, ["required"] = names };
    }

    static (string Name, JsonObject Shape) Text(string name, string description) =>
        (name, new JsonObject { ["type"] = "string", ["description"] = description });

    static (string Name, JsonObject Shape) Choice(string name, string description, params string[] values)
    {
        var allowed = new JsonArray();
        foreach (string value in values) allowed.Add(value);
        return (name, new JsonObject { ["type"] = "string", ["description"] = description, ["enum"] = allowed });
    }

    static (string Name, JsonObject Shape) Named(this JsonObject shape, string name) => (name, shape);
}
