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

    // "how are you", "what's up", "как си", "какво правиш": quick replies are fine.
    [GeneratedRegex(@"^(hi|hey|hello|yo|thanks|thank\syou|ok|okay|cool|nice|lol|how\sare\syou|how's\sit\sgoing|what's\sup|whats\sup|what\sare\syou\sdoing|are\syou\sthere|good\s(morning|night|evening)|здрасти|здравей|благодаря|мерси|ок|как\sси|как\sсте|какво\sправиш|какво\sстава|добро\sутро|лека\sнощ)[\s!?.,]*$")]
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
}
