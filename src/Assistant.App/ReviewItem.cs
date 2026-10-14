using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Assistant.Core.Memory;

namespace Assistant.App;

/// <summary>One weekly-review card in the chat: an editable fact with Keep and Forget.</summary>
public sealed class ReviewItem(ReviewCandidate candidate) : INotifyPropertyChanged
{
    string fact = candidate.Fact;
    string outcome = "";
    bool isOpen = true;

    public ReviewCandidate Candidate { get; } = candidate;
    public string Evidence => $"Why: {Candidate.Evidence}";

    /// <summary>The user can reword it before keeping.</summary>
    public string Fact
    {
        get => fact;
        set => Set(ref fact, value);
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
