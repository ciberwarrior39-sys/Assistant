using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Assistant.Core.Setup;

namespace Assistant.App;

public enum ModelOfferAnswer { Try, Later, No }

/// <summary>
/// The card that offers a bigger local model ("A smarter model may fit this PC"), then shows how trying it goes:
/// the download, the timing, and what was kept. Its three buttons go away once one is pressed.
/// </summary>
public sealed class ModelOfferItem(LocalModelInfo current, LocalModelInfo bigger) : INotifyPropertyChanged
{
    bool asking = true, working;
    double percent;
    bool knownProgress;
    string status = "";

    public LocalModelInfo Current => current;
    public LocalModelInfo Bigger => bigger;

    public string Title => "A smarter local model may fit this PC";

    public string Text =>
        $"{bigger.Name} ({bigger.DownloadGb:0.0} GB) understands more than your {current.Name} ({current.DownloadGb:0.0} GB), but it's " +
        "bigger. I can download it and time it on this PC: if it answers quickly enough, I switch to it and delete " +
        $"{current.Name}; if not, I delete the download and nothing changes.";

    public Visibility AskingVisibility => asking ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorkingVisibility => working ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StatusVisibility => status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public double Percent => percent;
    public bool ProgressUnknown => !knownProgress;
    public string Status => status;

    /// <summary>A button was pressed: no more buttons.</summary>
    public void Answered(string said)
    {
        asking = false;
        status = said;
        Changed(nameof(AskingVisibility), nameof(Status), nameof(StatusVisibility));
    }

    /// <summary>Progress: a step, and how far along (null: unknown).</summary>
    public void Show(string step, double? fraction)
    {
        working = true;
        status = step;
        knownProgress = fraction is not null;
        percent = (fraction ?? 0) * 100;
        Changed(nameof(WorkingVisibility), nameof(Status), nameof(StatusVisibility), nameof(Percent), nameof(ProgressUnknown));
    }

    /// <summary>Done, one way or the other: what happened, without the progress bar.</summary>
    public void Finish(string result)
    {
        working = false;
        status = result;
        Changed(nameof(WorkingVisibility), nameof(Status), nameof(StatusVisibility));
    }

    void Changed(params string[] names)
    {
        foreach (string name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
