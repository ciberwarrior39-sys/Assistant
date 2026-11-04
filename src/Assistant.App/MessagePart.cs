using System.Windows;
using Assistant.Core;
using Assistant.Core.Llm;

namespace Assistant.App;

/// <summary>A piece of a chat message: prose, or a code block shown in a monospace box with its own Copy button.</summary>
public sealed class MessagePart(ReplyPart part)
{
    static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "C#", ["csharp"] = "C#", ["c#"] = "C#", ["cpp"] = "C++", ["c++"] = "C++", ["c"] = "C",
        ["py"] = "Python", ["python"] = "Python", ["js"] = "JavaScript", ["javascript"] = "JavaScript",
        ["ts"] = "TypeScript", ["typescript"] = "TypeScript", ["java"] = "Java", ["html"] = "HTML", ["css"] = "CSS",
        ["sql"] = "SQL", ["json"] = "JSON", ["xml"] = "XML", ["xaml"] = "XAML", ["bash"] = "Bash", ["sh"] = "Bash",
        ["powershell"] = "PowerShell", ["ps1"] = "PowerShell", ["cmd"] = "Command Prompt", ["bat"] = "Command Prompt",
        ["lua"] = "Lua", ["go"] = "Go", ["rust"] = "Rust", ["kotlin"] = "Kotlin", ["swift"] = "Swift", ["php"] = "PHP",
        ["text"] = "Text", ["txt"] = "Text", ["md"] = "Markdown", ["markdown"] = "Markdown", ["yaml"] = "YAML",
    };

    public string Text { get; } = part.Text;
    public bool IsCode { get; } = part.IsCode;

    public string LanguageLabel =>
        part.Language.Length == 0 ? "Code" : LanguageNames.TryGetValue(part.Language, out string? name) ? name : part.Language;

    public Visibility ProseVisibility => IsCode ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CodeVisibility => IsCode ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A web page an answer was based on, as a link under the reply.</summary>
public sealed class SourceLink(WebSource source)
{
    public string Title { get; } = source.Title;
    public string Url { get; } = source.Url;
    public string Site { get; } = Uri.TryCreate(source.Url, UriKind.Absolute, out Uri? address)
        ? (address.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? address.Host[4..] : address.Host)
        : "";
}
