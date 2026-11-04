using Assistant.Core;

namespace Assistant.App;

/// <summary>The card at the top of the chat after an update: the new version and its three highlights.</summary>
public sealed class UpdateItem(ReleaseNotes notes)
{
    public string Title { get; } = $"Updated to {notes.Version}";

    public IReadOnlyList<ChangeLine> Highlights { get; } = notes.Highlights.Select(change => new ChangeLine(change)).ToList();
}

/// <summary>One change: its bold title ("Your own shortcuts:") and the rest.</summary>
public sealed class ChangeLine
{
    public ChangeLine(string change) => (Title, Text) = Changelog.Split(change);

    public string Title { get; }
    public string Text { get; }
}
