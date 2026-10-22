using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Assistant.Core.Diagnostics;

namespace Assistant.App;

/// <summary>An installed app as the Start menu lists it. Id is what Windows launches it by.</summary>
public sealed record InstalledApp(string Name, string Id);

/// <summary>
/// The apps in the Start menu, normal and Store ones, read from Windows' own "Apps" folder (shell:AppsFolder) — the
/// same list Start and Windows search use. Read once and kept 10 minutes. If that folder can't be read, the Start menu
/// shortcut folders are used instead.
/// </summary>
public sealed class AppCatalog
{
    static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    // Entries that are never what "open X" means.
    static readonly string[] NotApps =
        ["uninstall", "readme", "read me", "release notes", "license", "documentation", "help", "website", "деинсталиране"];

    List<InstalledApp> apps = [];
    DateTime readAt = DateTime.MinValue;

    public IReadOnlyList<InstalledApp> Apps
    {
        get
        {
            if (DateTime.UtcNow - readAt > KeepFor)
            {
                apps = Read();
                readAt = DateTime.UtcNow;
            }
            return apps;
        }
    }

    /// <summary>
    /// The best matches for a name: one when it's clear, several when it isn't, none when nothing fits. Score: 100 exact,
    /// 80 the name starts with it, 60 has all its words, 40 a near spelling.
    /// </summary>
    public (IReadOnlyList<InstalledApp> Apps, int Score) Find(string name)
    {
        string query = Normalize(name);
        if (query.Length == 0) return ([], 0);
        string[] words = query.Split(' ');

        var scored = Apps
            .Select(app => (App: app, Score: Score(Normalize(app.Name), query, words)))
            .Where(entry => entry.Score > 0)
            .ToList();
        if (scored.Count == 0) return ([], 0);

        int best = scored.Max(entry => entry.Score);
        return (scored.Where(entry => entry.Score == best)
                      .Select(entry => entry.App)
                      .DistinctBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                      .OrderBy(app => app.Name.Length)
                      .Take(6)
                      .ToList(), best);
    }

    public static void Launch(InstalledApp app)
    {
        // A shortcut or program file opens as Explorer would; a Store app opens through the Apps folder.
        var start = File.Exists(app.Id)
            ? new ProcessStartInfo(app.Id) { UseShellExecute = true }
            : new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.Id}") { UseShellExecute = true };
        using Process? _ = Process.Start(start);
    }

    // 100 exact, 80 starts with it, 60 has all its words, 40 a near spelling ("spotfy"), 0 no match.
    static int Score(string name, string query, string[] words)
    {
        if (name == query) return 100;
        if (name.StartsWith(query + " ", StringComparison.Ordinal)) return 80;
        string[] nameWords = name.Split(' ');
        if (words.All(word => nameWords.Any(part => part.StartsWith(word, StringComparison.Ordinal)))) return 60;
        int allowed = query.Length >= 7 ? 2 : query.Length >= 4 ? 1 : 0;
        if (allowed > 0 && (Distance(name, query) <= allowed || nameWords.Any(part => Distance(part, query) <= allowed))) return 40;
        return 0;
    }

    static string Normalize(string text) =>
        string.Join(' ', text.ToLowerInvariant()
                             .Replace('-', ' ').Replace('_', ' ').Replace('.', ' ')
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    static List<InstalledApp> Read()
    {
        List<InstalledApp> found;
        try
        {
            found = FromAppsFolder();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            DiagnosticLog.Error("Couldn't read the Apps folder; using Start menu shortcuts", ex);
            found = [];
        }
        if (found.Count == 0) found = FromStartMenu();
        return found.Where(app => !NotApps.Any(word => app.Name.Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    static List<InstalledApp> FromAppsFolder()
    {
        var list = new List<InstalledApp>();
        Type? shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return list;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string id = item.Path;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) list.Add(new InstalledApp(name, id));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
        return list;
    }

    static List<InstalledApp> FromStartMenu()
    {
        var list = new List<InstalledApp>();
        foreach (Environment.SpecialFolder root in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
        {
            string folder = Environment.GetFolderPath(root);
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                {
                    if (Path.GetExtension(file).ToLowerInvariant() is ".lnk" or ".url" or ".appref-ms")
                        list.Add(new InstalledApp(Path.GetFileNameWithoutExtension(file), file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that can't be read is skipped.
            }
        }
        return list;
    }

    static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return int.MaxValue;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
