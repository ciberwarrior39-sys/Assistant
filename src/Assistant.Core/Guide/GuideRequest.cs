using System.Text.RegularExpressions;
using Assistant.Core.Actions;

namespace Assistant.Core.Guide;

/// <summary>
/// What to find in the window: a control by its name ("where is the save button" gives "save"), or a task whose first
/// click has to be worked out ("where do I click to change the font").
/// </summary>
/// <param name="App">Set when the message named an app ("…in Excel"): it only counts while that app's window is the one in use.</param>
public sealed record GuideAsk(string What, bool Named, string Kind = "", string App = "");

/// <summary>
/// Recognises "show me where to click" requests, in English and Bulgarian. They always name a kind of control (button,
/// menu, tab…), a click ("where do I click to…"), or this window ("how do I … in this app"); "where is my tax
/// report" is a file search and "how do I bake bread" is chat, and neither comes here.
/// </summary>
public static partial class GuideRequest
{
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    const string Kind = @"(?<k>button|menu|option|tab|icon|check\s?box|link|field|box|setting|toggle|switch|slider|dropdown|drop-down)s?";
    const string Here = @"(?:here|in\s+(?:this|the)\s+(?:app|window|program|page)|on\s+(?:this|the|my)\s+(?:screen|page|window))";
    const string Click = @"(?:click|press|tap|push|hit)";
    const string KindBg = @"(?<k>бутон(?:ът|а|чето)?|меню(?:то)?|опция(?:та)?|настройка(?:та)?|раздел(?:ът|а)?|икона(?:та)?|поле(?:то)?|отметка(?:та)?)";
    const string ClickBg = @"(?:натисна|кликна|цъкна|щракна)";
    const string HereBg = @"(?:тук|в\s+(?:това|тази|този)\s+(?:приложение|програма|прозорец))";

    // A control by name.
    static readonly Regex[] NamedRules =
    [
        new($@"^where(?:'s|’s|\s+is|\s+are)\s+(?:the\s+|my\s+|that\s+)?(?<x>.+?)\s+{Kind}(?:\s+{Here})?$", Options),
        new($@"^(?:where\s+can\s+i\s+find|i\s+can(?:'t|’t|not)\s+find|show(?:\s+me)?|point\s+(?:to|at)|highlight)\s+(?:where\s+)?(?:the\s+|my\s+|that\s+)?(?<x>.+?)\s+{Kind}(?:\s+is)?(?:\s+{Here})?$", Options),
        new($@"^къде\s+(?:е|са|се\s+намира)\s+{KindBg}\s+(?:за\s+)?(?<x>.+?)(?:\s+{HereBg})?$", Options),
        new($@"^(?:покажи(?:\s+ми)?|намери|посочи)\s+(?:къде\s+е\s+)?{KindBg}\s+(?:за\s+)?(?<x>.+?)(?:\s+{HereBg})?$", Options),
    ];

    // Something to do: the first click is worked out from the controls in the window.
    static readonly Regex[] TaskRules =
    [
        new($@"^(?:where|what)\s+(?:do|should|can|would)\s+i\s+{Click}\s+(?:on\s+)?(?:to|for)\s+(?<t>.+?)(?:\s+{Here})?$", Options),
        new($@"^(?:show|tell)\s+me\s+(?:where|what)\s+to\s+{Click}\s+(?:on\s+)?(?:to|for)\s+(?<t>.+?)(?:\s+{Here})?$", Options),
        new($@"^(?:show\s+me\s+)?how\s+(?:do|can|would)\s+i\s+(?<t>.+?)\s+{Here}$", Options),
        new($@"^show\s+me\s+how\s+to\s+(?<t>.+?)\s+{Here}$", Options),
        new($@"^where\s+(?:do|can)\s+i\s+(?<t>.+?)\s+{Here}$", Options),
        new($@"^(?:покажи\s+(?:ми\s+)?)?къде\s+(?:да|трябва\s+да)\s+{ClickBg}\s*,?\s+за\s+(?:да\s+)?(?<t>.+?)(?:\s+{HereBg})?$", Options),
        new($@"^(?:покажи\s+(?:ми\s+)?)?как\s+(?:да|мога\s+да|се)\s+(?<t>.+?)\s+{HereBg}$", Options),
    ];

