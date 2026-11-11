using System.Text.RegularExpressions;

namespace Assistant.Core;

/// <summary>
/// Whether a message needs working out (maths, logic, code), so the cloud model may think longer and answer at
/// length instead of in a few quick sentences. English and Bulgarian. A miss only costs a quicker, shorter answer.
/// </summary>
public static partial class QuestionKind
{
    public static bool NeedsWorking(string message)
    {
        string text = message.ToLowerInvariant();
        return Sum().IsMatch(text) || Words().IsMatch(text) || text.Contains("```", StringComparison.Ordinal) || LooksLikeCode().IsMatch(message);
    }

    /// <summary>
    /// A real question (facts, explanations), not small talk: "who wrote…?", "кои са разказвачите…", "explain…".
    /// These get more thinking, so the model reads what it found instead of answering from memory.
    /// </summary>
    public static bool IsQuestion(string message)
    {
        string text = message.Trim().ToLowerInvariant();
        if (text.Length == 0 || SmallTalk().IsMatch(text)) return false;
        return text.Contains('?') || QuestionStart().IsMatch(text);
    }

    /// <summary>
    /// A question about the world that an encyclopedia could answer ("who wrote…?", "кога е роден Ботев?"), so it is
    /// worth looking up. Not small talk, maths or code, and not a question about the user or the assistant ("my", "I",
    /// "you"), one that leans on the chat before it ("is this…?", "why did he…?"), one about right now ("today",
    /// "what is … doing"), or a request for advice, ideas or writing ("should…", "how to…", "write…").
    /// A miss only costs the lookup: the answer still comes, and "Search the web" is under it.
    /// </summary>
    public static bool LooksUpFacts(string message)
    {
        if (!IsQuestion(message) || NeedsWorking(message)) return false;
        string text = WithoutLeadIn(message.Trim().ToLowerInvariant().Replace('’', '\''));
        if (text.Length == 0) return false;
        return !AboutUs().IsMatch(text) && !RightNow().IsMatch(text) && !Advice().IsMatch(text)
               && !(FollowUp().IsMatch(Opening(text)) && !IsItTrue().IsMatch(text));
    }

    // "can you tell me who wrote…", "кажи ми кой…": the polite start says nothing about the question.
    static string WithoutLeadIn(string text)
    {
        for (int round = 0; round < 3; round++)
        {
            Match lead = LeadIn().Match(text);
            if (!lead.Success || lead.Length == 0) break;
            text = text[lead.Length..];
        }
        return text;
    }

    // The first four words: a pronoun there ("is this…", "why did he…") points back at the chat, while one further on
    // usually has its name in the same question ("who was Napoleon and what were his reforms?").
    static string Opening(string text) => string.Join(' ', OpeningWord().Matches(text).Take(4).Select(match => match.Value));

    // "how are you", "how are you today", "what's up", "как си", "какво правиш": quick replies are fine.
    [GeneratedRegex(@"^(hi|hey|hello|yo|sup|thanks|thank\syou|ok|okay|cool|nice|lol|how\sare\syou|how\sare\syou\sdoing|how\sdo\syou\sdo|how's\sit\sgoing|how\sis\sit\sgoing|what's\sup|whats\sup|what's\snew|what\sare\syou\sdoing|are\syou\sthere|good\s(morning|afternoon|night|evening)|здрасти|здравей|привет|благодаря|мерси|ок|как\sси|как\sсте|как\sе|какво\sправиш|какво\sстава|какво\sново|добро\sутро|добър\sден|добър\sвечер|лека\sнощ)(\s(today|now|then|there|buddy|friend|my\sfriend|man|bro|днес|сега|бе|брат))*[\s!?.,]*$")]
    private static partial Regex SmallTalk();

    // Question words and requests for information, English and Bulgarian.
    [GeneratedRegex(@"^(who|whom|whose|what|when|where|which|why|how|is|are|was|were|do|does|did|can|could|should|would|will|has|have|tell\sme|explain|describe|list|name|compare|summari[sz]e|кой|коя|кое|кои|какво|какъв|каква|какви|кога|къде|защо|как|колко|чий|чия|чие|чии|дали|разкажи|обясни|опиши|кажи|изброй|сравни)\b")]
    private static partial Regex QuestionStart();

    // "12*7", "3x + 5 = 20", "2^10", "x²"
    [GeneratedRegex(@"\d\s*[-+*/^×÷=]\s*[\d(x-z]|[a-z]\s*[\^²³]|\b\d+\s*%\s*of\b")]
    private static partial Regex Sum();

