using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Core;

/// <summary>
/// Makes model output readable in the chat bubble, which shows plain text: LaTeX becomes symbols
/// ("\vec{u} \cdot \vec{v}" → "u⃗ · v⃗", "\frac{1}{2}" → "1/2", "x^2" → "x²") and markdown marks are dropped
/// ("**bold**" → "bold", "* item" → "• item"). File paths are left exactly as they are.
/// </summary>
public static partial class MathText
{
    static readonly Dictionary<string, string> Symbols = new()
    {
        ["cdot"] = "·", ["times"] = "×", ["div"] = "÷", ["pm"] = "±", ["mp"] = "∓",
        ["leq"] = "≤", ["le"] = "≤", ["geq"] = "≥", ["ge"] = "≥", ["neq"] = "≠", ["ne"] = "≠",
        ["approx"] = "≈", ["equiv"] = "≡", ["infty"] = "∞", ["propto"] = "∝",
        ["rightarrow"] = "→", ["to"] = "→", ["Rightarrow"] = "⇒", ["implies"] = "⇒", ["leftarrow"] = "←",
        ["Leftrightarrow"] = "⇔", ["iff"] = "⇔", ["mapsto"] = "↦",
        ["angle"] = "∠", ["circ"] = "°", ["degree"] = "°", ["perp"] = "⊥", ["parallel"] = "∥",
        ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["cup"] = "∪", ["cap"] = "∩", ["emptyset"] = "∅",
        ["forall"] = "∀", ["exists"] = "∃", ["neg"] = "¬", ["land"] = "∧", ["lor"] = "∨",
        ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["partial"] = "∂", ["nabla"] = "∇",
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε", ["varepsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["rho"] = "ρ", ["sigma"] = "σ",
        ["tau"] = "τ", ["phi"] = "φ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Pi"] = "Π", ["Sigma"] = "Σ",
        ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["cos"] = "cos", ["sin"] = "sin", ["tan"] = "tan", ["cot"] = "cot", ["sec"] = "sec", ["csc"] = "csc",
        ["arccos"] = "arccos", ["arcsin"] = "arcsin", ["arctan"] = "arctan", ["ln"] = "ln", ["log"] = "log",
        ["exp"] = "exp", ["lim"] = "lim", ["max"] = "max", ["min"] = "min", ["deg"] = "deg",
        ["quad"] = "  ", ["qquad"] = "   ", ["left"] = "", ["right"] = "", ["displaystyle"] = "",
        ["dots"] = "…", ["ldots"] = "…", ["cdots"] = "⋯",
    };

    // Stand-ins for escaped braces while the grouping braces are removed.
    const string OpenSet = "\uE000", CloseSet = "\uE001";

    const string SuperFrom = "0123456789+-=()ni°", SuperTo = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿⁱ°";
    const string SubFrom = "0123456789+-=()aeoxijnmk", SubTo = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₐₑₒₓᵢⱼₙₘₖ";

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
        return result.ToString().Trim();
    }

    static string Convert(string text)
    {
        text = Markdown(text);
        bool math = LatexCommand().IsMatch(text) || MathDollars().IsMatch(text) || PlainPower().IsMatch(text);
        if (!math) return text;

        text = DisplayDollars().Replace(text, match => match.Groups[1].Value);
        text = MathDollars().Replace(text, match => match.Groups[1].Value);
        text = text.Replace(@"\(", "").Replace(@"\)", "").Replace(@"\[", "").Replace(@"\]", "").Replace(@"\\", "\n");
        text = text.Replace(@"\,", " ").Replace(@"\;", " ").Replace(@"\!", "").Replace(@"\ ", " ")
                   .Replace(@"\{", OpenSet).Replace(@"\}", CloseSet).Replace(@"\|", "‖");

        text = Wrapper().Replace(text, match => match.Groups[1].Value); // \text{...}, \mathbf{...}
        string commands = text;
        text = Command().Replace(text, match =>
        {
            if (!Symbols.TryGetValue(match.Groups[1].Value, out string? symbol)) return match.Value;
            // "\cos\theta" → "cos θ", not "cosθ"
            int next = match.Index + match.Length;
            bool joined = symbol.Length > 1 && symbol.All(char.IsLetter) && next < commands.Length
                          && (char.IsLetter(commands[next]) || commands[next] == '\\');
            return joined ? symbol + " " : symbol;
        });

        // Innermost first, a few rounds for nesting like \frac{\sqrt{3}}{2}.
        for (int round = 0; round < 4; round++)
        {
            string before = text;
            text = Vector().Replace(text, match => Arrow(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value));
            text = Fraction().Replace(text, match => $"{Group(match.Groups[1].Value)}/{Group(match.Groups[2].Value)}");
            text = Root().Replace(text, match => "√" + Group(match.Groups[1].Value));
            text = SuperBraces().Replace(text, match => Script(match.Groups[1].Value, SuperFrom, SuperTo, "^"));
            text = SubBraces().Replace(text, match => Script(match.Groups[1].Value, SubFrom, SubTo, "_"));
            if (text == before) break;
        }
        text = SuperSingle().Replace(text, match => Script(match.Groups[1].Value, SuperFrom, SuperTo, "^"));
        text = SubSingle().Replace(text, match => Script(match.Groups[1].Value, SubFrom, SubTo, "_"));
        text = text.Replace("^°", "°").Replace("{", "").Replace("}", "");
        text = text.Replace(OpenSet, "{").Replace(CloseSet, "}"); // \{1, 2\} was a set: its braces stay
        return Spaces().Replace(text, " ");
    }

    // **bold**, `code`, "# heading", "* item" / "- item" → plain text with "• ".
    static string Markdown(string text)
    {
        text = Bold().Replace(text, match => match.Groups[1].Value);
        text = Code().Replace(text, match => match.Groups[1].Value);
        text = Heading().Replace(text, "");
        return Bullet().Replace(text, match => match.Groups[1].Value + "• ");
    }

    // u⃗: the combining arrow goes over the last letter ("AB" → "AB⃗").
    static string Arrow(string name) => name.Trim() + "\u20D7";

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

    [GeneratedRegex(@"[A-Za-z]:\\[^\s""'<>|?*]*")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"\\(vec|frac|sqrt|cdot|times|theta|alpha|beta|pi|angle|circ|text|mathbf|mathrm|left|right|cos|sin|leq|geq|neq|approx|overrightarrow|infty|sum|int)\b")]
    private static partial Regex LatexCommand();

    [GeneratedRegex(@"\$\$(.+?)\$\$", RegexOptions.Singleline)]
    private static partial Regex DisplayDollars();

    // $...$ only when it holds maths, so "$5 and $10" stays money.
    [GeneratedRegex(@"\$([^$\n]*[\\^_=][^$\n]*)\$(?!\d)")]
    private static partial Regex MathDollars();

    [GeneratedRegex(@"\\(?:text|textbf|textit|mathbf|mathrm|mathit|operatorname|boldsymbol)\{([^{}]*)\}")]
    private static partial Regex Wrapper();

    [GeneratedRegex(@"\\([A-Za-z]+)")]
    private static partial Regex Command();

    [GeneratedRegex(@"\\(?:vec|overrightarrow|vv)\s*(?:\{([^{}]*)\}|([A-Za-z]))")]
    private static partial Regex Vector();

    [GeneratedRegex(@"\\[dt]?frac\s*\{([^{}]*)\}\s*\{([^{}]*)\}")]
    private static partial Regex Fraction();

    [GeneratedRegex(@"\\sqrt\s*\{([^{}]*)\}")]
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

    [GeneratedRegex(@"^(\s*)[*-]\s+", RegexOptions.Multiline)]
    private static partial Regex Bullet();

    // "|a|^2", "x^2", "(a+b)^2" written without LaTeX.
    [GeneratedRegex(@"[\p{L}\)\|]\^(?:\d|\{)")]
    private static partial Regex PlainPower();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();
}
