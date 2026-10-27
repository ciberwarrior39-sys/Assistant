using System.IO;
using System.Windows;
using System.Windows.Media;
using Assistant.Core.Search;

namespace Assistant.App;

/// <summary>A found file (or a remembered folder) as one row in the chat: its kind, name, folder and date.</summary>
public sealed class FileRow(FileHit hit)
{
    static readonly Dictionary<string, string> KindColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["doc"] = "#2B579A", ["docx"] = "#2B579A", ["odt"] = "#2B579A", ["rtf"] = "#2B579A",
        ["xls"] = "#217346", ["xlsx"] = "#217346", ["csv"] = "#217346", ["ods"] = "#217346",
        ["ppt"] = "#C43E1C", ["pptx"] = "#C43E1C", ["odp"] = "#C43E1C",
        ["pdf"] = "#C8382E",
        ["png"] = "#8E6CEF", ["jpg"] = "#8E6CEF", ["jpeg"] = "#8E6CEF", ["gif"] = "#8E6CEF", ["webp"] = "#8E6CEF", ["psd"] = "#8E6CEF",
        ["mp3"] = "#D9467C", ["wav"] = "#D9467C", ["mp4"] = "#D9467C", ["mkv"] = "#D9467C", ["mov"] = "#D9467C",
        ["zip"] = "#7A6A55", ["rar"] = "#7A6A55", ["7z"] = "#7A6A55",
        ["txt"] = "#5F6878", ["md"] = "#5F6878",
        ["cs"] = "#3D8B8B", ["py"] = "#3D8B8B", ["js"] = "#3D8B8B", ["html"] = "#3D8B8B", ["json"] = "#3D8B8B", ["unity"] = "#3D8B8B",
        ["exe"] = "#B5651D", ["msi"] = "#B5651D", ["lnk"] = "#B5651D",
    };

    public string Path { get; } = hit.FullPath;
    public bool IsFolder { get; } = Directory.Exists(hit.FullPath);

    public string Name => IsFolder ? new DirectoryInfo(Path).Name : System.IO.Path.GetFileName(Path);

    public string Folder => (IsFolder ? Path : System.IO.Path.GetDirectoryName(Path)) ?? Path;

    /// <summary>The extension in lower case ("docx"), or "folder"; at most 4 letters fit the badge.</summary>
    public string Kind
    {
        get
        {
            if (IsFolder) return "folder";
            string extension = System.IO.Path.GetExtension(Path).TrimStart('.').ToLowerInvariant();
            return extension.Length is 0 or > 5 ? "file" : extension;
        }
    }

    public Brush Badge
    {
        get
        {
            string hex = IsFolder ? "#D9A21B" : KindColors.TryGetValue(Kind, out string? color) ? color : "#5F6878";
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>"today 14:20", "yesterday", "12 Oct", "3 Mar 2024".</summary>
    public string When
    {
        get
        {
            DateTime time = hit.Modified;
            int days = (DateTime.Today - time.Date).Days;
            return days switch
            {
                0 => $"today {time:HH:mm}",
                1 => "yesterday",
                _ when time.Year == DateTime.Today.Year => time.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture),
                _ => time.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
            };
        }
    }

    /// <summary>A folder opens in Explorer already, so it has no "Show in folder".</summary>
    public Visibility RevealVisibility => IsFolder ? Visibility.Collapsed : Visibility.Visible;
}
