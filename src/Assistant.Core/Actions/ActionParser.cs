using System.Globalization;
using System.Text.RegularExpressions;

namespace Assistant.Core.Actions;

/// <summary>
/// Reads requests to do something on the PC, in English and Bulgarian. Strict patterns: ordinary chat isn't caught,
/// and "open" only becomes an action when the runner finds what to open (otherwise file search gets the message).
/// </summary>
public static partial class ActionParser
{
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Windows Settings pages people ask for, by the words they use. Checked longest first.
    /// <summary>The link that stands for the assistant's own Settings window.</summary>
    public const string AssistantSettings = "assistant:settings";

    static readonly (string[] Words, string Page, string Link)[] SettingsPages =
    [
        (["windows", "pc", "computer", "system", "компютъра", "уиндоус", "системата"], "Windows Settings", "ms-settings:"),
        (["bluetooth", "блутут", "блутуут"], "Bluetooth", "ms-settings:bluetooth"),
        (["wi-fi", "wifi", "wireless", "уай-фай", "уайфай", "безжична мрежа"], "Wi-Fi", "ms-settings:network-wifi"),
        (["network", "internet", "мрежа", "мрежата", "интернет"], "Network", "ms-settings:network-status"),
        (["vpn"], "VPN", "ms-settings:network-vpn"),
        (["display", "screen", "resolution", "дисплей", "дисплея", "екран", "екрана", "резолюция"], "Display", "ms-settings:display"),
        (["night light", "нощна светлина"], "Night light", "ms-settings:nightlight"),
        (["sound", "audio", "speakers", "microphone", "звук", "звука", "микрофон", "микрофона"], "Sound", "ms-settings:sound"),
        (["notifications", "notification", "известия", "известията"], "Notifications", "ms-settings:notifications"),
        (["battery", "power", "sleep", "батерия", "батерията", "захранване", "заспиване"], "Power & battery", "ms-settings:powersleep"),
        (["storage", "disk space", "памет", "хранилище", "място"], "Storage", "ms-settings:storagesense"),
        (["apps", "installed apps", "programs", "приложения", "приложенията", "програми"], "Installed apps", "ms-settings:appsfeatures"),
        (["default apps", "приложения по подразбиране"], "Default apps", "ms-settings:defaultapps"),
        (["startup", "startup apps", "стартиране"], "Startup apps", "ms-settings:startupapps"),
        (["update", "updates", "windows update", "актуализации", "актуализациите", "обновления"], "Windows Update", "ms-settings:windowsupdate"),
        (["mouse", "touchpad", "мишка", "мишката", "тъчпад"], "Mouse", "ms-settings:mousetouchpad"),
        (["keyboard", "typing", "клавиатура", "клавиатурата"], "Typing", "ms-settings:typing"),
        (["language", "region", "език", "езика", "регион"], "Language & region", "ms-settings:regionlanguage"),
        (["date", "time", "clock", "дата", "час", "часовник"], "Date & time", "ms-settings:dateandtime"),
        (["background", "wallpaper", "фон", "фона", "тапет"], "Background", "ms-settings:personalization-background"),
        (["colors", "colours", "theme", "themes", "dark mode", "цветове", "тема", "теми"], "Colors", "ms-settings:colors"),
        (["privacy", "поверителност"], "Privacy & security", "ms-settings:privacy"),
        (["accounts", "account", "акаунт", "акаунти"], "Accounts", "ms-settings:yourinfo"),
        (["printers", "printer", "принтер", "принтери"], "Printers", "ms-settings:printers"),
        (["gaming", "game mode", "игри", "режим за игри"], "Game mode", "ms-settings:gaming-gamemode"),
        (["focus", "do not disturb", "не безпокойте", "фокус"], "Notifications", "ms-settings:notifications"),
    ];

