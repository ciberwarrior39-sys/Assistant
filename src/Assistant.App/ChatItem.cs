using System.Windows;
using Assistant.Core.Search;

namespace Assistant.App;

/// <summary>One message in the chat window: the user's (Who starts with "You") or the assistant's.</summary>
public sealed class ChatItem
{
    public required string Who { get; init; }
    public required string Text { get; init; }

    public IReadOnlyList<FileHit> Files
    {
        get => files;
        init
        {
            files = value;
            FileRows = value.Select(hit => new FileRow(hit)).ToList();
        }
    }
    IReadOnlyList<FileHit> files = [];

    /// <summary>The files as rows for the chat (kind, name, folder, date).</summary>
    public IReadOnlyList<FileRow> FileRows { get; private init; } = [];

    /// <summary>Shown word for word under the reply, for example what was saved to memory.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>After a screen or selected-text answer, with cloud chat connected: the request to send there.</summary>
    public string? CloudQuestion { get; init; }

    /// <summary>The selected text to send with it. Null: it's a screen question, read off the screenshot first.</summary>
    public string? CloudText { get; init; }

    public Visibility CloudButtonVisibility => CloudQuestion is null ? Visibility.Collapsed : Visibility.Visible;

    public string CloudButtonLabel => CloudText is null ? "Solve with cloud chat…" : "Ask cloud chat instead…";

    /// <summary>Set on answers worth pasting somewhere (translations, fixed text); Copy otherwise copies the reply.</summary>
    public string? CopyText { get; init; }

    /// <summary>On replies to a message: what was asked and who answered, for the 👍/👎 buttons.</summary>
    public string? Question { get; init; }
    public string Source { get; init; } = "";

    /// <summary>Buttons under the reply: which app to open, or a confirmation.</summary>
    public IReadOnlyList<ChoiceButton> Choices { get; init; } = [];

    public bool IsUser => Who.StartsWith("You", StringComparison.Ordinal);

    public Visibility UserVisibility => IsUser ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReplyVisibility => IsUser ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Copy, 👍 and 👎: under replies to a message, and under answers worth pasting.</summary>
    public Visibility MetaVisibility => !IsUser && (Question is not null || CopyText is not null) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RateVisibility => Question is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"via Groq" when the cloud answered; a note when it was asked but didn't answer; nothing for the app itself.</summary>
    public string SourceLabel => Source switch
    {
        "" or "local" => "",
        _ when Source.Contains("cloud unavailable", StringComparison.Ordinal) => "Cloud chat didn't answer, so the local model did",
        _ => $"via {Source}",
    };
}
