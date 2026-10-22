using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Assistant.Core.Diagnostics;
using Assistant.Core.Organizer;

namespace Assistant.App;

/// <summary>
/// The to-do checklist in the chat. Ticking a box writes todo.md at once; the list is read from the file when the card
/// is made, so changes made in another editor show up the next time the list is shown.
/// </summary>
public sealed class TodoListItem
{
    public TodoListItem(TodoStore store)
    {
        foreach (TodoItem item in store.Items().OrderBy(item => item.Done))
            Rows.Add(new TodoRow(item.Text, item.Done, store));
    }

    public ObservableCollection<TodoRow> Rows { get; } = [];
}

/// <summary>One task on the card. Done is bound two-way to its checkbox.</summary>
public sealed class TodoRow(string text, bool done, TodoStore store) : INotifyPropertyChanged
{
    bool isDone = done;

    public string Text { get; } = text;

    public bool Done
    {
        get => isDone;
        set
        {
            if (isDone == value) return;
            try
            {
                // Matched by its exact text, so the right line changes even if the file was edited meanwhile.
                if (store.SetDone(Text, value).Count == 1) isDone = value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Error("Could not update todo.md", ex);
            }
            OnChanged(); // shows the real state: unchanged if it couldn't be saved
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnChanged([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
