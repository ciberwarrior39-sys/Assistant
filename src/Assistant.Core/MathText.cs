using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Core;

/// <summary>
/// Makes model output readable in the chat bubble, which shows plain text: LaTeX becomes symbols
/// ("\vec{u} \cdot \vec{v}" → "u⃗ · v⃗", "\frac{1}{2}" → "1/2", "\sqrt3" → "√3", "x^2" → "x²") and markdown marks
/// are dropped ("**bold**" → "bold", "* item" → "• item", "---" lines go). File paths are left exactly as they are.
/// </summary>
public static partial class MathText
{
    static readonly Dictionary<string, string> Symbols = new()
    {
        ["cdot"] = "·", ["cdotp"] = "·", ["times"] = "×", ["div"] = "÷", ["pm"] = "±", ["mp"] = "∓", ["ast"] = "∗",
        ["leq"] = "≤", ["le"] = "≤", ["leqslant"] = "≤", ["geq"] = "≥", ["ge"] = "≥", ["geqslant"] = "≥",
        ["neq"] = "≠", ["ne"] = "≠", ["approx"] = "≈", ["sim"] = "∼", ["simeq"] = "≃", ["cong"] = "≅", ["equiv"] = "≡",
        ["infty"] = "∞", ["propto"] = "∝", ["ll"] = "≪", ["gg"] = "≫",
        ["rightarrow"] = "→", ["to"] = "→", ["longrightarrow"] = "⟶", ["Rightarrow"] = "⇒", ["implies"] = "⇒",
        ["Longrightarrow"] = "⟹", ["leftarrow"] = "←", ["gets"] = "←", ["Leftarrow"] = "⇐",
        ["leftrightarrow"] = "↔", ["Leftrightarrow"] = "⇔", ["iff"] = "⇔", ["mapsto"] = "↦",
        ["uparrow"] = "↑", ["downarrow"] = "↓",
        ["angle"] = "∠", ["measuredangle"] = "∡", ["circ"] = "°", ["degree"] = "°", ["perp"] = "⊥", ["bot"] = "⊥",
        ["parallel"] = "∥", ["triangle"] = "△", ["square"] = "□", ["Box"] = "□",
        ["therefore"] = "∴", ["because"] = "∵",
        ["langle"] = "⟨", ["rangle"] = "⟩", ["lvert"] = "|", ["rvert"] = "|", ["vert"] = "|", ["mid"] = "|",
        ["lVert"] = "‖", ["rVert"] = "‖", ["Vert"] = "‖", ["lfloor"] = "⌊", ["rfloor"] = "⌋", ["lceil"] = "⌈", ["rceil"] = "⌉",
        ["colon"] = ":", ["prime"] = "′", ["ell"] = "ℓ", ["hbar"] = "ħ",
        ["in"] = "∈", ["notin"] = "∉", ["ni"] = "∋", ["subset"] = "⊂", ["subseteq"] = "⊆", ["supset"] = "⊃", ["supseteq"] = "⊇",
        ["cup"] = "∪", ["cap"] = "∩", ["setminus"] = "∖", ["emptyset"] = "∅", ["varnothing"] = "∅",
        ["forall"] = "∀", ["exists"] = "∃", ["neg"] = "¬", ["lnot"] = "¬", ["land"] = "∧", ["wedge"] = "∧", ["lor"] = "∨", ["vee"] = "∨",
        ["oplus"] = "⊕", ["otimes"] = "⊗",
        ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["iint"] = "∬", ["oint"] = "∮", ["partial"] = "∂", ["nabla"] = "∇",
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε", ["varepsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["rho"] = "ρ", ["varrho"] = "ρ",
        ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ",
        ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["cos"] = "cos", ["sin"] = "sin", ["tan"] = "tan", ["tg"] = "tg", ["cot"] = "cot", ["cotg"] = "cotg", ["ctg"] = "cotg",
        ["sec"] = "sec", ["csc"] = "csc", ["arccos"] = "arccos", ["arcsin"] = "arcsin", ["arctan"] = "arctan", ["arccot"] = "arccot",
        ["sinh"] = "sinh", ["cosh"] = "cosh", ["tanh"] = "tanh", ["ln"] = "ln", ["lg"] = "lg", ["log"] = "log",
        ["exp"] = "exp", ["lim"] = "lim", ["max"] = "max", ["min"] = "min", ["sup"] = "sup", ["inf"] = "inf",
        ["det"] = "det", ["gcd"] = "gcd", ["deg"] = "deg", ["dim"] = "dim", ["mod"] = "mod", ["bmod"] = "mod", ["pmod"] = "mod",
        ["quad"] = "  ", ["qquad"] = "   ",
        ["dots"] = "…", ["ldots"] = "…", ["cdots"] = "⋯", ["vdots"] = "⋮", ["ddots"] = "⋱",
    };

