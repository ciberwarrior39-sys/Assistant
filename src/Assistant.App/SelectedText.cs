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
/// changed. Web pages are looked at first (a browser's address bar also has "selected" text: the address), and a
/// browser that only starts sharing its page when asked gets a second look a moment later. Apps that don't share
/// their selection (Electron apps like Discord) fall back to text copied in the last two minutes, and the card says
/// so; anything copied longer ago isn't used, because it's probably not what the user means.
/// </summary>
public static class SelectedText
{
    // A big web page can take a while to search; past this, the copied text is used instead.
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3.5);
    static readonly TimeSpan SecondLookAfter = TimeSpan.FromMilliseconds(700);
    static readonly TimeSpan CopiedRecently = TimeSpan.FromMinutes(2);
    const int MaxAreas = 300;

    /// <summary>The text, or null with the reason to show.</summary>
    public static async Task<(PickedText? Text, string Problem)> PickAsync(WindowCapture windows)
    {
        TextSource source = windows.TextTarget();
        if (source.Problem is null)
        {
            // UI Automation runs off the UI thread: it may wait on the other app, and must never wait on our own.
            Task<string?> read = Task.Run(async () =>
            {
                // Chrome and Edge build the page's accessibility view only once something asks for it, so the first
                // look often finds nothing: look again a moment later.
                if (FromAutomation(source.Window) is { } first) return first;
                await Task.Delay(SecondLookAfter);
                return FromAutomation(source.Window);
            });
            if (await Task.WhenAny(read, Task.Delay(ReadTimeout)) == read && await read is { } marked)
                return (new PickedText(marked, $"Marked in {source.Name}"), "");
        }
        else if (!source.ClipboardAllowed)
        {
            return (null, source.Problem);
        }

        if (ClipboardWatch.ChangedWithin(CopiedRecently) && Copied() is { } copied)
        {
            string from = source.Problem is null
                ? $"What you just copied ({source.Name} didn't share the marked text)"
                : "What you just copied";
            return (new PickedText(copied, from), "");
        }
        return (null, "I couldn't read the marked text in that window. Mark it, press Ctrl+C, then ask again: " +
                      "I'll use what you just copied.");
    }

    static string? FromAutomation(IntPtr window)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(window);
            if (Selection(root) is { } own) return own;

            var withText = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);

            // Documents first: a web page, a Word or PDF document.
            var documents = new AndCondition(withText, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            int looked = 0;
            foreach (AutomationElement document in root.FindAll(TreeScope.Descendants, documents))
            {
                if (++looked > MaxAreas) break;
                if (Selection(document) is { } marked) return marked;
            }

            // Then the other text areas (edit boxes), except those in a toolbar: a browser's address bar reports the
            // address as selected even when the user marked something on the page.
            foreach (AutomationElement area in root.FindAll(TreeScope.Descendants, withText))
            {
                if (++looked > MaxAreas) break;
                if (area.Current.ControlType == ControlType.Document || InToolbar(area)) continue;
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

    static bool InToolbar(AutomationElement element)
    {
        TreeWalker walker = TreeWalker.ControlViewWalker;
        AutomationElement? parent = walker.GetParent(element);
        for (int depth = 0; parent is not null && depth < 8; depth++)
        {
            if (parent.Current.ControlType == ControlType.ToolBar) return true;
            parent = walker.GetParent(parent);
        }
        return false;
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