    public static ActionIntent? Parse(string message)
    {
        string text = Courtesy(Spaces().Replace(message.Trim(), " "));
        if (text.Length == 0 || text.Length > 200) return null;

        if (Volume(text) is { } volume) return volume;
        if (Media(text) is { } media) return media;
        if (First(text, Lock)) return new LockPc();
        if (First(text, DarkMode)) return new SetTheme(true);
        if (First(text, LightMode)) return new SetTheme(false);
        if (First(text, Screenshot)) return new TakeScreenshot();
        if (First(text, EmptyBin)) return new EmptyRecycleBin(false);
        if (Settings(text) is { } settings) return settings;
        if (Find(text, Search) is { } search) return new WebSearch(search.Groups["q"].Value.Trim());
        if (Find(text, Open) is { } open) return Target(open.Groups["x"].Value);
        return null;
    }

    // Bulgarian "can you…" turns the verb into its "you" form ("можеш ли да спреш", "би ли спрял"); back to the command form ("спри").
    static readonly Dictionary<string, string> YouForm = new(StringComparer.OrdinalIgnoreCase)
    {
        ["спреш"] = "спри", ["пуснеш"] = "пусни", ["продължиш"] = "продължи", ["увеличиш"] = "увеличи", ["намалиш"] = "намали",
        ["усилиш"] = "усили", ["заглушиш"] = "заглуши", ["отвориш"] = "отвори", ["стартираш"] = "стартирай", ["заключиш"] = "заключи",
        ["изпразниш"] = "изпразни", ["изчистиш"] = "изчисти", ["направиш"] = "направи", ["потърсиш"] = "потърси", ["сложиш"] = "сложи",
        ["включиш"] = "включи", ["изключиш"] = "изключи", ["прескочиш"] = "прескочи", ["върнеш"] = "върни", ["смениш"] = "смени",
        ["покажеш"] = "покажи",
        // "би ли пуснал/пуснала …"
        ["пуснал"] = "пусни", ["пуснала"] = "пусни",
        ["спрял"] = "спри", ["спряла"] = "спри",
        ["продължил"] = "продължи", ["продължила"] = "продължи",
        ["увеличил"] = "увеличи", ["увеличила"] = "увеличи",
        ["намалил"] = "намали", ["намалила"] = "намали",
        ["усилил"] = "усили", ["усилила"] = "усили",
        ["заглушил"] = "заглуши", ["заглушила"] = "заглуши",
        ["отворил"] = "отвори", ["отворила"] = "отвори",
        ["стартирал"] = "стартирай", ["стартирала"] = "стартирай",
        ["заключил"] = "заключи", ["заключила"] = "заключи",
        ["изпразнил"] = "изпразни", ["изпразнила"] = "изпразни",
        ["изчистил"] = "изчисти", ["изчистила"] = "изчисти",
        ["направил"] = "направи", ["направила"] = "направи",
        ["потърсил"] = "потърси", ["потърсила"] = "потърси",
        ["сложил"] = "сложи", ["сложила"] = "сложи",
        ["включил"] = "включи", ["включила"] = "включи",
        ["изключил"] = "изключи", ["изключила"] = "изключи",
        ["прескочил"] = "прескочи", ["прескочила"] = "прескочи",
        ["върнал"] = "върни", ["върнала"] = "върни",
        ["сменил"] = "смени", ["сменила"] = "смени",
        ["показал"] = "покажи", ["показала"] = "покажи",
    };

    /// <summary>
    /// Drops the polite parts around a request, so "hey, can you pause the music please?" reads as "pause the music",
    /// and "можеш ли да спреш музиката, моля" as "спри музиката".
    /// </summary>
    public static string Courtesy(string text)
    {
        text = text.Trim().TrimEnd('.', '!', '?', ' ');
        string before;
        do
        {
            before = text;
            text = LeadingCourtesy().Replace(text, "").Trim();
            text = TrailingCourtesy().Replace(text, "").Trim().TrimEnd('.', '!', '?', ',', ' ');
        }
        while (text != before);

        int space = text.IndexOf(' ');
        string first = space < 0 ? text : text[..space];
        return YouForm.TryGetValue(first, out string? command) ? command + (space < 0 ? "" : text[space..]) : text;
    }

