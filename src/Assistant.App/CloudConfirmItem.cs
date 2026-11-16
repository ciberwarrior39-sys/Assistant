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

    /// <summary>Added under the cloud's answer by the app itself (the scam check's advice), so it's always there.</summary>
    public string? AppendToAnswer { get; init; }

    /// <summary>"Show me where to click": what the user wants to do, and the names of the controls in their window.</summary>
    public bool ForGuide { get; init; }

    public string QuestionLabel => ForGuide ? "What you want to do" : ForSelectedText ? "Your request" : "Your question";
    public string TextLabel => ForGuide ? "Names of the buttons in that window" : ForSelectedText ? "Selected text" : "Text from your screen";

    public string Explanation => ForGuide
        ? "Only this and these names are sent (and logged in the sent folder), so cloud chat can pick where to click. Delete any line that's private, such as a file or a person's name."
        : ForSelectedText
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
