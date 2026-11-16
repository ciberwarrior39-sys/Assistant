using Assistant.Core.Actions;

namespace Assistant.Core.Guide;

/// <summary>
/// One control in a window, as Windows describes it to screen readers: its name ("Save"), what it is ("button") and
/// where it is on the screen, in the screen's own pixels.
/// </summary>
public sealed record UiElement(string Name, string Kind, int X, int Y, int Width, int Height, bool Enabled = true);

/// <summary>The controls of the window the user was in. Complete is false when the look was cut short (too many, too slow).</summary>
public sealed record UiWindow(string App, int X, int Y, int Width, int Height, IReadOnlyList<UiElement> Elements, bool Complete = true);

/// <summary>What a look at the window gave: the window, or why not (a game, an excluded window, nothing readable).</summary>
public sealed record UiScan(UiWindow? Window, string? Problem);

/// <summary>
/// "Show me where to click": implemented by the app. It only ever looks (through Windows' accessibility interface,
/// the one screen readers use) and draws; it never clicks, types or takes the keyboard.
/// </summary>
public interface IScreenGuide
{
    /// <summary>The app whose window the user was last in ("Excel"), or "" when there is none. Cheap: nothing is read.</summary>
    string WindowApp { get; }

    /// <summary>Reads the controls of the window the user was last in (not the assistant's own).</summary>
    Task<UiScan> ScanAsync(CancellationToken ct);

    /// <summary>Draws a ring around the control for a few seconds. Clicks go straight through it.</summary>
    void Show(UiElement element);

    /// <summary>The character walks over and stands next to the control, for when the ring wasn't enough.</summary>
    void Point(UiElement element);
}

/// <summary>Offered by cloud chat for "where's the thing that makes text bold?": find it in the window and ring it.</summary>
public sealed record GuideShow(string What) : ActionIntent;

/// <summary>From the "Which one?" buttons: ring this control.</summary>
public sealed record ShowElement(UiElement Element) : ActionIntent;

/// <summary>"I can't see it": the character goes and stands next to it.</summary>
public sealed record PointAtElement(UiElement Element) : ActionIntent;

/// <summary>"Next step": look at the window again and pick what to click now. Done is what was clicked so far.</summary>
public sealed record GuideNextStep(string Task, string Done) : ActionIntent;

/// <summary>
/// The local model couldn't pick: cloud chat can, from the names of the controls. The chat shows this as a card with
/// exactly what would be sent (the task and the list), and nothing goes until Send is pressed.
/// </summary>
public sealed record GuideCloudOffer(string Task, string Names);
