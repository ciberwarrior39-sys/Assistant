using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Assistant.Core;

namespace Assistant.App;

/// <summary>
/// The "Help with selected text" card in the chat: a preview of the marked text, where it came from, and the buttons.
/// It stays open for more than one request, until newer text replaces it.
/// </summary>
public sealed class TextHelpItem(string text, string source) : INotifyPropertyChanged
{
    const int PreviewChars = 300;

    string question = "";
    string outcome = "";
    bool isOpen = true;

    public string Text { get; } = text;
    public string Source { get; } = source;
    public string Preview => Text.Length > PreviewChars ? Text[..PreviewChars] + "…" : Text;

    public string Size => Text.Length > TextHelp.MaxLocalChars
        ? $"{Text.Length:N0} characters: longer than the local model reads at once, so it uses the start."
        : $"{Text.Length:N0} characters";

    /// <summary>The user's own question for "Ask".</summary>
    public string Question
    {
        get => question;
        set => Set(ref question, value);
    }

    public bool IsOpen
    {
        get => isOpen;
        private set
        {
            Set(ref isOpen, value);
            OnChanged(nameof(ButtonsVisibility));
        }
    }

    public Visibility ButtonsVisibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;

    public string Outcome
    {
        get => outcome;
        private set => Set(ref outcome, value);
    }

    public void Close(string result)
    {
        IsOpen = false;
        Outcome = result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