    static ActionIntent? Volume(string text)
    {
        if (Find(text, VolumeSet) is { } set)
        {
            int percent = int.Parse(set.Groups["n"].Value, CultureInfo.InvariantCulture);
            return percent is >= 0 and <= 100 ? new VolumeChange(VolumeOp.Set, percent) : null;
        }
        if (First(text, VolumeUp)) return new VolumeChange(VolumeOp.Up);
        if (First(text, VolumeDown)) return new VolumeChange(VolumeOp.Down);
        if (First(text, Mute)) return new VolumeChange(VolumeOp.Mute);
        if (First(text, Unmute)) return new VolumeChange(VolumeOp.Unmute);
        if (First(text, VolumeQuery)) return new VolumeChange(VolumeOp.Query);
        return null;
    }

    static ActionIntent? Media(string text) =>
        First(text, PlayPause) ? new MediaCommand(MediaOp.PlayPause)
        : First(text, NextTrack) ? new MediaCommand(MediaOp.Next)
        : First(text, PreviousTrack) ? new MediaCommand(MediaOp.Previous)
        : null;

    // "open bluetooth settings", "bluetooth settings", "settings", "отвори настройките за блутут"
    static ActionIntent? Settings(string text)
    {
        if (Find(text, SettingsRequest) is not { } match) return null;
        string page = match.Groups["p"].Value.Trim().ToLowerInvariant();
        // "open settings" on its own means the assistant's own Settings; "Windows settings" means Windows'.
        if (page.Length == 0) return new OpenSettings("Assistant", AssistantSettings);
        foreach (var entry in SettingsPages.OrderByDescending(entry => entry.Words.Max(word => word.Length)))
        {
            if (entry.Words.Any(word => page == word || page.Contains(word, StringComparison.Ordinal)))
                return new OpenSettings(entry.Page, entry.Link);
        }
        return null;
    }

    // "the Spotify app" → (Spotify, App); "youtube website" → (youtube, Site); "my Downloads folder" → (Downloads, Folder)
    static OpenTarget? Target(string what)
    {
        string name = what.Trim().TrimEnd('?');
        OpenKind kind = OpenKind.Any;
        Match hint = KindWord().Match(name);
        if (hint.Success)
        {
            string word = hint.Groups["k"].Value.ToLowerInvariant();
            kind = word is "app" or "application" or "program" or "приложението" or "програмата" ? OpenKind.App
                 : word is "folder" or "папката" ? OpenKind.Folder
                 : OpenKind.Site;
            name = name.Remove(hint.Index, hint.Length);
        }
        name = Leading().Replace(name, "").Trim();
        return name.Length == 0 ? null : new OpenTarget(name, kind);
    }

    static bool First(string text, Regex[] patterns) => patterns.Any(pattern => pattern.IsMatch(text));

    static Match? Find(string text, Regex[] patterns)
    {
        foreach (Regex pattern in patterns)
        {
            Match match = pattern.Match(text);
            if (match.Success) return match;
        }
        return null;
    }

    // ---- Phrases -------------------------------------------------------------------------------------------------

    const string Polite = @"(?:(?:please|can\s+you|could\s+you)\s+)?";

    static readonly Regex[] VolumeSet =
    [
        new($@"^{Polite}(?:set\s+|turn\s+)?(?:the\s+)?(?:volume|sound)\s+(?:to\s+|at\s+)?(?<n>\d{{1,3}})\s*(?:%|percent)?$", Options),
        new(@"^(?:сложи\s+|направи\s+)?(?:звука|звукът|силата\s+на\s+звука)\s+(?:на\s+)?(?<n>\d{1,3})\s*%?$", Options),
    ];

    static readonly Regex[] VolumeUp =
    [
        new($@"^{Polite}(?:turn\s+(?:the\s+)?(?:volume|sound|it|music)\s+up|turn\s+up\s+(?:the\s+)?(?:volume|sound|music)|volume\s+up|louder|(?:increase|raise)\s+(?:the\s+)?(?:volume|sound))$", Options),
        new(@"^(?:увеличи|усили)(?:\s+малко)?(?:\s+(?:звука|музиката))?$|^(?:пусни\s+)?по-силно$", Options),
    ];

