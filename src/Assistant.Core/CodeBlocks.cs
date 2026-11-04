using System.Text.RegularExpressions;

namespace Assistant.Core;

/// <summary>A piece of a reply: prose, or a code block with its language ("python", "csharp", or empty).</summary>
public sealed record ReplyPart(bool IsCode, string Language, string Text);

/// <summary>
/// Splits a reply into prose and fenced code blocks (```python … ```), so code keeps its exact characters and layout
/// (the maths and markdown cleanup only touches prose) and the chat can show it in its own block with a Copy button.
/// A block cut off at the end of a reply (no closing fence) still counts as code.
/// </summary>
public static partial class CodeBlocks
{
    public static IReadOnlyList<ReplyPart> Split(string text)
    {
        var parts = new List<ReplyPart>();
        int at = 0;
        foreach (Match fence in Fence().Matches(text))
        {
            AddProse(parts, text[at..fence.Index]);
            string code = fence.Groups["code"].Value.TrimEnd('\r', '\n');
            if (code.Trim().Length > 0) parts.Add(new ReplyPart(true, fence.Groups["lang"].Value.Trim().ToLowerInvariant(), code));
            at = fence.Index + fence.Length;
        }
        AddProse(parts, text[at..]);
        return parts;
    }

    /// <summary>Puts parts back together, with each code block fenced on its own lines.</summary>
    public static string Join(IEnumerable<ReplyPart> parts) =>
        string.Join("\n\n", parts.Select(part => part.IsCode ? $"```{part.Language}\n{part.Text}\n```" : part.Text)
                                 .Where(text => text.Length > 0)).Trim();

    public static bool HasCode(string text) => Fence().IsMatch(text);

    static void AddProse(List<ReplyPart> parts, string text)
    {
        string trimmed = text.Trim('\r', '\n');
        if (trimmed.Trim().Length > 0) parts.Add(new ReplyPart(false, "", trimmed));
    }

    // ``` or ~~~, an optional language, a line break, the code, then the closing fence (or the end of the reply).
    [GeneratedRegex(@"(?<open>```|~~~)[ \t]*(?<lang>[\w#+.\-]*)[^\n]*\r?\n(?<code>.*?)(?:\k<open>|\z)", RegexOptions.Singleline)]
    private static partial Regex Fence();
}
