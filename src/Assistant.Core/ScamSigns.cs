using System.Text.RegularExpressions;

namespace Assistant.Core;

/// <summary>What the code found in a message: the warning signs (plain words) and where its links really go.</summary>
public sealed record ScamReport(IReadOnlyList<string> Signs, IReadOnlyList<string> LinkHosts)
{
    /// <summary>The first reply: what the code found, before any model has looked.</summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            Signs.Count switch
            {
                0 => "I didn't find the usual scam signs in the text itself. That doesn't prove it's safe: some scams are written well.",
                1 => "One sign scammers often use:",
                _ => $"{Signs.Count} signs scammers often use:",
            },
        };
        lines.AddRange(Signs.Select(sign => "- " + sign));
        if (LinkHosts.Count > 0)
            lines.Add($"Its link{(LinkHosts.Count == 1 ? " goes" : "s go")} to: {string.Join(", ", LinkHosts)}.");
        return string.Join("\n", lines);
    }

    /// <summary>The same for a model, as part of the request (shown to the user before anything is sent).</summary>
    public string ForModel() =>
        Signs.Count == 0
            ? "A checker found none of the common warning signs in it."
            : "Warning signs a checker found in it: " + string.Join(" ", Signs);
}

/// <summary>
/// "Is this a scam?": the warning signs scammers' messages share, found by code (English and Bulgarian), on this
/// computer. Not a verdict on its own: a model weighs them with the text, and the user always gets <see cref="Advice"/>.
/// Signs: hurry or threats, asking for passwords, codes or card details, a small fee to release a parcel, an unexpected
/// prize or refund, payment by gift card, crypto or money transfer, a remote-control app, "new number" from family, a request for money,
/// "verify your account", guaranteed profit; and links that pretend to be a brand, hide behind a link shortener,
/// use a bare number or look-alike letters, or end in an address ending common in scams.
/// </summary>
public static partial class ScamSigns
{
    /// <summary>The same advice under every verdict, from code rather than a model, so it's always there.</summary>
    public const string Advice =
        "When unsure: don't click links, don't reply and don't pay. Open the company's own app or type its website " +
        "yourself, or call the number on your card or bill. A real company never asks for passwords or codes by message.";

    const int MaxSigns = 6;
    const int MaxQuote = 40;

    static readonly (Regex Pattern, string Sign)[] TextSigns =
    [
        (Hurry(), "It rushes you or threatens a block{0}. Scammers hurry you so you don't stop to think."),
        (Secrets(), "It asks for a password, code or card details{0}. Real companies never ask for these by message."),
        (Fee(), "It wants a small fee or payment to release a parcel{0}: a common delivery scam."),
        (Prize(), "It promises a prize, refund or money you weren't expecting{0}."),
        (OddPayment(), "It mentions gift cards, crypto or a money transfer{0}: payments that can't be reversed."),
        (RemoteControl(), "It mentions a remote-control app{0}, which would let a stranger use your PC."),
        (NewNumber(), "Someone says they have a new number{0}. If they ask for money, call their old number first."),
        (MoneyAsk(), "It asks you to send money{0}."),
        (VerifyDetails(), "It asks you to verify or update your account or payment details{0}, usually through a link."),
        (EasyMoney(), "It promises guaranteed or easy money{0}."),
    ];