    // Commands that take an argument and are handled below. With the symbols, they decide whether a text has LaTeX.
    static readonly HashSet<string> Structural =
    [
        "vec", "overrightarrow", "vv", "frac", "dfrac", "tfrac", "cfrac", "sqrt", "hat", "widehat", "bar", "overline",
        "tilde", "widetilde", "dot", "ddot", "underline", "boxed", "begin", "end", "left", "right", "big", "Big", "bigg", "Bigg",
        "text", "textbf", "textit", "textrm", "mathbf", "mathrm", "mathit", "mathsf", "mathcal", "mathbb", "operatorname",
        "boldsymbol", "bm", "emph", "mbox", "displaystyle",
    ];

    static readonly Dictionary<char, char> Blackboard = new()
    {
        ['R'] = 'ℝ', ['N'] = 'ℕ', ['Z'] = 'ℤ', ['Q'] = 'ℚ', ['C'] = 'ℂ',
    };

    const string SuperFrom = "0123456789+-=()ni°", SuperTo = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿⁱ°";
    const string SubFrom = "0123456789+-=()aeoxijnmk", SubTo = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐₑₒₓᵢⱼₙₘₖ";

    // Stand-ins for escaped characters while the LaTeX ones are removed.
    const string OpenSet = "\uE000", CloseSet = "\uE001", Ampersand = "\uE002";

    public static string Readable(string text)
    {
        if (text.Length == 0) return text;

        // Paths are kept out of every change below ("D:\notes\pi" must stay as it is).
        var result = new StringBuilder();
        int at = 0;
        foreach (Match path in WindowsPath().Matches(text))
        {
            result.Append(Convert(text[at..path.Index]));
            result.Append(path.Value);
            at = path.Index + path.Length;
        }
        result.Append(Convert(text[at..]));

        string readable = TrailingSpaces().Replace(result.ToString(), "\n");
        return BlankLines().Replace(readable, "\n\n").Trim();
    }

    static string Convert(string text)
    {
        text = Markdown(text);
        bool math = HasLatex(text) || MathDollars().IsMatch(text) || PlainPower().IsMatch(text);
        if (!math) return text;

        text = DisplayDollars().Replace(text, match => match.Groups[1].Value);
        text = MathDollars().Replace(text, match => match.Groups[1].Value);
        text = LatexEnvironment().Replace(text, ""); // \begin{aligned} ... \end{aligned}: only the lines inside stay
        text = LineBreak().Replace(text, "\n"); // before \[ goes, since "\\[2pt]" contains it
        text = text.Replace(@"\(", "").Replace(@"\)", "").Replace(@"\[", "").Replace(@"\]", "");
        text = text.Replace(@"\,", " ").Replace(@"\;", " ").Replace(@"\:", " ").Replace(@"\!", "").Replace(@"\ ", " ")
                   .Replace(@"\{", OpenSet).Replace(@"\}", CloseSet).Replace(@"\|", "‖")
                   .Replace(@"\&", Ampersand).Replace(@"\%", "%").Replace(@"\$", "$").Replace(@"\#", "#").Replace(@"\_", "_");
        text = text.Replace("&", ""); // alignment marks: "u &= ..." → "u = ..."

        text = Symbol(text, keepUnknown: true);

        // Innermost first, a few rounds for nesting like \boxed{\frac{\sqrt{3}}{2}}.
        for (int round = 0; round < 6; round++)
        {
            string before = text;
            text = Wrapper().Replace(text, match => Unwrap(match.Groups[1].Value, Argument(match, 2)));
            text = Vector().Replace(text, match => Arrow(Argument(match, 1)));
            text = Accent().Replace(text, match => Accented(match.Groups[1].Value, Argument(match, 2)));
            text = Fraction().Replace(text, match => $"{Group(Argument(match, 1))}/{Group(Argument(match, 3))}");
            text = Root().Replace(text, match => RootOf(match.Groups[1].Value, Argument(match, 2)));
            text = SuperBraces().Replace(text, match => Script(match.Groups[1].Value, SuperFrom, SuperTo, "^"));
            text = SubBraces().Replace(text, match => Script(match.Groups[1].Value, SubFrom, SubTo, "_"));
            if (text == before) break;
        }
        text = SuperSingle().Replace(text, match => Script(match.Groups[1].Value, SuperFrom, SuperTo, "^"));
        text = SubSingle().Replace(text, match => Script(match.Groups[1].Value, SubFrom, SubTo, "_"));

        // Whatever command is left is formatting nobody needs to see (\displaystyle, \big, unknown ones).
        text = Symbol(text, keepUnknown: false);
        text = text.Replace("^°", "°").Replace("{", "").Replace("}", "");
        text = text.Replace(OpenSet, "{").Replace(CloseSet, "}").Replace(Ampersand, "&"); // \{1, 2\} was a set: its braces stay
        return Spaces().Replace(text, " ");
    }