    // Maths, logic and programming words; Bulgarian as word starts, so the endings match too.
    [GeneratedRegex("""
        \b(solve|calculate|compute|equation|integral|derivative|probability|prove|proof|formula|geometry|algebra
        |how\smany|how\smuch\sis|step\sby\sstep|explain\swhy|why\sdoes|what's\sthe\sdifference|difference\sbetween
        |code|coding|program|programming|function|method|class|variable|loop|array|bug|debug|error|exception|compile|compiler
        |python|c\#|c\+\+|java|javascript|typescript|html|css|sql|regex|unity|script|algorithm|api|json|recursion)(?![\w])
        |\b(реши|решение|изчисли|пресметни|уравнени|задача|задачи|докаж|вероятност|интеграл|производн|формул|геометри
        |защо|стъпка\sпо\sстъпка|разлика(та)?\sмежду|код|програм|функци|променлив|цикъл|масив|грешка|алгоритъм)
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Words();

    // A line of code pasted in: "int x = 5;", "def f(x):", "for (i = 0; …", "{ … }"
    [GeneratedRegex(@"[;{}]\s*$|^\s*(def|class|public|private|static|import|using|for|while|if|return|var|let|const|int|string|void)\b.*[(=:{]", RegexOptions.Multiline)]
    private static partial Regex LooksLikeCode();

    [GeneratedRegex("""
        ^(?:please|hey|ok|okay|so|and|well|моля|добре|а|и)[\s,]+
        |^(?:can|could|would|will)\s+you\s+(?:please\s+)?(?:tell|explain|describe|show|remind)\s+(?:to\s+)?(?:me\s+)?
        |^(?:tell|show|remind)\s+me\s+
        |^explain\s+to\s+me\s+
        |^what\s+do\s+you\s+know\s+about\s+
        |^do\s+you\s+know\s+
        |^какво\s+знаеш\s+за\s+
        |^i\s+(?:want|need|would\s+like|'d\s+like)\s+to\s+know\s+
        |^(?:кажи|разкажи|обясни|опиши|покажи)\s+ми\s+
        |^(?:можеш|може)\s+ли\s+да\s+(?:ми\s+)?(?:кажеш|разкажеш|обясниш|опишеш)\s+
        |^знаеш\s+ли\s+
        |^искам\s+да\s+(?:знам|разбера|науча)\s+
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LeadIn();

    // The user ("my exam", "should I", "аз", "ми") or the assistant ("you", "your", "ти", "можеш"). "I" only next to a
    // verb, so "World War I" isn't the user.
    [GeneratedRegex("""
        \b(?:my|mine|myself|me|i'm|i've|i'd|i'll|you|you're|your|yours|yourself)\b
        |\bi\s+(?:am|was|were|have|had|do|did|don't|didn't|can|can't|could|should|would|will|won't|want|need|like|love|hate|think|feel|know|live|work|study|use|used|get|got|go|went|make|made|said|say|told|tell|saw|see|just|really|also|still|never|always|keep|kept|put|left|buy|bought|play|played)\b
        |\b(?:am|do|did|don't|didn't|can|can't|could|should|shouldn't|would|will|have|haven't|was|must|may|shall)\s+i\b
        |\b(?:аз|мен|мене|ме|ми|мой|моя|моят|мое|моето|мои|моите)\b
        |\b(?:ти|теб|тебе|твой|твоя|твоят|твое|твоето|твои|твоите|вие|вас|ви|ваш|ваша|вашият|вашето|ваши|вашите)\b
        |\b(?:можеш|знаеш|мислиш|смяташ|харесваш|обичаш|искаш|правиш|казваш|помниш)\b
        |\b(?:какъв|каква|кой|коя|как)\s+си\b
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex AboutUs();

    // A word that points at something said earlier.
    [GeneratedRegex(@"\b(?:this|that|these|those|it|he|she|they|him|her|them|his|hers|their|theirs|its|това|този|тази|тези|онова|онзи|онази|онези|той|тя|те|него|нея|тях)\b")]
    private static partial Regex FollowUp();

    // "is it true that the Nile is the longest river?" stands on its own.
    [GeneratedRegex(@"^(?:is|was)\s+it\s+(?:true|correct|right|possible)\b|^вярно\s+ли\s+е\b|^истина\s+ли\s+е\b")]
    private static partial Regex IsItTrue();

    // Now, today, what someone is doing: an encyclopedia doesn't know.
    [GeneratedRegex(@"\b(?:doing|up\s+to|right\s+now|today|tonight|tomorrow|yesterday|this\s+(?:week|morning|afternoon|evening|weekend)|днес|утре|вчера|сега|довечера|в\s+момента|тази\s+(?:седмица|сутрин|вечер))\b")]
    private static partial Regex RightNow();

    // Advice, opinions, how-tos and things to write: the model's own job, not a lookup.
    [GeneratedRegex("""
        ^(?:should|shall|must)\b
        |^how\s+to\b
        |^what\s+(?:should|would|could)\b
        |^(?:which|what)\s+(?:is|are|'s)\s+(?:better|best|the\s+best)\b
        |\b(?:worth\s+it|a\s+good\s+idea|better\s+than)\b
        |^(?:write|draft|compose|make|create|generate|suggest|recommend|think\s+of|come\s+up|help|translate|rewrite|fix|improve|summari[sz]e|imagine|pretend|let's|lets)\b
        |^(?:напиши|направи|създай|измисли|предложи|препоръчай|помогни|преведи|поправи|подобри|обобщи|представи\s+си)(?![\p{L}])
        |^(?:трябва\s+ли|дали\s+да|как\s+да|какво\s+да|кое\s+е\s+по-добр)
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Advice();

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex OpeningWord();
}