    // Brands scammers pretend to be, by the word in an address, and their own sites.
    static readonly (string Name, string[] Words, string[] Sites)[] Brands =
    [
        ("PayPal", ["paypal"], ["paypal.com", "paypal.me"]),
        ("Amazon", ["amazon"], ["amazon.com", "amazon.de", "amazon.co.uk", "amazon.fr", "amazon.it", "amazon.es", "amazon.nl", "amazon.pl", "amazon.se", "amazonaws.com", "amazon.jobs"]),
        ("Apple", ["apple", "icloud", "itunes"], ["apple.com", "icloud.com", "apple.news", "itunes.com"]),
        ("Microsoft", ["microsoft", "outlook", "onedrive", "xbox", "office365", "hotmail"], ["microsoft.com", "live.com", "office.com", "outlook.com", "office365.com", "onedrive.com", "xbox.com", "microsoftonline.com", "sharepoint.com", "hotmail.com"]),
        ("Google", ["google", "gmail"], ["google.com", "google.bg", "gmail.com", "googleusercontent.com", "googleapis.com"]),
        ("Netflix", ["netflix"], ["netflix.com"]),
        ("DHL", ["dhl"], ["dhl.com", "dhl.de", "dhl.bg"]),
        ("FedEx", ["fedex"], ["fedex.com"]),
        ("UPS", ["ups"], ["ups.com"]),
        ("Econt", ["econt"], ["econt.com", "econt.bg"]),
        ("Speedy", ["speedy"], ["speedy.bg"]),
        ("Bulgarian Posts", ["bgpost", "bulpost"], ["bgpost.bg"]),
        ("DSK Bank", ["dsk", "dskbank", "dskdirect"], ["dskbank.bg", "dskdirect.bg"]),
        ("UniCredit", ["unicredit", "bulbank"], ["unicreditbulbank.bg", "unicredit.it", "unicreditgroup.eu"]),
        ("Fibank", ["fibank"], ["fibank.bg"]),
        ("Postbank", ["postbank"], ["postbank.bg"]),
        ("Revolut", ["revolut"], ["revolut.com", "revolut.me"]),
        ("Facebook", ["facebook", "meta"], ["facebook.com", "fb.com", "fb.me", "facebookmail.com", "meta.com"]),
        ("Instagram", ["instagram"], ["instagram.com"]),
        ("WhatsApp", ["whatsapp"], ["whatsapp.com", "whatsapp.net", "wa.me"]),
        ("Steam", ["steam", "steamcommunity", "steampowered"], ["steampowered.com", "steamcommunity.com", "steamstatic.com", "steam.tv"]),
        ("Discord", ["discord"], ["discord.com", "discord.gg", "discordapp.com", "discord.media"]),
        ("Roblox", ["roblox"], ["roblox.com"]),
        ("Epic Games", ["epicgames"], ["epicgames.com"]),
        ("Binance", ["binance"], ["binance.com"]),
        ("Coinbase", ["coinbase"], ["coinbase.com"]),
        ("eBay", ["ebay"], ["ebay.com", "ebay.de", "ebay.co.uk"]),
        ("OLX", ["olx"], ["olx.bg", "olx.pl", "olx.ro"]),
        ("A1", ["a1"], ["a1.bg"]),
        ("Yettel", ["yettel"], ["yettel.bg"]),
        ("Vivacom", ["vivacom"], ["vivacom.bg"]),
        ("the tax agency (НАП)", ["nra", "nap"], ["nra.bg"]),
    ];

