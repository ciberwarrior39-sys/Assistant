using System.Windows;
using Assistant.Core.Search;

namespace Assistant.App;

/// <summary>One message in the chat window.</summary>
public sealed class ChatItem
{
    public required string Who { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<FileHit> Files { get; init; } = [];

    /// <summary>Shown word for word under the reply, for example what was saved to memory.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>After a screen or selected-text answer, with cloud chat connected: the request to send there.</summary>
    public string? CloudQuestion { get; init; }

    /// <summary>The selected text to send with it. Null: it's a screen question, read off the screenshot first.</summary>
    public string? CloudText { get; init; }

    public Visibility CloudButtonVisibility => CloudQuestion is null ? Visibility.Collapsed : Visibility.Visible;

    public string CloudButtonLabel => CloudText is null ? "Solve with cloud chat…" : "Ask cloud chat instead…";

    /// <summary>Set on answers worth pasting somewhere (translations, fixed text): shows a Copy button.</summary>
    public string? CopyText { get; init; }

    public Visibility CopyVisibility => CopyText is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Buttons under the reply: which app to open, or a confirmation.</summary>
    public IReadOnlyList<ChoiceButton> Choices { get; init; } = [];
}
