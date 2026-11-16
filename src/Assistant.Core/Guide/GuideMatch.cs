using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Core.Guide;

/// <summary>A control that fits what was asked for, and how well (100: the same name).</summary>
public sealed record GuideCandidate(UiElement Element, int Score);

/// <summary>
/// Finds the control a name means among those in a window, by code: the same name, the start of it, all of its words,
/// a word for the same thing in the other language ("save" and "запази"), or one typo away. Things to click beat
/// plain text, and what the user called it ("button", "tab") counts. Also words everything for people and models:
/// the list a model picks from, and "the Save button, top left".
/// </summary>
public static partial class GuideMatch
{
    /// <summary>At or above this, and clear of the runner-up, the match is shown without asking.</summary>
    public const int Sure = 60;

    const int Lowest = 50;

    // What can be clicked or filled in; the rest (text, panes, pictures) is scenery.
    static readonly HashSet<string> Clickable = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "menu item", "menu", "tab", "link", "checkbox", "radio button", "combo box", "text field", "list item",
        "tree item", "split button", "slider", "spinner",
    };

    // The same thing in other words or the other language, for the names apps use most.
    static readonly string[][] SameThing =
    [
        ["save", "запази", "запис", "запазване", "записване", "съхрани"],
        ["save as", "запиши като", "запази като", "запазване като"],
        ["open", "отвори", "отваряне"],
        ["new", "нов", "нова", "ново", "създай"],
        ["close", "exit", "quit", "затвори", "изход", "затваряне"],
        ["settings", "options", "preferences", "настройки", "опции", "предпочитания"],
        ["print", "печат", "отпечатай", "принтирай", "отпечатване"],
        ["search", "find", "търси", "търсене", "намери", "намиране"],
        ["delete", "remove", "изтрий", "изтриване", "премахни", "премахване"],
        ["send", "изпрати", "изпращане"],
        ["copy", "копирай", "копиране"],
        ["paste", "постави", "поставяне"],
        ["cut", "изрежи", "изрязване"],
        ["undo", "отмени", "отмяна"],
        ["redo", "повтори", "възстанови"],
        ["help", "помощ"],
        ["file", "файл"],
        ["edit", "редактиране", "редактирай", "редакция"],
        ["view", "изглед"],
        ["format", "формат", "форматиране"],
        ["insert", "вмъкни", "вмъкване"],
        ["tools", "инструменти"],
        ["download", "downloads", "изтегли", "изтегляне", "изтегляния", "свали"],
        ["upload", "качи", "качване"],
        ["share", "сподели", "споделяне"],
        ["back", "назад"],
        ["forward", "напред"],
        ["refresh", "reload", "обнови", "опресни", "презареди"],
        ["home", "начало"],
        ["cancel", "отказ", "откажи"],
        ["ok", "добре"],
        ["apply", "приложи"],
        ["next", "напред", "следващ", "следваща", "нататък"],
        ["sign in", "log in", "login", "вход", "влез", "влизане"],
        ["sign out", "log out", "logout", "изход", "излез", "излизане"],
        ["font", "шрифт"],
        ["bold", "получер", "удебелен"],
        ["zoom", "мащаб", "увеличение"],
        ["history", "история", "хронология"],
        ["bookmarks", "favorites", "favourites", "отметки", "любими"],
        ["attach", "attachment", "прикачи", "прикачване"],
        ["replace", "замести", "заместване", "замени"],
        ["select all", "избери всички", "маркирай всичко", "избери всичко"],
        ["rename", "преименувай", "преименуване"],
        ["properties", "свойства"],
        ["account", "profile", "профил", "акаунт"],
    ];

    /// <summary>The controls that fit, best first. Empty when nothing comes close.</summary>
    public static IReadOnlyList<GuideCandidate> Find(string wanted, IReadOnlyList<UiElement> elements, string kind = "")
    {
        string want = Normal(wanted);
        if (want.Length == 0) return [];
        string[] wantWords = want.Split(' ');
        HashSet<string> wantSame = Same(want);

        var found = new List<GuideCandidate>();
        var seen = new HashSet<(string, string, int, int)>();
        foreach (UiElement element in elements)
        {
            if (element.Width <= 0 || element.Height <= 0) continue;
            string name = Normal(element.Name);
            if (name.Length == 0 || !seen.Add((name, element.Kind, element.X, element.Y))) continue;

            int score = NameScore(want, wantWords, wantSame, name);
            if (score == 0) continue;
            bool clickable = Clickable.Contains(element.Kind);
            score += clickable ? 5 : -30;
            if (kind.Length > 0 && element.Kind.Contains(kind, StringComparison.OrdinalIgnoreCase)) score += 8;
            if (!element.Enabled) score -= 5;
            if (score >= Lowest) found.Add(new GuideCandidate(element, score));
        }
        return found.OrderByDescending(candidate => candidate.Score).ToList();
    }

    /// <summary>The one control to show without asking, or null: nothing fits well enough, or two fit equally.</summary>
    public static UiElement? Best(IReadOnlyList<GuideCandidate> candidates) =>
        candidates.Count > 0 && candidates[0].Score >= Sure && (candidates.Count == 1 || candidates[0].Score - candidates[1].Score >= 8)
            ? candidates[0].Element
            : null;

    static int NameScore(string want, string[] wantWords, HashSet<string> wantSame, string name)
    {
        if (name == want) return 100;
        if (wantSame.Contains(name)) return 90;
        string[] nameWords = name.Split(' ');
        // "save" for "Save as…" and "page setup" for "Page setup and printing"; also the name inside a longer ask.
        if (name.StartsWith(want + " ", StringComparison.Ordinal)) return 80;
        if (want.StartsWith(name + " ", StringComparison.Ordinal) && name.Length >= 3) return 72;
        if (wantWords.All(word => nameWords.Contains(word))) return 75;
        if (wantSame.Any(same => same.Contains(' ') ? name.Contains(same, StringComparison.Ordinal) : nameWords.Contains(same))) return 68;
        if (wantWords.All(word => word.Length >= 3 && nameWords.Any(part => part.StartsWith(word, StringComparison.Ordinal)))) return 64;
        if (wantWords.Length == 1 && want.Length >= 5 && nameWords.Any(part => Math.Abs(part.Length - want.Length) <= 1 && Distance(part, want) <= 1)) return 56;
        return 0;
    }

    static HashSet<string> Same(string want)
    {
        var same = new HashSet<string>(StringComparer.Ordinal);
        foreach (string[] group in SameThing)
        {
            if (!group.Contains(want)) continue;
            foreach (string word in group)
            {
                if (word != want) same.Add(word);
            }
        }
        return same;
    }

    /// <summary>
    /// A control's name the way people say it: lower case, without the menu's underline mark ("&amp;File"), a keyboard
    /// shortcut after it ("Save\tCtrl+S", "Bold (Ctrl+B)"), dots or a colon at the end.
    /// </summary>
    public static string Normal(string name)
    {
        string text = name;
        int tab = text.IndexOf('\t');
        if (tab >= 0) text = text[..tab];
        text = Shortcut().Replace(text, "");
        text = text.Replace("&", "").Replace("…", " ").Replace("...", " ");
        var kept = new StringBuilder(text.Length);
        foreach (char letter in text.ToLowerInvariant())
            kept.Append(char.IsLetterOrDigit(letter) || letter is '+' or '#' ? letter : ' ');
        return Spaces().Replace(kept.ToString(), " ").Trim();
    }

    /// <summary>
    /// The controls a model may pick from, one per line: "12. Save (button)". Only things to click, each name once,
    /// the top of the window first, at most <paramref name="max"/>. The numbers are positions in this text only.
    /// </summary>
    public static string Listing(UiWindow window, int max)
    {
        var lines = new StringBuilder();
        int number = 0;
        foreach (UiElement element in Pickable(window).Take(max))
            lines.Append(++number).Append(". ").Append(Shown(element.Name)).Append(" (").Append(element.Kind).Append(")\n");
        return lines.ToString().TrimEnd();
    }

    /// <summary>The control a line of a listing names ("12. Save (button)"), found again in the window; null when it's gone.</summary>
    public static UiElement? FromListing(string listing, int number, UiWindow window)
    {
        foreach (string line in listing.Split('\n'))
        {
            Match entry = ListingLine().Match(line.Trim());
            if (!entry.Success || !int.TryParse(entry.Groups["n"].Value, out int at) || at != number) continue;
            string name = Normal(entry.Groups["name"].Value), kind = entry.Groups["kind"].Value.Trim();
            return Pickable(window).FirstOrDefault(element => Normal(element.Name) == name && element.Kind == kind)
                   ?? Pickable(window).FirstOrDefault(element => Normal(element.Name) == name);
        }
        return null;
    }

    static IEnumerable<UiElement> Pickable(UiWindow window)
    {
        var seen = new HashSet<(string, string)>();
        return window.Elements
            .Where(element => element.Enabled && element.Width > 0 && element.Height > 0 && Clickable.Contains(element.Kind)
                              && Normal(element.Name).Length > 0 && seen.Add((Normal(element.Name), element.Kind)));
    }

    /// <summary>"the Save button, top left": the name, what it is, and roughly where in its window.</summary>
    public static string Describe(UiElement element, UiWindow window)
    {
        string where = "";
        if (window.Width > 0 && window.Height > 0)
        {
            double across = (element.X + element.Width / 2.0 - window.X) / window.Width;
            double down = (element.Y + element.Height / 2.0 - window.Y) / window.Height;
            string row = down < 0.34 ? "top" : down > 0.66 ? "bottom" : "middle";
            string column = across < 0.34 ? "left" : across > 0.66 ? "right" : "middle";
            where = row == "middle" && column == "middle" ? ", in the middle" : row == "middle" ? $", on the {column}" : $", {row} {column}";
        }
        return $"\"{Shown(element.Name)}\" ({element.Kind}{where})";
    }

    /// <summary>A name short enough for a button or a list, without its shortcut.</summary>
    public static string Shown(string name)
    {
        int tab = name.IndexOf('\t');
        string text = Spaces().Replace((tab >= 0 ? name[..tab] : name).Replace("&", ""), " ").Trim();
        return text.Length > 40 ? text[..40].TrimEnd() + "…" : text;
    }

    // Edit distance, for one typo ("setings").
    static int Distance(string a, string b)
    {
        int[] previous = new int[b.Length + 1], current = new int[b.Length + 1];
        for (int column = 0; column <= b.Length; column++) previous[column] = column;
        for (int row = 1; row <= a.Length; row++)
        {
            current[0] = row;
            for (int column = 1; column <= b.Length; column++)
            {
                int change = a[row - 1] == b[column - 1] ? 0 : 1;
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + change);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    [GeneratedRegex(@"\s*\((?:ctrl|alt|shift|win|f\d)[^)]*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Shortcut();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(?<n>\d+)\.\s+(?<name>.+?)\s+\((?<kind>[^()]+)\)$")]
    private static partial Regex ListingLine();
}
