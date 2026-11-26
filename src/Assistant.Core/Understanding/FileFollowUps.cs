using System.Text.RegularExpressions;
using Assistant.Core.Search;

namespace Assistant.Core.Understanding;

/// <summary>
/// What to do with a file from the list just shown: open it, or show it in its folder (<see cref="Reveal"/>). When the
/// message doesn't say which of several, <see cref="File"/> is null and <see cref="Choices"/> holds the ones to pick
/// from. <see cref="Problem"/> is set when the message points past the list ("the fifth one" of three).
/// </summary>
public sealed record FilePick(FileHit? File, bool Reveal, IReadOnlyList<FileHit> Choices, string? Problem = null);

/// <summary>
/// Follow-ups on a list of files, by code: "open it", "open the second one", "show it in the folder", "open the pdf",
/// "where is it?", "отвори го", "второто". Only right after an answer that showed files; anything that doesn't point
/// at one of them (a name that isn't in the list, "open Spotify") is left to the other rules.
/// </summary>
public static partial class FileFollowUps
{
    public static FilePick? Parse(string message, IReadOnlyList<Turn> recent)
    {
        // The files from the latest answer that had some, if it was one of the last two answers.
        int at = recent.Count - 1;
        while (at >= 0 && recent[at].Files.Count == 0) at--;
        if (at < 0 || at < recent.Count - 2) return null;
        IReadOnlyList<FileHit> files = recent[at].Files;
        bool justShown = at == recent.Count - 1;

        string text = Clean(message);
        if (text.Length == 0) return null;

        bool reveal;
        string what;
        if (RevealRequest().Match(text) is { Success: true } shown)
        {
            reveal = true;
            what = shown.Groups["what"].Value.Trim();
        }
        else if (OpenRequest().Match(text) is { Success: true } open)
        {
            reveal = false;
            what = open.Groups["what"].Value.Trim();
        }
        else if (PronounFirst().Match(text) is { Success: true } bulgarian) // "го отвориш" (from "можеш ли да го отвориш")
        {
            reveal = bulgarian.Groups["verb"].Value.StartsWith("покаж", StringComparison.Ordinal);
            what = "";
        }
        else
        {
            // Just "the second one" / "второто" / "the one that's just a folder": only right after the list, and only a
            // clear pointer into it. After "Which one should I show?" it does what was asked; otherwise it opens.
            if (!justShown) return null;
            bool pending = recent[at].PendingReveal ?? false;
            if (Place(text, files, bareNumber: true) is { } alone) return Pick(alone, files, pending);
            return Descriptor().Match(text) is { Success: true } barePointer && Described(barePointer.Groups["d"].Value, files) is { } bareFits
                ? Choose(bareFits, files, pending)
                : null;
        }

        if (Pronouns.Contains(what))
        {
            if (!justShown) return null; // "it" is about the latest answer, and that wasn't the list
            // "it" is the file just opened ("open the second one", then "show it in the folder"), or the only one.
            FileHit? it = recent[at].Focus ?? (files.Count == 1 ? files[0] : null);
            return new FilePick(it, reveal, files);
        }
        if (Place(what, files, bareNumber: true) is { } place) return Pick(place, files, reveal);

        // A name or a type from the list: "open Proba", "the pdf", "the accdb one".
        string name = NameFiller().Replace(what, "").Trim();
        if (name.Length < 2) return null;
        List<FileHit> named = files.Where(file => Names(file, name)).ToList();
        if (named.Count > 0) return Choose(named, files, reveal);

        // What it is or where it is: "the one that's just a folder", "the plain one", "the desktop one".
        string described = Descriptor().Match(what) is { Success: true } pointer ? pointer.Groups["d"].Value : what;
        return Described(described, files) is { } fits ? Choose(fits, files, reveal) : null;
    }

    static FilePick Choose(List<FileHit> fits, IReadOnlyList<FileHit> files, bool reveal) =>
        fits.Count == 1 ? new FilePick(fits[0], reveal, files) : new FilePick(null, reveal, fits);

