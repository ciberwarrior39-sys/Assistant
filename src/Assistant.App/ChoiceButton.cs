using System.ComponentModel;
using System.Windows.Media;
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

    // A button for a file or folder ("Which one should I open?") takes the colour of its kind, like the file cards:
    // yellow for folders, blue for Word documents, green for spreadsheets…
    readonly string? kind = option.Intent switch
    {
        OpenFoundFile found => FileRow.KindOf(found.Path),
        CopyFile copy => FileRow.KindOf(copy.Source),
        _ => null,
    };

    public bool HasKind => kind is not null;

    /// <summary>The outline, and the fill under the mouse.</summary>
    public Brush? KindBrush => kind is null ? null : FileRow.KindBrush(kind);

    /// <summary>The light fill: the same colour, mostly see-through, so the label stays readable in both themes.</summary>
    public Brush? KindTint => kind is null ? null : FileRow.KindBrush(kind, alpha: 60);

    /// <summary>Said on hover, so the colour isn't the only sign of what it is.</summary>
    public string? KindName => kind switch
    {
        null => null,
        "folder" => "Folder",
        "file" => "File",
        _ => $".{kind} file",
    };

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
