namespace Assistant.Core.Storage;

/// <summary>One custom shortcut: the keys ("Ctrl+Alt+P") and the command they run ("pause"), as if typed to the assistant.</summary>
public sealed class Shortcut
{
    public string Keys { get; set; } = "";
    public string Command { get; set; } = "";
}
