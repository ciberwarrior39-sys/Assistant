using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Assistant.App;

/// <summary>
/// "Solve with cloud chat": the text read off the screenshot, shown before anything is sent. The user can fix
/// what was misread, or cancel. Only the question and this text go to the cloud; the screenshot never does.
/// </summary>
public sealed class CloudConfirmItem(string question, string screenText) : INotifyPropertyChanged
{
    string questionText = question;
    string text = screenText;
    string outcome = "";
    bool isOpen = true;

    /// <summary>Selected text rather than text read off a screenshot: changes the card's wording and where it's sent.</summary>
    public bool ForSelectedText { get; init; }

    public string QuestionLabel => ForSelectedText ? "Your request" : "Your question";
    public string TextLabel => ForSelectedText ? "Selected text" : "Text from your screen";

    public string Explanation => ForSelectedText
        ? "Only your request and this text are sent (and logged in the sent folder). Remove anything private first."
        : "Read off the screenshot on this computer. Fix anything it misread. Only your question and this text are sent (and logged in the sent folder); the screenshot stays here.";

    public string Question
    {
        get => questionText;
        set => Set(ref questionText, value);
    }

    public string ScreenText
    {
        get => text;
        set => Set(ref text, value);
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