    static readonly Regex[] VolumeDown =
    [
        new($@"^{Polite}(?:turn\s+(?:the\s+)?(?:volume|sound|it|music)\s+down|turn\s+down\s+(?:the\s+)?(?:volume|sound|music)|volume\s+down|quieter|(?:decrease|lower)\s+(?:the\s+)?(?:volume|sound))$", Options),
        new(@"^намали(?:\s+малко)?(?:\s+(?:звука|музиката))?$|^(?:пусни\s+)?по-тихо$", Options),
    ];

    static readonly Regex[] Mute =
    [
        new($@"^{Polite}(?:mute|silence)(?:\s+(?:the\s+)?(?:sound|volume|pc|computer|audio))?$", Options),
        new(@"^(?:заглуши|спри\s+звука|изключи\s+звука)$", Options),
    ];

    static readonly Regex[] Unmute =
    [
        new($@"^{Polite}unmute(?:\s+(?:the\s+)?(?:sound|volume|pc|computer|audio))?$", Options),
        new(@"^(?:пусни|включи)\s+звука$", Options),
    ];

    static readonly Regex[] VolumeQuery =
    [
        new(@"^what(?:'s|\s+is)\s+(?:the\s+)?volume(?:\s+at)?\??$", Options),
        new(@"^колко\s+е\s+(?:звукът|звука)\??$", Options),
    ];

    static readonly Regex[] PlayPause =
    [
        new(@"^(?:pause|resume|unpause|play|stop)(?:\s+playing)?(?:\s+(?:the\s+|my\s+|this\s+|some\s+)?(?:music|song|songs|video|track|playback|it|spotify|youtube|podcast|audio))?(?:\s+(?:on|in)\s+(?:spotify|youtube))?$", Options),
        new(@"^(?:continue|start)\s+(?:playing\s+)?(?:the\s+|my\s+)?(?:music|song|video|playback|spotify|podcast)$", Options),
        new(@"^(?:пауза|спри|пусни|продължи)(?:\s+(?:музиката|песента|видеото|спотифай|spotify|подкаста))?$", Options),
    ];

    static readonly Regex[] NextTrack =
    [
        new(@"^(?:next|skip)(?:\s+(?:the\s+|this\s+)?(?:song|track|video|one|it))?$", Options),
        new(@"^(?:play|put\s+on|go\s+to|skip\s+to)\s+(?:the\s+)?next\s+(?:song|track|one|video)$", Options),
        new(@"^(?:следваща(?:та)?(?:\s+песен)?|пусни\s+следващата(?:\s+песен)?|прескочи(?:\s+(?:песента|тази\s+песен))?|смени\s+песента)$", Options),
    ];

    static readonly Regex[] PreviousTrack =
    [
        new(@"^previous(?:\s+(?:song|track|video|one))?$|^go\s+back(?:\s+a)?\s+(?:song|track)$", Options),
        new(@"^(?:play|put\s+on|go\s+to|go\s+back\s+to)\s+(?:the\s+)?(?:previous|last)\s+(?:song|track|one|video)(?:\s+again)?$", Options),
        new(@"^(?:предишна(?:та)?(?:\s+песен)?|пусни\s+предишната(?:\s+песен)?|върни\s+(?:песента|предишната(?:\s+песен)?))$", Options),
    ];

    static readonly Regex[] Lock =
    [
        new($@"^{Polite}lock\s+(?:my\s+|the\s+)?(?:pc|computer|screen|laptop)$", Options),
        new(@"^заключи\s+(?:компютъра|екрана|лаптопа)$", Options),
    ];

    static readonly Regex[] DarkMode =
    [
        new($@"^{Polite}(?:(?:switch|change)\s+to|turn\s+on|use|enable|go)\s+(?:the\s+)?dark\s+(?:mode|theme)$|^dark\s+(?:mode|theme)(?:\s+on)?$", Options),
        new(@"^(?:(?:включи|пусни|сложи|смени\s+на)\s+)?(?:тъмен\s+режим|тъмна\s+тема|тъмния\s+режим|тъмната\s+тема)$", Options),
    ];

