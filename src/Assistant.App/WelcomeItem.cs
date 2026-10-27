namespace Assistant.App;

/// <summary>
/// A suggestion under the greeting. Text: what goes in the box. Send: send it straight away, or leave it there to be
/// finished ("find my …"). Null text: help with selected text, which has its own flow.
/// </summary>
public sealed record Suggestion(string Label, string? Text, bool Send);

/// <summary>The greeting at the top of a chat, with a few real things to try.</summary>
public sealed class WelcomeItem
{
    public IReadOnlyList<Suggestion> Suggestions { get; } =
    [
        new("Find a file", "find my ", Send: false),
        new("What did I do today?", "what did I do today?", Send: true),
        new("Set a timer", "set a timer for ", Send: false),
        new("My to-do list", "my list", Send: true),
        new("Help with selected text", null, Send: false),
    ];
}
