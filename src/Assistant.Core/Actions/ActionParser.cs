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

        if (Backup(text) is { } backup) return backup;
        if (Copy(text) is { } copy) return copy;
        if (First(text, TempFiles)) return new CleanUp(CleanUpKind.TempFiles, Confirmed: false);
        if (First(text, OldInstallers)) return new CleanUp(CleanUpKind.OldInstallers, Confirmed: false);
        if (CheckUp(text) is { } checkUp) return checkUp;
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
        ["изтриеш"] = "изтрий", ["махнеш"] = "махни", ["почистиш"] = "почисти", ["премахнеш"] = "премахни",
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

    // "why is my PC so slow?", "my disk is full", "компютърът ми е бавен": a check-up. Searched anywhere in the message
    // (people say it many ways), but only when the PC itself is meant: a slow internet or a slow website is something else.
    static ActionIntent? CheckUp(string text)
    {
        if (text.Length > 120) return null;
        // "speed test", "how fast is my internet": measured straight away. "My internet speed is so slow" is a complaint,
        // so it gets the whole check-up (which offers the speed test too).
        if (SpeedRequest().IsMatch(text) && !Complaint().IsMatch(text)) return new SpeedTest();
        if (InternetCheck().IsMatch(text)) return new PcCheckUp(CheckUpKind.Internet);
        if (NotThePc().IsMatch(text)) return null;
        if (SpaceCheck().IsMatch(text)) return new PcCheckUp(CheckUpKind.Space);
        if (SpeedCheck().IsMatch(text)) return new PcCheckUp(CheckUpKind.Speed);
        return null;
    }

    [GeneratedRegex("""
        \b(?:pc|computer|laptop|windows|system)\b.*\b(?:slow|slower|slowly|lags?|lagging|laggy|sluggish|freez\w*|hangs|hanging|stutter\w*)\b
        |\b(?:slow|lagging|laggy|sluggish)\s+(?:pc|computer|laptop)\b
        |\bspeed\s+up\s+(?:my\s+|the\s+|this\s+)?(?:pc|computer|laptop|windows)\b
        |\b(?:what|which\s+\w+)(?:'s|\s+is|\s+are)\s+(?:using|eating|hogging|taking)\s+(?:up\s+)?(?:all\s+)?(?:my\s+|the\s+)?(?:ram|memory|cpu|processor)\b
        |\b(?:pc|computer|laptop)\s+check-?\s?up\b|\bcheck-?\s?up\s+(?:of\s+)?(?:my\s+|the\s+)?(?:pc|computer|laptop)\b
        |\bcheck\s+(?:my\s+|the\s+)?(?:pc|computer|laptop)(?:'s)?(?:\s+(?:health|speed|performance))?$
        |\b(?:компютър|лаптоп|пц|уиндоус)\w*\b.*\b(?:бав\w*|забав\w*|лагва\w*|насича\w*|зависва\w*|блокира\w*|тормози\w*)
        |\b(?:бав\w*|забав\w*)\b.*\b(?:компютър|лаптоп|пц)\w*
        |\bускори\s+(?:ми\s+)?(?:компютъра|лаптопа|пц-то)
        |\bкакво\s+(?:натоварва|изяжда|ползва|използва|заема)\s+(?:ми\s+)?(?:рам|рам-а|процесора|компютъра)
        |\b(?:провери|прегледай)\s+(?:ми\s+)?(?:компютъра|лаптопа)
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex SpeedCheck();

    [GeneratedRegex("""
        \b(?:disk|drive|storage|hard\s+drive|ssd|hdd|c\s+drive|c:|space)\s+(?:is\s+)?(?:almost\s+|nearly\s+|completely\s+)?(?:full|running\s+out|running\s+low|low)\b
        |\b(?:running\s+out\s+of|out\s+of|low\s+on|no\s+more)\s+(?:disk\s+|drive\s+|storage\s+|free\s+)?(?:space|storage)\b
        |\b(?:free|clear|make)\s+(?:up\s+)?(?:some\s+)?(?:disk\s+|drive\s+|storage\s+)?(?:space|room|storage)\b
        |\b(?:what(?:'s|\s+is)|what\s+are)\s+(?:taking|using|eating|filling)\s+(?:up\s+)?(?:all\s+)?(?:the\s+|my\s+)?(?:space|storage|disk|drive)\b
        |\bclean\s*up\s+(?:my\s+|the\s+|this\s+)?(?:pc|computer|laptop|disk|drive|storage)\b|\bdisk\s+clean\s*up\b
        |\bhow\s+much\s+free\s+(?:disk\s+)?(?:space|storage)\b
        |\bhow\s+much\s+(?:disk\s+)?(?:space|storage)\s+(?:do\s+i\s+have|have\s+i\s+got|is\s+left|is\s+free|left)\b
        |\b(?:диск|паметта|хранилище)\w*\s+(?:ми\s+)?(?:е\s+)?(?:пъл(?:ен|на|но)|свършва|препълн\w*)
        |\bняма(?:м)?\s+(?:повече\s+)?(?:свободно\s+)?място\b
        |\b(?:освободи|направи)\s+(?:малко\s+|още\s+)?(?:свободно\s+)?място\b
        |\bпочисти\s+(?:ми\s+)?(?:компютъра|лаптопа|диска|паметта)
        |\bкакво\s+(?:заема|пълни|изяжда)\s+(?:ми\s+)?(?:мястото|място|диска|паметта)
        |\bколко\s+свободно\s+място\b
        |\bколко\s+място\s+(?:имам|има|остава|ми\s+остава|е\s+свободно)\b
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex SpaceCheck();

    // "my internet isn't working", "the Wi-Fi keeps dropping", "нямам интернет", "бавен интернет". "Connection" and "network"
    // only after "my"/"the", so a database connection or a neural network isn't caught.
    [GeneratedRegex("""
        \b(?:internet|wi-?fi|(?:my|the)\s+(?:internet\s+|wi-?fi\s+)?(?:connection|network))\b.*?
          \b(?:not\s+working|doesn['’]t\s+work|does\s+not\s+work|isn['’]t\s+working|is\s+not\s+working|stopped\s+working|down|slow|slower|so\s+slow
          |keeps\s+(?:dropping|disconnecting|cutting\s+out)|drops|dropping|disconnects|disconnecting|cuts\s+out|laggy|lagging|lags|broken|bad|weak|unstable
          |not\s+connecting|won['’]t\s+connect|can['’]t\s+connect|isn['’]t\s+connecting|gone|out)\b
        |\bno\s+(?:internet|wi-?fi)\b
        |\bcan['’]t\s+(?:connect\s+to|get\s+on(?:to)?|access|reach)\s+(?:the\s+)?(?:internet|wi-?fi|any\s+websites?|websites|online)\b
        |\b(?:websites?|pages|sites)\s+(?:won['’]t|don['’]t|aren['’]t|do\s+not|will\s+not)\s+(?:load|open|work)\b
        |\b(?:check|test|diagnose|fix|troubleshoot)\s+(?:my\s+|the\s+)?(?:internet|wi-?fi|connection|network)(?:\s+(?:speed|connection))?\b
        |\b(?:internet|wi-?fi|connection)\s+speed\b|\bspeed\s*test\b|\bhow\s+fast\s+is\s+my\s+(?:internet|wi-?fi|connection)\b
        |\b(?:интернет|уай-?фа|wi-?fi|вайфа|връзката|мрежата)\w*\b.*?(?:не\s+работи|не\s+върви|не\s+тръгва|бав\w*|спира|прекъсва|пада|го\s+няма|изчезна|изчезва|лагва\w*|слаб\w*)
        |\bнямам\s+(?:интернет|връзка|wi-?fi|уай-?фай|вайфай)\b|\bняма\s+(?:интернет|връзка\s+с\s+интернет)\b
        |\b(?:провери|тествай|оправи)\s+(?:ми\s+)?(?:интернета|връзката|скоростта\s+на\s+интернета|уай-?фая|вайфая)
        |\bбав(?:ен|ия|ият)\s+интернет|\bскоростта\s+на\s+интернета\b
        |\bсайтовете\s+не\s+(?:се\s+)?(?:зареждат|отварят|отваря)
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex InternetCheck();

    [GeneratedRegex("""
        \bspeed\s*-?\s*test\b
        |\b(?:test|check|measure|run)\s+(?:a\s+)?(?:my\s+|the\s+)?(?:internet|wi-?fi|connection|network|download|upload)\s+speed\b
        |\b(?:what's|what\s+is|show)\s+(?:my\s+|the\s+)?(?:internet|wi-?fi|connection|download|upload)\s+speed\b
        |\bhow\s+fast\s+is\s+(?:my\s+|the\s+)?(?:internet|wi-?fi|connection)\b
        |\b(?:тествай|провери|измери|покажи)\s+(?:ми\s+)?(?:скоростта|скорост)\b
        |\bскорост(?:та)?\s+на\s+(?:интернета|връзката|уай-?фая|вайфая)\b
        |\bколко\s+(?:е\s+)?бърз\s+(?:е\s+)?(?:ми\s+)?(?:интернетът|интернета)
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex SpeedRequest();

    [GeneratedRegex(@"\b(?:slow|slower|not\s+working|n['’]t\s+working|down|drops?|dropping|lag\w*|bad|weak|unstable|terrible|бав\w*|не\s+работи|прекъсва|пада|слаб\w*|лагва\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Complaint();

    // A slow connection, website or game server isn't the PC: other help (and a later check-up) is for those.
    [GeneratedRegex(@"\b(?:internet|wi-?fi|connection|network|website|site|download\s+speed|ping|server|интернет\w*|уай-?фай|връзка\w*|мрежа\w*|сайт\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotThePc();

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

    // "make a backup of my CV on the desktop", "copy report.docx to the USB", "направи копие на CV-то на работния плот".
    // "Back up my files to the USB" is about all of them: that's a backup.
    static ActionIntent? Copy(string text)
    {
        if (Find(text, CopyTo) is not { } match) return null;
        string what = match.Groups["what"].Value.Trim(), where = match.Groups["where"].Value.Trim();
        if (what.Length == 0 || where.Length == 0) return null;
        return AllFiles().IsMatch(what) ? new BackupNow() : new CopyRequest(what, where);
    }

    static readonly Regex[] CopyTo =
    [
        new($@"^{Polite}(?:make|create|save|put|keep)\s+(?:a\s+)?(?:backup|back-up|copy|duplicate|spare\s+copy)\s+(?:of|for)\s+(?<what>.+?)\s+(?:in|on|to|into|onto|at)\s+(?<where>.+)$", Options),
        new($@"^{Polite}(?:back\s*up|backup|copy|duplicate|save\s+a\s+copy\s+of)\s+(?<what>.+?)\s+(?:to|into|in|on|onto)\s+(?<where>.+)$", Options),
        new(@"^(?:направи|създай|запази|сложи)\s+(?:ми\s+)?(?:резервно\s+)?копие\s+(?:на|за)\s+(?<what>.+?)\s+(?:в|във|на)\s+(?<where>.+)$", Options),
        new(@"^(?:копирай|архивирай)\s+(?:ми\s+)?(?<what>.+?)\s+(?:в|във|на)\s+(?<where>.+)$", Options),
    ];

    [GeneratedRegex(@"^(?:all\s+)?(?:of\s+)?(?:my\s+|the\s+)?(?:files|stuff|everything|data|pc|computer|laptop|всичко|файловете(?:\s+ми)?|компютъра)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AllFiles();

    // "back up my files", "when was my last backup?", "set up backups", "also back up D:\Projects", "направи резервно копие".
    static BackupIntent? Backup(string text)
    {
        if (First(text, BackupAdd) && Find(text, BackupAdd) is { } add) return new AddBackupFolder(add.Groups["path"].Value.Trim().Trim('"'));
        if (First(text, BackupSetUp)) return new SetUpBackups();
        if (First(text, BackupAsk)) return new BackupStatus();
        if (First(text, BackupRun)) return new BackupNow();
        return null;
    }

    static readonly Regex[] BackupRun =
    [
        new($@"^{Polite}(?:back\s*up|backup)\s+(?:all\s+)?(?:of\s+)?(?:my\s+|the\s+)?(?:files|pc|computer|laptop|stuff|documents|everything|data)(?:\s+now)?$", Options),
        // "backup now" arrives as "backup": Courtesy drops a trailing "now".
        new($@"^{Polite}(?:back\s*up|backup|back\s*up\s+now|backup\s+now|(?:run|make|start|do)\s+(?:a\s+|the\s+|my\s+)?backup(?:\s+now)?)$", Options),
        new(@"^(?:направи|пусни|започни)\s+(?:ми\s+)?(?:резервно\s+копие|резервни\s+копия|бекъп|архив)(?:\s+(?:на\s+(?:файловете|компютъра)(?:\s+ми)?|сега))?$", Options),
        new(@"^(?:архивирай|бекъпни)\s+(?:ми\s+)?(?:файловете|компютъра)(?:\s+(?:ми|си))?(?:\s+сега)?$", Options),
    ];

    static readonly Regex[] BackupAsk =
    [
        new(@"\b(?:when\s+was\s+(?:my\s+|the\s+)?last\s+backup|(?:are|is)\s+my\s+(?:files|stuff|pc|computer|laptop|data)\s+backed\s+up|backup\s+status|did\s+(?:the\s+|my\s+)?backup\s+(?:run|work|happen)|do\s+i\s+have\s+a\s+backup)\b", Options),
        new(@"кога\s+(?:беше|е)\s+(?:последното\s+)?(?:резервно\s+копие|бекъп|архивиране)|архивирани\s+ли\s+са|имам\s+ли\s+(?:резервно\s+копие|бекъп)", Options),
    ];

    static readonly Regex[] BackupSetUp =
    [
        new($@"^{Polite}(?:set\s*up|setup|turn\s+on|start|configure)\s+(?:my\s+|the\s+)?(?:automatic\s+)?backups?$|^backup\s+settings$", Options),
        new(@"^(?:настрой|включи)\s+(?:ми\s+)?(?:резервните\s+копия|резервни\s+копия|архивирането|бекъпа|бекъп)$", Options),
    ];

    // A full path after "back up" ("also back up D:\Projects", "архивирай и D:\Снимки").
    static readonly Regex[] BackupAdd =
    [
        new($@"^{Polite}(?:also\s+)?(?:back\s*up|backup|add)\s+(?:the\s+folder\s+|folder\s+)?(?<path>""?[a-z]:\\.*?""?)(?:\s+(?:too|as\s+well|to\s+(?:the\s+|my\s+)?backups?))?$", Options),
        new(@"^(?:архивирай|добави)\s+(?:и\s+)?(?:папката\s+)?(?<path>""?[a-z]:\\.*?""?)(?:\s+(?:също|към\s+резервните\s+копия))?$", Options),
    ];

    // "delete my temporary files", "изтрий временните файлове": says how much first, then a button deletes them.
    static readonly Regex[] TempFiles =
    [
        new($@"^{Polite}(?:delete|clear|clean|remove|empty|wipe|get\s+rid\s+of)\s+(?:out\s+|up\s+)?(?:all\s+)?(?:of\s+)?(?:my\s+|the\s+|all\s+the\s+)?(?:temporary|temp)\s+(?:files|folder|data)(?:\s+(?:on|from)\s+(?:my|the|this)\s+(?:pc|computer|laptop))?$", Options),
        new(@"^(?:изтрий|изчисти|почисти|махни|премахни)\s+(?:ми\s+)?(?:всички\s+)?(?:временните|временни)\s+(?:файлове|данни)(?:\s+(?:от|на)\s+(?:компютъра|лаптопа))?$", Options),
    ];

    // "delete old installers", "изтрий старите инсталатори": the installers in Downloads older than a month.
    static readonly Regex[] OldInstallers =
    [
        new($@"^{Polite}(?:delete|clear|clean\s+up|remove|get\s+rid\s+of)\s+(?:all\s+)?(?:my\s+|the\s+)?(?:old\s+)?(?:installers|setup\s+files|installation\s+files)(?:\s+(?:in|from)\s+(?:my\s+|the\s+)?downloads)?$", Options),
        new(@"^(?:изтрий|изчисти|махни|премахни)\s+(?:ми\s+)?(?:всички\s+)?(?:старите\s+)?(?:инсталатори|инсталаторите|инсталационни\s+файлове|инсталационните\s+файлове)(?:\s+(?:от|в)\s+(?:изтеглени|изтеглянията|downloads))?$", Options),
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