    /// <summary>
    /// The items a description fits: "just a folder" / "plain" (folders with no project in them), "the project" /
    /// "the solution" / "the unity one" (what <see cref="FileDescriber"/> recognises), or a place in the path ("on the
    /// desktop", "in repos"). Null when it fits none, so the message is left to the other rules.
    /// </summary>
    static List<FileHit>? Described(string description, IReadOnlyList<FileHit> files)
    {
        string text = description.Trim();
        if (text.Length == 0) return null;

        List<FileHit> fits;
        if (Plain().IsMatch(text))
        {
            fits = files.Where(file => FileDescriber.Describe(file).PlainFolder).ToList();
        }
        else if (ProjectWord().Match(text) is { Success: true } project)
        {
            var described = files.Select(file => (file, FileDescriber.Describe(file)))
                                 .Where(pair => !pair.Item2.PlainFolder && Directory.Exists(pair.file.FullPath)).ToList();
            // "the solution", "the unity one", "the C# one": narrowed by that word when it says which.
            string word = project.Value;
            var narrowed = described.Where(pair => pair.Item2.Text.Contains(word, StringComparison.OrdinalIgnoreCase)).ToList();
            fits = (narrowed.Count > 0 ? narrowed : described).Select(pair => pair.file).ToList();
        }
        else
        {
            // A place: every remaining word names a folder on the way ("desktop", "repos"). Whole folder names (or
            // their start), so "it" or "settings" doesn't match half a word somewhere in a path.
            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => !PlaceFiller.Contains(word)).ToArray();
            if (words.Length == 0 || words.Any(word => word.Length < 3)) return null;
            fits = files.Where(file => words.All(word => InPath(file.FullPath, word))).ToList();
        }
        return fits.Count > 0 ? fits : null;
    }

    static bool InPath(string path, string word) =>
        (Path.GetDirectoryName(path.TrimEnd('\\')) ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => folder.Equals(word, StringComparison.OrdinalIgnoreCase)
                           || (word.Length >= 4 && folder.StartsWith(word, StringComparison.OrdinalIgnoreCase)));

    static readonly HashSet<string> PlaceFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "on", "in", "at", "from", "inside", "under", "my", "one", "folder", "file", "is", "it's", "its",
        "that", "this", "which", "please",
        "на", "в", "във", "от", "под", "папката", "папка", "файла", "е",
    };

    /// <summary>
    /// Right after a file search, "a folder, perhaps?", "what about folders?", "files?", "папка?": the same search again,
    /// for folders (true) or for files (false). Null for anything else.
    /// </summary>
    public static (FileSearchQuery Search, bool Folders)? Again(string message, IReadOnlyList<Turn> recent)
    {
        if (recent.Count == 0 || recent[^1].Search is not { Keywords.Count: > 0 } search) return null;
        string text = Clean(message.Replace(',', ' '));
        if (Retry().Match(text) is not { Success: true } match) return null;
        string kind = match.Groups["kind"].Value;
        bool folders = !kind.StartsWith("file", StringComparison.Ordinal) && !kind.StartsWith("файл", StringComparison.Ordinal);
        return (search, folders);
    }

    /// <summary>
    /// A question about the list just shown: "which one is just a folder?", "what's in them?", "what's the
    /// difference?", "кое е просто папка?". Returns the list; null for anything else, or for a question about something
    /// of the user's own that the list doesn't hold ("what's in my downloads folder?").
    /// </summary>
    public static IReadOnlyList<FileHit>? AboutList(string message, IReadOnlyList<Turn> recent)
    {
        if (recent.Count == 0 || recent[^1].Files.Count == 0) return null;
        string text = Clean(message);
        if (text.Length == 0 || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 14) return null;
        bool asks = message.TrimEnd().EndsWith('?') || QuestionStart().IsMatch(text);
        if (!asks || !ListWord().IsMatch(text) || Own().IsMatch(text)) return null;
        return recent[^1].Files;
    }

    /// <summary>"Which one is just a folder?", "a normal folder", "обикновена папка": the answer leads with that.</summary>
    public static bool AsksForPlain(string message) => Plain().IsMatch(message);

    [GeneratedRegex(@"^(?:which|what|what's|whats|is|are|does|do|how|describe|explain|tell\s+me\s+about|кой|коя|кое|кои|какво|каква|какви|какъв|има\s+ли|опиши|разкажи)(?!\p{L})",
                    RegexOptions.CultureInvariant)]
    private static partial Regex QuestionStart();

    [GeneratedRegex(@"(?<!\p{L})(?:one|ones|them|these|those|it|its|each|folder|folders|file|files|difference|inside|contain|contains|тях|тези|тия|него|нея|това|всеки|всяка|всяко|папк\p{L}*|файл\p{L}*|разлик\p{L}*|вътре|съдърж\p{L}*)(?!\p{L})",
                    RegexOptions.CultureInvariant)]
    private static partial Regex ListWord();

    // "my downloads folder", "моята папка": something of the user's own, not one from the list.
    [GeneratedRegex(@"(?<!\p{L})(?:my|mine|моя|моята|моите|моето|моят)(?!\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex Own();

    [GeneratedRegex(@"(?<!\p{L})(?:just|only|plain|normal|regular|simple|ordinary|real|просто|само|обикновен\p{L}*|нормалн\p{L}*|истинск\p{L}*)(?!\p{L})",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Plain();

    /// <summary>Whether opening this file runs code (a program or script): those only open after a press of "Run it".</summary>
    public static bool Runs(string path) =>
        RunnableExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Opening these runs code, so the user confirms first (safety rule in the spec).</summary>
    public static readonly IReadOnlyList<string> RunnableExtensions =
        [".exe", ".bat", ".cmd", ".com", ".msi", ".ps1", ".vbs", ".js", ".scr", ".lnk"];

    static readonly HashSet<string> Pronouns = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "it", "it up", "it again", "that", "this", "that one", "this one", "that file", "this file", "the file",
        "the document", "that document", "the one", "the one you found", "the file you found",
        "го", "я", "него", "нея", "това", "онова", "файла", "файлът", "документа", "този файл", "го пак",
    };

    // Where in the list: -1 is past its end; null is not a place at all.
    static int? Place(string text, IReadOnlyList<FileHit> files, bool bareNumber)
    {
        Match english = EnglishPlace().Match(text);
        if (english.Success)
        {
            string word = english.Groups["n"].Value.ToLowerInvariant();
            if (Numbers().Match(word) is { Success: true } digits)
                return Index(int.Parse(digits.Value), files);
            return word switch
            {
                "first" or "top" => 0,
                "second" => Index(2, files),
                "third" => Index(3, files),
                "fourth" => Index(4, files),
                "fifth" => Index(5, files),
                "sixth" => Index(6, files),
                "seventh" => Index(7, files),
                "eighth" => Index(8, files),
                "ninth" => Index(9, files),
                "tenth" => Index(10, files),
                "last" or "bottom" => files.Count - 1,
                "newest" or "latest" or "most recent" => Newest(files),
                "oldest" => Oldest(files),
                _ => null,
            };
        }

        Match bulgarian = BulgarianPlace().Match(text);
        if (bulgarian.Success)
        {
            return bulgarian.Groups["n"].Value.ToLowerInvariant() switch
            {
                "първ" => 0,
                "втор" => Index(2, files),
                "трет" => Index(3, files),
                "четвърт" => Index(4, files),
                "пет" => Index(5, files),
                "шест" => Index(6, files),
                "седм" => Index(7, files),
                "осм" => Index(8, files),
                "девет" => Index(9, files),
                "десет" => Index(10, files),
                "последн" => files.Count - 1,
                "най-нов" => Newest(files),
                "най-стар" => Oldest(files),
                _ => null,
            };
        }

        if (NumberPlace().Match(text) is { Success: true } numbered)
            return Index(int.Parse(numbered.Groups["n"].Value), files);
        if (bareNumber && Numbers().Match(text) is { Success: true } bare && bare.Length == text.Length)
            return Index(int.Parse(bare.Value), files);
        return null;
    }

    static int Index(int number, IReadOnlyList<FileHit> files) => number >= 1 && number <= files.Count ? number - 1 : -1;

    static int Newest(IReadOnlyList<FileHit> files) =>
        files.Select((file, index) => (file.Modified, index)).MaxBy(pair => pair.Modified).index;

    static int Oldest(IReadOnlyList<FileHit> files) =>
        files.Select((file, index) => (file.Modified, index)).MinBy(pair => pair.Modified).index;

    static FilePick Pick(int index, IReadOnlyList<FileHit> files, bool reveal) =>
        index >= 0
            ? new FilePick(files[index], reveal, files)
            : new FilePick(null, reveal, files,
                files.Count == 1 ? "There was only one file in that list." : $"There were only {files.Count} files in that list.");

    // "proba" names Proba.accdb; "pdf" or "accdb" names it by its type.
    static bool Names(FileHit file, string name)
    {
        string bare = Path.GetFileNameWithoutExtension(file.FullPath);
        string type = Path.GetExtension(file.FullPath).TrimStart('.');
        return bare.Contains(name, StringComparison.OrdinalIgnoreCase)
               || file.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)
               || type.Equals(name.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
    }

    // Lower case, no end punctuation, without "yes", "please", "can you" and the like around the request.
    static string Clean(string message)
    {
        string text = message.Trim().ToLowerInvariant().Replace('\u2019', '\'').TrimEnd('?', '!', '.', ' ');
        string before;
        do
        {
            before = text;
            text = Lead().Replace(text, "").Trim();
            text = Tail().Replace(text, "").Trim().TrimEnd('?', '!', '.', ',', ' ');
        }
        while (text != before);
        return text;
    }

    [GeneratedRegex(@"^(?:yes|yeah|yep|ok|okay|alright|great|cool|thanks|thank\s+you|please|pls|now|then|and|so|just|can\s+you|could\s+you|would\s+you|will\s+you|can\s+u|go\s+ahead\s+and|i\s+want\s+to|i'd\s+like\s+to|да|добре|супер|мерси|благодаря|моля|сега|и|ами|а|можеш\s+ли\s+да|може\s+ли\s+да|би\s+ли|искам\s+да|да)(?:[\s,]+|$)",
                    RegexOptions.CultureInvariant)]
    private static partial Regex Lead();

    [GeneratedRegex(@"[\s,]+(?:please|pls|for\s+me|now|thanks|моля|ти|ми\s+го)$", RegexOptions.CultureInvariant)]
    private static partial Regex Tail();

    // "show it in the folder", "where is it", "open the folder", "open the folder of the second one", "покажи го в папката".
    [GeneratedRegex(@"^(?:(?:show|open|reveal)\s+(?<what>.*?)\s*in\s+(?:the\s+|its\s+|a\s+)?(?:folder|explorer|file\s+explorer)"
                    + @"|show\s+(?:me\s+)?where\s+(?<what>.*?)\s+is(?:\s+(?:saved|located|stored|kept))?"
                    + @"|where\s+(?:is|'s)\s+(?<what>.*?)(?:\s+(?:saved|located|stored|kept))?"
                    + @"|where's\s+(?<what>.*?)"
                    + @"|open\s+(?:the\s+|its\s+|that\s+|this\s+|it'?s\s+)?(?:containing\s+)?(?:folder|file\s+location|location)(?:\s+(?:of|for)\s+(?<what>.+?)|\s+(?:it'?s|it\s+is)\s+in)?"
                    + @"|reveal\s+(?<what>.+?)"
                    + @"|покажи\s+(?:ми\s+)?(?<what>.*?)\s*в\s+(?:папката|explorer)"
                    + @"|(?:покажи\s+(?:ми\s+)?)?къде\s+(?:е|се\s+намира)\s*(?<what>.*?)"
                    + @"|отвори\s+(?:му\s+|й\s+)?папката(?:\s+(?:на|с)\s+(?<what>.+?))?)$",
                    RegexOptions.CultureInvariant)]
    private static partial Regex RevealRequest();

    // "open it", "open the second one", "play it", "run it", "отвори го", "пусни второто".
    [GeneratedRegex(@"^(?:open|launch|start|run|play|show\s+me|pull\s+up|bring\s+up|отвори|отвориш|пусни|пуснеш|стартирай|покажи(?:\s+ми)?)(?:\s+(?<what>.*))?$",
                    RegexOptions.CultureInvariant)]
    private static partial Regex OpenRequest();

    // Bulgarian puts the pronoun first after "можеш ли да": "го отвориш", "го покажеш в папката".
    [GeneratedRegex(@"^(?:го|я)\s+(?<verb>отвориш|пуснеш|покажеш)(?:\s+в\s+папката)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PronounFirst();

    [GeneratedRegex(@"^(?:the\s+)?(?<n>first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|last|top|bottom|newest|latest|most\s+recent|oldest|\d{1,2}(?:st|nd|rd|th))(?:\s+(?:one|file|result|document|item|on\s+the\s+list))?$",
                    RegexOptions.CultureInvariant)]
    private static partial Regex EnglishPlace();

    [GeneratedRegex(@"^(?:на\s+)?(?<n>първ|втор|трет|четвърт|пет|шест|седм|осм|девет|десет|последн|най-нов|най-стар)(?:ия|ият|ото|ата|ите|ия|о|а)?(?:\s+(?:файл|документ|резултат))?$",
                    RegexOptions.CultureInvariant)]
    private static partial Regex BulgarianPlace();

    [GeneratedRegex(@"^(?:number|no\.?|#|номер|№)\s*(?<n>\d{1,2})(?:\s+(?:one|file))?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPlace();

    [GeneratedRegex(@"\d{1,2}", RegexOptions.CultureInvariant)]
    private static partial Regex Numbers();

    // "a folder, perhaps", "what about folders", "maybe a folder then", "а папка", "може би папка". One word around it
    // may be anything ("a folder, perhabs?"): this is only tried right after a search, and only looks again.
    [GeneratedRegex(@"^(?:(?:what|how)\s+about\s+|may\s*be\s+|може\s+би\s+|(?!not\s|no\s|не\s)\p{L}+\s+)?(?:an?\s+|the\s+|some\s+|in\s+|в\s+)?(?<kind>folders?|director(?:y|ies)|files?|папк(?:а|и|ата|ите)|директори(?:я|и)|файл(?:ове|а)?)(?:\s+(?:with\s+that\s+name|named\s+like\s+that|instead|then|perhaps|maybe|\p{L}+))?$",
                    RegexOptions.CultureInvariant)]
    private static partial Regex Retry();

    // "the one that's just a folder", "the one on the desktop", "the plain one", "the desktop one", "тази на десктопа",
    // "тази, която е просто папка": the part that says which (d).
    [GeneratedRegex(@"^(?:(?:the\s+)?(?:one|folder|file)\s+(?:that's|thats|that\s+is|which\s+is|that|which|on|in|from|at|inside)\s+(?<d>.+)"
                    + @"|(?:the\s+)?(?<d>[\p{L}\p{N}#+.-]+(?:\s+[\p{L}\p{N}#+.-]+)?)\s+(?:one|folder|file)"
                    + @"|(?:тази|този|това|онази|онзи|онова|тая|тоя)?\s*,?\s*(?:която|който|което)\s+е\s+(?<d>.+)"
                    + @"|(?:тази|този|това|онази|онзи|онова|тая|тоя|папката|файла)\s+(?:на|в|във|от)\s+(?<d>.+))$",
                    RegexOptions.CultureInvariant)]
    private static partial Regex Descriptor();

    // What FileDescriber recognises in a folder.
    [GeneratedRegex(@"(?<!\p{L})(?:project|solution|repository|repo|git|unity|node|python|java|rust|c#|c\+\+|visual\s+studio|проект\p{L}*)(?!\p{L})",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectWord();

    // "the pdf one", "my proba file" → "pdf", "proba".
    [GeneratedRegex(@"^(?:the|my|that|this|файла|документа)\s+|\s+(?:one|file|document|файл|файла)$", RegexOptions.CultureInvariant)]
    private static partial Regex NameFiller();
}
