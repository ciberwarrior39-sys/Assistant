namespace Assistant.Core.Search;

/// <summary>
/// Bulgarian Cyrillic and Latin letters, for file names typed in the other alphabet: "splavi" finds "Сплави.pptx" and
/// "доклад" finds "doklad.docx". Uses the official Bulgarian transliteration (щ = sht, ъ = a, ю = yu, я = ya).
/// </summary>
public static class Transliteration
{
    static readonly (string Cyrillic, string Latin)[] Letters =
    [
        ("щ", "sht"), ("ж", "zh"), ("ц", "ts"), ("ч", "ch"), ("ш", "sh"), ("ю", "yu"), ("я", "ya"),
        ("а", "a"), ("б", "b"), ("в", "v"), ("г", "g"), ("д", "d"), ("е", "e"), ("з", "z"), ("и", "i"), ("й", "y"),
        ("к", "k"), ("л", "l"), ("м", "m"), ("н", "n"), ("о", "o"), ("п", "p"), ("р", "r"), ("с", "s"), ("т", "t"),
        ("у", "u"), ("ф", "f"), ("х", "h"), ("ъ", "a"), ("ь", "y"),
    ];

    /// <summary>The word in the other alphabet, or null when it's mixed, has no letters to change, or isn't Bulgarian/Latin.</summary>
    public static string? Swap(string word)
    {
        string lower = word.ToLowerInvariant();
        bool cyrillic = lower.Any(ch => ch is >= 'а' and <= 'я');
        bool latin = lower.Any(ch => ch is >= 'a' and <= 'z');
        if (cyrillic == latin) return null;

        if (cyrillic)
        {
            var text = new System.Text.StringBuilder();
            foreach (char ch in lower)
                text.Append(Letters.FirstOrDefault(letter => letter.Cyrillic[0] == ch).Latin ?? ch.ToString());
            return text.ToString();
        }

        // Latin to Cyrillic: the longest letter group first, so "sht" becomes щ, not с-х-т.
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < lower.Length;)
        {
            var match = Letters
                .Where(letter => letter.Cyrillic != "ъ" && letter.Cyrillic != "ь") // "a" and "y" read as а and й
                .Where(letter => string.CompareOrdinal(lower, i, letter.Latin, 0, letter.Latin.Length) == 0)
                .OrderByDescending(letter => letter.Latin.Length)
                .FirstOrDefault();
            if (match.Latin is null)
            {
                result.Append(lower[i]);
                i++;
            }
            else
            {
                result.Append(match.Cyrillic);
                i += match.Latin.Length;
            }
        }
        return result.ToString();
    }
}
