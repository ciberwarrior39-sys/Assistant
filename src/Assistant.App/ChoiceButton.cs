using System.ComponentModel;
using Assistant.Core.Actions;

namespace Assistant.App;

/// <summary>
/// A button under a reply (which app to open, "Empty the Recycle Bin", "Cancel"). Pressing one disables the whole
/// group, so a confirmation can't run twice.
/// </summary>
public sealed class ChoiceButton(ActionOption option, List<ChoiceButton> group) : INotifyPropertyChanged
{
    bool isEnabled = true;

    public string Label => option.Label;
    public ActionIntent? Intent => option.Intent;

    public bool IsEnabled
    {
        get => isEnabled;
        private set
        {
            isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public void CloseGroup()
    {
        foreach (ChoiceButton button in group) button.IsEnabled = false;
    }

    public static IReadOnlyList<ChoiceButton> For(IReadOnlyList<ActionOption> options)
    {
        var group = new List<ChoiceButton>();
        group.AddRange(options.Select(option => new ChoiceButton(option, group)));
        return group;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