    static readonly HashSet<string> Shorteners = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit.ly", "tinyurl.com", "t.co", "cutt.ly", "is.gd", "rb.gy", "shorturl.at", "tiny.cc", "ow.ly", "s.id", "t.ly",
        "rebrand.ly", "bl.ink", "goo.su", "clck.ru", "u.to", "v.gd", "shorturl.com", "tinyurl.ru",
    };

    // Address endings that are cheap and common in scam links (not proof on their own).
    static readonly HashSet<string> RiskyEndings = new(StringComparer.OrdinalIgnoreCase)
    {
        "top", "xyz", "click", "icu", "cam", "sbs", "rest", "gq", "tk", "ml", "cf", "ga", "zip", "mov", "cfd", "buzz",
        "monster", "lol", "quest", "cyou", "bond", "vip", "work",
    };

    // Second-level endings, so "amazon.co.uk" counts as amazon's own.
    static readonly HashSet<string> TwoPartEndings = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "com.au", "co.jp", "com.br", "com.tr", "co.in", "com.mx",
    };

    public static ScamReport Check(string text)
    {
        string lower = text.ToLowerInvariant();
        var signs = new List<string>();

        foreach ((Regex pattern, string sign) in TextSigns)
        {
            Match match = pattern.Match(lower);
            if (!match.Success) continue;
            // The words as written, when lowercasing kept the length (it does for Latin and Cyrillic).
            string quote = lower.Length == text.Length ? text.Substring(match.Index, match.Length) : match.Value;
            quote = quote.Trim();
            signs.Add(string.Format(sign, quote.Length is > 0 and <= MaxQuote ? $" (\"{quote}\")" : ""));
        }

        var hosts = new List<string>();
        foreach (Match link in Link().Matches(lower))
        {
            if (Host(link.Value) is not { } host || hosts.Contains(host)) continue;
            hosts.Add(host);
            signs.AddRange(LinkSigns(host, link.Value));
        }

        return new ScamReport(signs.Distinct().Take(MaxSigns).ToList(), hosts.Take(5).ToList());
    }

    static IEnumerable<string> LinkSigns(string host, string link)
    {
        string site = Site(host);
        string[] parts = host.Split('.', '-');

        foreach ((string name, string[] words, string[] sites) in Brands)
        {
            bool named = parts.Any(part => words.Any(word => word.Length <= 4 ? part == word : part.StartsWith(word, StringComparison.Ordinal)));
            if (named && !sites.Contains(site, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"A link has \"{name}\" in it but goes to {host}, which isn't {name}'s own site.";
                break;
            }
        }
        if (Shorteners.Contains(site)) yield return $"A shortened link ({site}) hides where it really goes.";
        if (IpAddress().IsMatch(host)) yield return "A link goes to a bare number address instead of a website name.";
        if (host.Contains("xn--", StringComparison.Ordinal)) yield return $"A link ({host}) uses look-alike letters that copy a real address.";
        if (RiskyEndings.Contains(host[(host.LastIndexOf('.') + 1)..])) yield return $"A link ends in .{host[(host.LastIndexOf('.') + 1)..]}, an address ending that's cheap and common in scams.";
        if (link.Contains('@') && !link.StartsWith("mailto", StringComparison.Ordinal) && Authority(link).Contains('@'))
            yield return "A link hides its real address after an @ sign.";
    }

    // "https://paypal.com@evil.top/x" → "evil.top"; "www.dhl-paket.top/abc" → "dhl-paket.top".
    static string? Host(string link)
    {
        string authority = Authority(link);
        int at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        int colon = authority.IndexOf(':');
        if (colon >= 0) authority = authority[..colon];
        authority = authority.Trim('.');
        if (authority.StartsWith("www.", StringComparison.Ordinal)) authority = authority[4..];
        return authority.Contains('.') ? authority : null;
    }

    static string Authority(string link)
    {
        string rest = Scheme().Replace(link, "");
        int end = rest.IndexOfAny(['/', '?', '#']);
        return end >= 0 ? rest[..end] : rest;
    }

    // The part of a host that's registered: "login.paypal.com" → "paypal.com", "amazon.co.uk" stays.
    static string Site(string host)
    {
        string[] labels = host.Split('.');
        if (labels.Length <= 2) return host;
        string lastTwo = labels[^2] + "." + labels[^1];
        return TwoPartEndings.Contains(lastTwo) ? labels[^3] + "." + lastTwo : lastTwo;
    }

    [GeneratedRegex("""
        \b(?:within\s+(?:12|24|48|72)\s+hours|immediately|urgent(?:ly)?|act\s+now|final\s+(?:notice|warning|reminder)|last\s+(?:chance|warning)
        |(?:account|card|profile|access|subscription)\s+(?:has\s+been\s+|will\s+be\s+|is\s+(?:being\s+)?)?(?:suspended|locked|blocked|closed|disabled|restricted|deactivated|terminated)
        |suspicious\s+(?:activity|login|sign-in))\b
        |спешно|незабавно|в\s+рамките\s+на\s+\d+\s+часа|последно\s+(?:предупреждение|напомняне)|подозрителна\s+активност
        |(?:профил|акаунт|карта|сметка|абонамент)\w*\s+(?:\S+\s+){0,3}?(?:блокира|спря|закри|ограничен|деактивира)\w*
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Hurry();

    [GeneratedRegex("""
        \b(?:password|passcode|pin\s+code|one[-\s]time\s+(?:code|password)|verification\s+code|security\s+code|otp|cvv|cvc
        |card\s+(?:number|details)|login\s+details|seed\s+phrase|recovery\s+phrase)\b
        |парол\w*|пин\s*-?\s*код\w*|\bпин\b|еднократ\w+\s+код\w*|код\w*\s+(?:за|от)\s+(?:потвърждение|достъп|sms|смс)
        |(?:данни|номер)\w*\s+(?:на|за)\s+(?:вашата\s+|твоята\s+)?карта\w*
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Secrets();

    [GeneratedRegex("""
        \b(?:customs|delivery|shipping|redelivery|handling|release|clearance)\s+(?:fee|charge|payment|cost)
        |\bunpaid\s+(?:shipping|delivery|fee|toll|invoice)
        |\b(?:parcel|package|shipment)\s+(?:is\s+|has\s+been\s+)?(?:on\s+hold|held|waiting|undeliverable|suspended|could\s+not\s+be\s+delivered)
        |\btoll\s+(?:payment|balance)\b
        |митническ\w+\s+такс\w*|\bмито\b|такса\s+за\s+(?:доставка|митническо|освобождаване|пренасочване|съхранение)
        |неплатен\w*\s+(?:такса|доставка|сума)|пратка\w*\s+(?:\S+\s+){0,3}?(?:задържан\w*|не\s+може\s+да\s+бъде\s+доставена|очаква\s+плащане)
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Fee();

    [GeneratedRegex("""
        \b(?:you(?:'ve|\s+have)?\s+(?:won|been\s+selected)|winner|claim\s+(?:your\s+)?(?:prize|reward|refund|gift)
        |free\s+(?:iphone|gift|prize)|tax\s+refund|you\s+are\s+entitled\s+to)\b
        |спечели\w*|печелите|печеливш\w*|полага\w*\s+(?:ви\s+)?(?:се\s+)?(?:сума|възстановяване|обезщетение)
        |възстановяване\s+на\s+(?:данък|сума|такса)
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Prize();

    [GeneratedRegex(@"\b(?:gift\s*cards?|steam\s+cards?|google\s+play\s+cards?|itunes\s+cards?|bitcoin|btc|usdt|crypto(?:currency)?|western\s+union|moneygram|wire\s+transfer)\b|подаръчн\w+\s+карт\w*|ваучер\w*|биткойн\w*|крипто\w*")]
    private static partial Regex OddPayment();

    [GeneratedRegex(@"\b(?:anydesk|teamviewer|rustdesk|quick\s*assist|ultraviewer|supremo|remote\s+access)\b")]
    private static partial Regex RemoteControl();

    [GeneratedRegex(@"\b(?:new\s+number|my\s+phone\s+(?:broke|is\s+broken)|lost\s+my\s+phone)\b|\bнов(?:ия)?\s+(?:ми\s+)?номер\b|телефонът\s+ми\s+(?:се\s+)?(?:счупи|развали)|загубих\s+(?:си\s+)?телефона")]
    private static partial Regex NewNumber();

    [GeneratedRegex(@"\b(?:send|transfer|lend|wire)\s+(?:me\s+|us\s+)?(?:\d[\d\s,.]*\s*)?(?:euros?|eur|€|\$|dollars|pounds|money|leva|лв)(?!\w)|(?:прати|изпрати|преведи|дай|заеми)\s+(?:ми\s+|ни\s+)?(?:\d[\d\s,.]*\s*)?(?:лв|лева|евро|пари)(?!\w)")]
    private static partial Regex MoneyAsk();

    [GeneratedRegex("""
        \b(?:verify|confirm|update|validate|reactivate|unlock)\s+(?:your\s+)?(?:account|identity|details|information|info|payment|billing|card|profile|login)\b
        |потвърд\w+\s+(?:вашите\s+|своите\s+|си\s+)?(?:данни|самоличност\w*|профил\w*|акаунт\w*|плащане\w*)
        |актуализира\w+\s+(?:вашите\s+|своите\s+|си\s+)?(?:данни|информация\w*|профил\w*)
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex VerifyDetails();

    [GeneratedRegex(@"\b(?:guaranteed\s+(?:profit|returns?|income)|double\s+your\s+(?:money|investment)|earn\s+\$?\d+[\s,]*(?:per|a)\s+(?:day|hour)|risk[-\s]free\s+(?:investment|profit)|passive\s+income)\b|гарантиран\w*\s+(?:печалба|доход|възвръщаемост)|печели\w*\s+\d+\s*(?:лв|евро|€|\$)\w*\s+(?:на|за)\s+(?:ден|час)")]
    private static partial Regex EasyMoney();

    // Links: with a scheme or www., or a bare address with a common ending ("dhl-paket.top/abc"). Bare ".zip" and
    // ".mov" are left out: those are far more often file names than addresses.
    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""')\]]+|\b[a-z0-9][a-z0-9.-]*\.(?:com|net|org|info|bg|eu|top|xyz|click|icu|cam|sbs|rest|gq|tk|ml|cf|ga|cfd|buzz|monster|lol|quest|shop|online|site|live|link|co|io|me|app|ly|gd|at|id|to|vip|work|cyou|bond)\b(?:/[^\s<>""')\]]*)?")]
    private static partial Regex Link();

    [GeneratedRegex(@"^https?://", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();

    [GeneratedRegex(@"^\d{1,3}(?:\.\d{1,3}){3}$")]
    private static partial Regex IpAddress();
}
