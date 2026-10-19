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
