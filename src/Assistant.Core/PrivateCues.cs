using System.Text.RegularExpressions;
using Assistant.Core.Memory;
using Assistant.Core.Tools;

namespace Assistant.Core;

/// <summary>
/// Decides whether plain chat may go straight to the cloud. A message that mentions files, the screen, memory,
/// activity, notes, tasks, reminders or past conversations, or that touches something the user saved, goes to the
/// local model first, as before. The list leans towards "local": a wrong "local" only costs speed, a wrong "cloud"
/// sends a message out without the local model looking at it first.
/// </summary>
public static partial class PrivateCues
{
    public static bool LooksLocal(string message, MemoryStore? memory)
    {
        string text = message.ToLowerInvariant().Replace('’', '\'');
        if (Cues().IsMatch(text) || WindowsPath().IsMatch(message) || FileName().IsMatch(text)) return true;

        if (memory is null) return false;
        if (RememberTool.IsRequest(message) || ForgetTool.IsRequest(message) || RecallTool.ShouldLookUp(message, memory)) return true;

        // "my" with a word from a saved fact ("how is my sister's exam going?" when a fact mentions her).
        if (!Mine().IsMatch(text)) return false;
        string[] words = MemoryStore.Words(message).Where(word => word.Length >= 4).ToArray();
        return words.Length > 0
               && memory.AllFacts().Any(fact => MemoryStore.Overlap(words, MemoryStore.Words(fact.Text).Where(word => word.Length >= 4).ToArray()) > 0);
    }

    // Files and folders, the screen, memory, activity, notes and tasks, conversations. English as whole words;
    // Bulgarian as word starts, so the endings ("файла", "файловете") match too. (Spaces are written \s: this
    // pattern mode ignores plain spaces.)
    [GeneratedRegex("""
        \b(files?|folders?|documents?|docx|pdfs?|xlsx|pptx|spreadsheets?|presentations?|downloads|desktop|screenshots?
        |screen|this\s(error|window|message|page|tab)|look\sat|looking\sat
        |remember|forget|what\sdo\syou\sknow|did\si\stell\syou|i\stold\syou|my\sname|who\sam\si
        |what\s(did|have|was)\si|how\s(long|much\stime)\sdid\si|worked\son|working\son|spent|screen\stime|my\sactivity
        |notes?|to-?do|my\s(list|tasks?)|remind|reminders?|timers?
        |talked\sabout|what\s(did|have)\swe|we\s(talked|spoke|discussed)|our\s(chat|conversation)|last\stime\swe
        |cv|resume)\b
        |\b(файл|папк|документ|сваля|изтегл|работния\sплот|екран|снимк|погледни|тази\sгрешка
        |запомни|забрави|какво\sзнаеш|казах\sти|името\sми|как\sсе\sказвам|кой\sсъм
        |какво\s(правих|съм\sправил|съм\sправила)|работих|прекарах
        |бележк|задач|списък|напомн|таймер|говорихме|разговаряхме)
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Cues();

    [GeneratedRegex(@"[A-Za-z]:\\")]
    private static partial Regex WindowsPath();

    // "report.docx", "song.mp3": a file name.
    [GeneratedRegex(@"\b[\w-]+\.(docx?|xlsx?|pptx?|pdf|txt|csv|png|jpe?g|gif|mp[34]|wav|zip|rar|7z|exe|msi|cs|py|js|html?|json|md|unity|blend|psd)\b")]
    private static partial Regex FileName();

    [GeneratedRegex(@"\b(my|mine|ми|моя|моят|моите|моето|мой)\b")]
    private static partial Regex Mine();
}
