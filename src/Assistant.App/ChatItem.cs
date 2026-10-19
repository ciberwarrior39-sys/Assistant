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

    /// <summary>After a screen answer, with cloud chat connected: the question, for "Solve with cloud chat".</summary>
    public string? CloudQuestion { get; init; }

    public Visibility CloudButtonVisibility => CloudQuestion is null ? Visibility.Collapsed : Visibility.Visible;
}