    static readonly Regex[] LightMode =
    [
        new($@"^{Polite}(?:(?:switch|change)\s+to|turn\s+on|use|enable|go)\s+(?:the\s+)?light\s+(?:mode|theme)$|^light\s+(?:mode|theme)(?:\s+on)?$", Options),
        new(@"^(?:(?:включи|пусни|сложи|смени\s+на)\s+)?(?:светъл\s+режим|светла\s+тема|светлия\s+режим|светлата\s+тема)$", Options),
    ];

    static readonly Regex[] Screenshot =
    [
        new($@"^{Polite}(?:take|make|grab|capture)\s+(?:a\s+)?(?:screenshot|screen\s*shot|screen\s+capture|picture\s+of\s+(?:the|my)\s+screen)(?:\s+of\s+(?:the|my)\s+screen)?$|^screenshot$", Options),
        new(@"^(?:направи\s+)?(?:скрийншот|снимка\s+на\s+екрана|екранна\s+снимка)$", Options),
    ];

    static readonly Regex[] EmptyBin =
    [
        new($@"^{Polite}(?:empty|clear|clean)\s+(?:out\s+)?(?:the\s+|my\s+)?(?:recycle\s+bin|bin|trash)$", Options),
        new(@"^(?:изпразни|изчисти)\s+кошчето$", Options),
    ];

    static readonly Regex[] SettingsRequest =
    [
        new($@"^{Polite}(?:(?:open|show|go\s+to)\s+)?(?:the\s+|my\s+)?(?:(?<p>.+?)\s+)?settings$", Options),
        new($@"^{Polite}(?:open|show)\s+(?:the\s+)?settings\s+(?:for|of)\s+(?<p>.+)$", Options),
        new(@"^(?:(?:отвори|покажи)\s+)?настройки(?:те)?(?:\s+(?:за|на)\s+(?<p>.+))?$", Options),
    ];

    static readonly Regex[] Search =
    [
        new($@"^{Polite}(?:search|look)\s+(?:on\s+)?(?:the\s+web|the\s+internet|online|google|on\s+google)\s+(?:for\s+)?(?<q>.+)$", Options),
        new($@"^{Polite}(?:search\s+for|search|look\s+up)\s+(?<q>.+?)\s+(?:online|on\s+the\s+(?:web|internet)|on\s+google)$", Options),
        new(@"^google\s+(?<q>.+)$", Options),
        new(@"^(?:потърси|търси)\s+(?:в\s+интернет|в\s+гугъл|в\s+google|онлайн)\s+(?:за\s+)?(?<q>.+)$", Options),
        new(@"^(?:потърси|търси)\s+(?<q>.+?)\s+(?:в\s+интернет|в\s+гугъл|в\s+google|онлайн)$", Options),
        new(@"^гугълни\s+(?<q>.+)$", Options),
    ];

    static readonly Regex[] Open =
    [
        new($@"^{Polite}(?:open|launch|start|run|go\s+to)\s+(?:up\s+)?(?<x>.+)$", Options),
        new(@"^(?:отвори|пусни|стартирай|влез\s+в)\s+(?<x>.+)$", Options),
    ];

    [GeneratedRegex(@"\s+(?<k>app|application|program|website|site|web\s+site|page|folder|приложението|програмата|сайта|страницата|папката)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KindWord();

    [GeneratedRegex(@"^(?:the|my|a|моята|моя|моите)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Leading();

    [GeneratedRegex(@"^(?:hey|hi|ok|okay|yo|please|pls|assistant|can\s+you|could\s+you|would\s+you|will\s+you|can\s+u|хей|ей|моля(?:\s+те)?|можеш\s+ли\s+да|ще\s+можеш\s+ли\s+да|би\s+ли(?:\s+(?:могъл|могла))?(?:\s+да)?)\b[\s,!]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingCourtesy();

    [GeneratedRegex(@"[\s,]+(?:please|pls|now|right\s+now|for\s+me|for\s+a\s+(?:sec|second|moment|bit|while)|a\s+(?:bit|little)|thanks|thank\s+you|моля|сега|ако\s+обичаш|благодаря|малко)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingCourtesy();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