    // "…in Excel", "…в Word": an app by name. Whether that is the window in use is for the caller to check
    // (GuideAsk.App), so "where is Plovdiv in Bulgaria" and a question about another program stay ordinary chat.
    const string InApp = @"\s+in\s+(?:the\s+|my\s+)?(?<app>[\p{L}\p{N}][\p{L}\p{N} .+-]{1,24})";
    const string InAppBg = @"\s+(?:в|във)\s+(?<app>[\p{L}\p{N}][\p{L}\p{N} .+-]{1,24})";

    static readonly Regex[] NamedInApp =
    [
        new($@"^where(?:'s|’s|\s+is|\s+are)\s+(?:the\s+|my\s+|that\s+)?(?<x>.+?){InApp}$", Options),
        new($@"^(?:where\s+can\s+i\s+find|where\s+do\s+i\s+find|i\s+can(?:'t|’t|not)\s+find|show\s+me|point\s+(?:to|at))\s+(?:the\s+|my\s+|that\s+)?(?<x>.+?){InApp}$", Options),
        new($@"^къде\s+(?:е|са|се\s+намира|да\s+намеря)\s+(?<x>.+?){InAppBg}$", Options),
    ];

    static readonly Regex[] TaskInApp =
    [
        new($@"^(?:show\s+me\s+)?how\s+(?:do|can|would)\s+i\s+(?<t>.+?){InApp}$", Options),
        new($@"^show\s+me\s+how\s+to\s+(?<t>.+?){InApp}$", Options),
        new($@"^where\s+(?:do|can)\s+i\s+(?<t>.+?){InApp}$", Options),
        new($@"^(?:покажи\s+(?:ми\s+)?)?как\s+(?:да|мога\s+да|се)\s+(?<t>.+?){InAppBg}$", Options),
    ];

