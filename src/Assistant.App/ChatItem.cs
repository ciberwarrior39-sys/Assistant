using Assistant.Core.Search;

namespace Assistant.App;

/// <summary>One message in the chat window.</summary>
public sealed class ChatItem
{
    public required string Who { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<FileHit> Files { get; init; } = [];
}
