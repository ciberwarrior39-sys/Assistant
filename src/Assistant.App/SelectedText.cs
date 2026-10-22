using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Assistant.Core;

namespace Assistant.App;

/// <summary>Text the user marked (or, failing that, last copied), and where it came from.</summary>
public sealed record PickedText(string Text, string Source);

/// <summary>
/// "Help with selected text": reads the text marked in the window the user was using, through Windows' accessibility
/// interface (UI Automation), the way screen readers do. No key presses are simulated and the clipboard is never
/// changed. Apps that don't share their selection (some Chrome pages, Electron apps like Discord) fall back to the
/// last copied text, and the card says so, so the user sees what was taken.
/// </summary>
public static class SelectedText
{
    // A big web page can take a while to search; past this, the copied text is used instead.
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    const int MaxAreas = 300;

    /// <summary>The text, or null with the reason to show.</summary>
    public static async Task<(PickedText? Text, string Problem)> PickAsync(WindowCapture windows)
    {
        TextSource source = windows.TextTarget();
        if (source.Problem is null)
        {
            // UI Automation runs off the UI thread: it may wait on the other app, and must never wait on our own.
            Task<string?> read = Task.Run(() => FromAutomation(source.Window));
            if (await Task.WhenAny(read, Task.Delay(ReadTimeout)) == read && await read is { } marked)
                return (new PickedText(marked, $"Marked in {source.Name}"), "");
        }
        else if (!source.ClipboardAllowed)
        {
            return (null, source.Problem);
        }

        if (Copied() is { } copied)
        {
            string from = source.Problem is null
                ? $"What you copied last ({source.Name} didn't share the marked text)"
                : "What you copied last";
            return (new PickedText(copied, from), "");
        }
        return (null, "I couldn't find any marked text. Mark it with the mouse, then right-click me and choose " +
                      "\"Help with selected text\". If that app still doesn't share it, also press Ctrl+C after marking.");
    }

    static string? FromAutomation(IntPtr window)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(window);
            if (Selection(root) is { } own) return own;

            // The text areas in the window: a document, a web page, an edit box. Usually only a few.
            var withText = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
            int looked = 0;
            foreach (AutomationElement area in root.FindAll(TreeScope.Descendants, withText))
            {
                if (++looked > MaxAreas) break;
                if (Selection(area) is { } marked) return marked;
            }
            return null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException
                                       or COMException or TimeoutException)
        {
            return null; // the window closed, or it doesn't support this
        }
    }

    static string? Selection(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)) return null;
        try
        {
            TextPatternRange[] ranges = ((TextPattern)pattern).GetSelection();
            string text = string.Join("\n", ranges.Select(range => range.GetText(TextHelp.MaxCloudChars + 1))).Trim();
            return text.Length > 0 ? text : null;
        }
        catch (InvalidOperationException)
        {
            return null; // this area has no selection support
        }
    }

    // Read only; the clipboard is left exactly as it was. The UI thread is required here (the clipboard is STA).
    static string? Copied()
    {
        try
        {
            if (!Clipboard.ContainsText()) return null;
            string text = Clipboard.GetText().Trim();
            return text.Length > 0 ? text : null;
        }
        catch (COMException)
        {
            return null; // another app is holding the clipboard
        }
    }
}