    /// <summary>True when the app a message named ("excel") is the app in use ("Excel", "Microsoft Excel").</summary>
    public static bool SameApp(string named, string inUse)
    {
        string a = named.Trim(), b = inUse.Trim();
        return a.Length >= 2 && b.Length >= 2
               && (b.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    public static GuideAsk? Parse(string message)
    {
        string text = ActionParser.Courtesy(Spaces().Replace(message.Trim(), " "));
        if (text.Length == 0 || text.Length > 200) return null;

        foreach (Regex rule in NamedRules)
        {
            if (rule.Match(text) is { Success: true } named && Clean(named.Groups["x"].Value) is { Length: > 0 } name)
                return new GuideAsk(name, Named: true, KindOf(named.Groups["k"].Value));
        }
        foreach (Regex rule in TaskRules)
        {
            if (rule.Match(text) is { Success: true } task && Clean(task.Groups["t"].Value) is { Length: > 1 } what)
                return new GuideAsk(what, Named: false);
        }
        foreach (Regex rule in NamedInApp)
        {
            if (rule.Match(text) is { Success: true } named && Clean(named.Groups["x"].Value) is { Length: > 0 } name)
                return new GuideAsk(name, Named: true, App: Clean(named.Groups["app"].Value));
        }
        foreach (Regex rule in TaskInApp)
        {
            if (rule.Match(text) is { Success: true } task && Clean(task.Groups["t"].Value) is { Length: > 1 } what)
                return new GuideAsk(what, Named: false, App: Clean(task.Groups["app"].Value));
        }
        return null;
    }

    /// <summary>
    /// Typed right after a control was marked, instead of pressing "I can't see it": "I don't see it", "where?",
    /// "не го виждам". Only means that while a ring was just shown; the caller checks.
    /// </summary>
    public static bool CantSee(string message) => Any(message, CantSeeRules);

    /// <summary>Typed instead of pressing "Next step": "next", "what now?", "done", "натиснах го".</summary>
    public static bool Next(string message) => Any(message, NextRules);

    static bool Any(string message, Regex[] rules)
    {
        string text = ActionParser.Courtesy(Spaces().Replace(message.Trim(), " "));
        return text.Length is > 0 and <= 80 && rules.Any(rule => rule.IsMatch(text));
    }

    // Loose on purpose: these only count in the minutes after a ring was shown, when "go to it", "point at it" or
    // "I still don't see anything" can hardly mean something else (and "go to it" must not become "open an app").
    static readonly Regex[] CantSeeRules =
    [
        new(@"\b(?:can(?:'t|’t|not)|cant|don(?:'t|’t)|dont|do\s+not|didn(?:'t|’t)|couldn(?:'t|’t)|could\s+not|unable\s+to)\s+(?:really\s+|even\s+|quite\s+)?(?:see|find|spot|notice|tell)\b", Options),
        new(@"\b(?:point|walk\s+(?:to|over|there)|come\s+(?:here|over)|go\s+(?:to\s+(?:it|that|the\s+(?:button|ring|spot|place|one))|there|over|and\s+(?:point|show)|next\s+to|near)|show\s+(?:me\s+)?(?:it|where|again|that)|lead\s+me|take\s+me\s+(?:to\s+it|there)|which\s+one)\b", Options),
        new(@"^(?:(?:where|wher)(?:\s+(?:is\s+)?(?:it|that|exactly))?|show\s+me|nothing|still\s+nothing|not\s+there|nothing\s+there)$", Options),
        new(@"(?<![\p{L}])(?:не\s+(?:(?:го|я|ги|мога\s+да\s+го)\s+)?(?:виждам|намирам|откривам|видя|намеря)|иди|отиди|ела|заведи\s+ме|няма\s+го)(?![\p{L}])", Options),
        new(@"^(?:(?:покажи|посочи)(?:\s+ми)?(?:\s+го)?(?:\s+къде(?:\s+е)?)?|къде(?:\s+е)?(?:\s+точно)?|кое|(?:пак\s+)?нищо)$", Options),
    ];

    static readonly Regex[] NextRules =
    [
        new(@"^(?:next(?:\s+step)?|what(?:'s|’s|\s+is)?\s+next|what\s+now|now\s+what|and\s+(?:then|now)|then(?:\s+what)?|(?:i\s+)?(?:clicked|pressed|did)(?:\s+(?:it|that))?|done|got\s+it)$", Options),
        new(@"^(?:следваща(?:та)?\s+стъпка|какво\s+следва|а\s+сега|и\s+сега|сега\s+какво|после|след\s+това|натиснах(?:\s+го)?|кликнах(?:\s+го)?|готово)$", Options),
    ];

    static string Clean(string words) => words.Trim().Trim('"', '„', '“', '”', '«', '»', '\'', ' ');

    // The kind of control the user named, in the scanner's words ("" when it doesn't narrow things: "option", "setting").
    static string KindOf(string word)
    {
        string kind = word.ToLowerInvariant();
        if (kind.StartsWith("button", StringComparison.Ordinal) || kind.StartsWith("бутон", StringComparison.Ordinal)) return "button";
        if (kind.StartsWith("menu", StringComparison.Ordinal) || kind.StartsWith("меню", StringComparison.Ordinal)) return "menu";
        if (kind.StartsWith("tab", StringComparison.Ordinal) || kind.StartsWith("раздел", StringComparison.Ordinal)) return "tab";
        if (kind.StartsWith("check", StringComparison.Ordinal) || kind.StartsWith("отметка", StringComparison.Ordinal)) return "checkbox";
        if (kind.StartsWith("link", StringComparison.Ordinal)) return "link";
        if (kind is "field" or "box" || kind.StartsWith("поле", StringComparison.Ordinal)) return "text field";
        if (kind.StartsWith("slider", StringComparison.Ordinal)) return "slider";
        return "";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