    static bool HasLatex(string text) =>
        Command().Matches(text).Any(match => Symbols.ContainsKey(match.Groups[1].Value) || Structural.Contains(match.Groups[1].Value));

    // Known commands become their symbol; "\cos\theta" → "cos θ", not "cosθ" (but "\cos\left(" → "cos(").
    static string Symbol(string text, bool keepUnknown) => Command().Replace(text, match =>
    {
        if (!Symbols.TryGetValue(match.Groups[1].Value, out string? symbol)) return keepUnknown ? match.Value : "";
        bool word = symbol.Length > 1 && symbol.All(char.IsLetter);
        return word && WordFollows(text, match.Index + match.Length) ? symbol + " " : symbol;
    });

    static readonly HashSet<string> Invisible = ["left", "right", "big", "Big", "bigg", "Bigg", "displaystyle", "begin", "end"];

    static bool WordFollows(string text, int at)
    {
        if (at >= text.Length) return false;
        if (char.IsLetter(text[at])) return true;
        if (text[at] != '\\') return false;
        int end = at + 1;
        while (end < text.Length && char.IsAsciiLetter(text[end])) end++;
        string name = text[(at + 1)..end];
        if (Symbols.TryGetValue(name, out string? symbol)) return symbol.Length > 0 && char.IsLetter(symbol[0]);
        return Structural.Contains(name) && !Invisible.Contains(name);
    }

    // An argument is {braced} (group n) or a single character / number written straight after (group n + 1).
    static string Argument(Match match, int group) =>
        match.Groups[group].Success ? match.Groups[group].Value : match.Groups[group + 1].Value;

    static string Unwrap(string command, string content)
    {
        if (command == "mathbb" && content.Trim().Length == 1 && Blackboard.TryGetValue(content.Trim()[0], out char letter))
            return letter.ToString();
        return content;
    }

    // u⃗: the combining arrow goes over the last letter ("AB" → "AB⃗").
    static string Arrow(string name) => name.Trim() + "\u20D7";

    static string Accented(string accent, string content)
    {
        content = content.Trim();
        if (content.Length == 0) return "";
        return accent switch
        {
            "overline" => string.Concat(content.Select(ch => $"{ch}\u0305")), // a line over every letter: AB̅ → A̅B̅
            "underline" => content,
            "hat" or "widehat" => content + "\u0302",
            "bar" => content + "\u0304",
            "tilde" or "widetilde" => content + "\u0303",
            "ddot" => content + "\u0308",
            _ => content + "\u0307", // dot
        };
    }

    static string RootOf(string index, string content) => index.Trim() switch
    {
        "" or "2" => "√" + Group(content),
        "3" => "∛" + Group(content),
        "4" => "∜" + Group(content),
        _ => Script(index, SuperFrom, SuperTo, "^") + "√" + Group(content),
    };

    // A single number or letter needs no brackets in a fraction or root; anything longer does.
    static string Group(string part)
    {
        part = part.Trim();
        return SimpleTerm().IsMatch(part) ? part : $"({part})";
    }

    static string Script(string part, string from, string to, string mark)
    {
        part = part.Trim();
        if (part.All(ch => from.Contains(ch)))
            return new string(part.Select(ch => to[from.IndexOf(ch)]).ToArray());
        return part.Length == 1 ? mark + part : $"{mark}({part})";
    }

