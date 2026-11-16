using System.Text;

namespace Assistant.Core.Storage;

/// <summary>
/// Rewrites a file without ever leaving half of it behind: the new content goes to a temporary file next to it, which
/// then takes the old file's place in one step. A crash or power cut mid-write leaves the old file as it was.
/// For the user's own files that are rewritten whole (settings, notes, to-dos, reminders).
/// </summary>
public static class SafeFile
{
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, Utf8);
        File.Move(temp, path, overwrite: true);
    }

    public static void WriteAllLines(string path, IEnumerable<string> lines)
    {
        string temp = path + ".tmp";
        File.WriteAllLines(temp, lines, Utf8);
        File.Move(temp, path, overwrite: true);
    }
}