    // **bold**, `code`, "# heading", "---" rules, "* item" / "- item" → plain text with "• ".
    static string Markdown(string text)
    {
        text = Bold().Replace(text, match => match.Groups[1].Value);
        text = Code().Replace(text, match => match.Groups[1].Value);
        text = Heading().Replace(text, "");
        text = Rule().Replace(text, "");
        return Bullet().Replace(text, match => match.Groups[1].Value + "• ");
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\s""'<>|?*]*")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"\$\$(.+?)\$\$", RegexOptions.Singleline)]
    private static partial Regex DisplayDollars();

    // $...$ only when it holds maths, so "$5 and $10" stays money.
    [GeneratedRegex(@"\$([^$\n]*[\\^_=][^$\n]*)\$(?!\d)")]
    private static partial Regex MathDollars();

    [GeneratedRegex(@"\\(?:begin|end)\s*\{[A-Za-z*]*\}(?:\{[^{}]*\})?")]
    private static partial Regex LatexEnvironment();

    [GeneratedRegex(@"\\([A-Za-z]+)")]
    private static partial Regex Command();

    // "\\" ends a line; one newline after it is part of the same break.
    [GeneratedRegex(@"\\\\(?:\[[^\]]*\])?[ \t]*(?:\r?\n)?")]
    private static partial Regex LineBreak();

    // \mathbf{a}, and the short form \mathbf a.
    [GeneratedRegex(@"\\(text|textbf|textit|textrm|mathbf|mathrm|mathit|mathsf|mathcal|mathbb|operatorname|boldsymbol|bm|boxed|emph|mbox)\s*(?:\{([^{}]*)\}|([\p{L}\p{N}]))")]
    private static partial Regex Wrapper();

    [GeneratedRegex(@"\\(?:vec|overrightarrow|vv)\s*(?:\{([^{}]*)\}|([\p{L}\p{N}]))")]
    private static partial Regex Vector();

    [GeneratedRegex(@"\\(overline|underline|widehat|hat|bar|widetilde|tilde|ddot|dot)\s*(?:\{([^{}]*)\}|([\p{L}\p{N}]))")]
    private static partial Regex Accent();

    // \frac{a}{b}, and the short forms \frac12 and \frac{a}2.
    [GeneratedRegex(@"\\[dtc]?frac\s*(?:\{([^{}]*)\}|([\p{L}\p{N}]))\s*(?:\{([^{}]*)\}|([\p{L}\p{N}]))")]
    private static partial Regex Fraction();

    // \sqrt{x}, \sqrt[3]{x}, and the short forms \sqrt3, \sqrt 22 and \sqrt x.
    [GeneratedRegex(@"\\sqrt\s*(?:\[([^\]]*)\])?\s*(?:\{([^{}]*)\}|(\d+(?:[.,]\d+)?|\p{L}))")]
    private static partial Regex Root();

    [GeneratedRegex(@"\^\{([^{}]*)\}")]
    private static partial Regex SuperBraces();

    [GeneratedRegex(@"_\{([^{}]*)\}")]
    private static partial Regex SubBraces();

    // x^2, |a|^2 and a_1, but not "file_2025" or "2^10" (those need braces to count).
    [GeneratedRegex(@"(?<=[\p{L}\p{N}\)\|\u20D7])\^([\p{L}\p{N}°])(?![\p{L}\p{N}])")]
    private static partial Regex SuperSingle();

    [GeneratedRegex(@"(?<=[\p{L}\u20D7])_([\p{L}\p{N}])(?![\p{L}\p{N}])")]
    private static partial Regex SubSingle();

    [GeneratedRegex(@"^-?√?[\p{L}\p{N}.,]+\u20D7?$")]
    private static partial Regex SimpleTerm();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex Code();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[ \t]*(?:-{3,}|\*{3,}|_{3,})[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Rule();

    [GeneratedRegex(@"^(\s*)[*-]\s+", RegexOptions.Multiline)]
    private static partial Regex Bullet();

    // "|a|^2", "x^2", "(a+b)^2" written without LaTeX.
    [GeneratedRegex(@"[\p{L}\)\|]\^(?:\d|\{)")]
    private static partial Regex PlainPower();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[ \t]+\r?\n")]
    private static partial Regex TrailingSpaces();

    [GeneratedRegex(@"(?:\r?\n){3,}")]
    private static partial Regex BlankLines();
}
