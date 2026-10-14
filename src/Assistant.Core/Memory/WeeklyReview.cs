using Assistant.Core.Activity;

namespace Assistant.Core.Memory;

/// <summary>A fact the assistant noticed, waiting for the user's Keep or Forget. Evidence says why.</summary>
public sealed record ReviewCandidate(string Fact, string Evidence, MemoryKind Kind);

/// <summary>
/// Reads the week's activity log and proposes at most 5 facts worth keeping. Plain counting rules,
/// not the language model: the small model invents things, and every candidate here can be checked
/// against the log. Facts already in memory, or ones the user chose to forget, are not proposed again.
/// </summary>
public static class WeeklyReview
{
    public const int MaxCandidates = 5;

    static readonly TimeSpan HeavyUse = TimeSpan.FromHours(3);
    static readonly TimeSpan ProjectTime = TimeSpan.FromHours(2);
    const int MinDays = 3;

    // Windows' own shell windows say nothing about the user.
    static readonly HashSet<string> SystemApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ApplicationFrameHost.exe", "LockApp.exe", "SearchHost.exe", "SearchApp.exe",
        "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "TextInputHost.exe", "Taskmgr.exe",
        "SystemSettings.exe", "dwm.exe",
    };

    /// <summary>Where the project name sits in each editor's window title, split on " - ".</summary>
    static readonly Dictionary<string, Func<string[], string?>> ProjectInTitle = new(StringComparer.OrdinalIgnoreCase)
    {
        // "● App.xaml.cs - Assistant - Visual Studio Code"
        ["Code.exe"] = parts => parts.Length >= 3 ? parts[^2] : null,
        // "Assistant - Microsoft Visual Studio"
        ["devenv.exe"] = parts => parts.Length >= 2 ? parts[0] : null,
        // "Assistant – App.xaml.cs" (Rider) and "MyGame - SampleScene - Windows, Mac, Linux - Unity 6..." (Unity)
        ["rider64.exe"] = parts => parts.Length >= 2 ? parts[0] : null,
        ["Unity.exe"] = parts => parts.Length >= 3 ? parts[0] : null,
    };

    public static IReadOnlyList<ReviewCandidate> Propose(IReadOnlyList<ActivityEntry> week, MemoryStore memory, IReadOnlyCollection<string> declined)
    {
        if (week.Count == 0) return [];

        // A saved line that contains the fact counts too, for example after the user added detail to it.
        var known = memory.AllFacts().Select(fact => fact.Text).Concat(declined).ToList();
        var candidates = new List<(ReviewCandidate Candidate, double Strength)>();

        candidates.AddRange(MainApps(week));
        candidates.AddRange(Projects(week));
        if (LateNights(week) is { } late) candidates.Add(late);
        if (UsualStart(week) is { } start) candidates.Add(start);

        return candidates
            .Where(item => !known.Any(fact => fact.Contains(item.Candidate.Fact, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.Strength)
            .Select(item => item.Candidate)
            .Take(MaxCandidates)
            .ToList();
    }

    // "Uses Unity a lot": at least 3 h on at least 3 days. Top two apps.
    static IEnumerable<(ReviewCandidate, double)> MainApps(IReadOnlyList<ActivityEntry> week) =>
        week.Where(entry => !SystemApps.Contains(entry.App))
            .GroupBy(entry => entry.App, StringComparer.OrdinalIgnoreCase)
            .Select(app => (Name: Friendly(app.Key), Seconds: app.Sum(entry => entry.Seconds), Days: Days(app)))
            .Where(app => app.Seconds >= HeavyUse.TotalSeconds && app.Days >= MinDays)
            .OrderByDescending(app => app.Seconds)
            .Take(2)
            .Select(app => (new ReviewCandidate($"Uses {app.Name} a lot",
                                                $"{Hours(app.Seconds)} on {app.Days} of the last 7 days",
                                                MemoryKind.AboutMe),
                            app.Seconds));

    // "Works on a project called Assistant": the project part of an editor's window titles, 2 h on 2+ days.
    static IEnumerable<(ReviewCandidate, double)> Projects(IReadOnlyList<ActivityEntry> week) =>
        week.Select(entry => (entry, Project: ProjectName(entry)))
            .Where(item => item.Project is not null)
            .GroupBy(item => item.Project!, StringComparer.OrdinalIgnoreCase)
            .Select(project => (Name: project.Key,
                                App: Friendly(project.First().entry.App),
                                Seconds: project.Sum(item => item.entry.Seconds),
                                Days: Days(project.Select(item => item.entry))))
            .Where(project => project.Seconds >= ProjectTime.TotalSeconds && project.Days >= 2)
            .OrderByDescending(project => project.Seconds)
            .Take(2)
            .Select(project => (new ReviewCandidate($"Works on a project called \"{project.Name}\"",
                                                    $"{Hours(project.Seconds)} in {project.App} on {project.Days} days",
                                                    MemoryKind.AboutMe),
                                project.Seconds * 1.5)); // a named project says more than an app

    // "Often works late": active after 23:00 (or before 4:00) on at least 3 nights.
    static (ReviewCandidate, double)? LateNights(IReadOnlyList<ActivityEntry> week)
    {
        int nights = week.Where(entry => entry.End.Hour >= 23 || entry.Start.Hour < 4)
                         .Select(entry => entry.Start.AddHours(-4).Date) // after midnight counts as the night before
                         .Distinct()
                         .Count();
        if (nights < MinDays) return null;
        return (new ReviewCandidate("Often works late, after 11 pm", $"on {nights} of the last 7 nights", MemoryKind.AboutMe),
                nights * 3600.0);
    }

    // "Usually starts around 10:00": first activity of the day (after 5:00) on 4+ days, all within 2 hours.
    static (ReviewCandidate, double)? UsualStart(IReadOnlyList<ActivityEntry> week)
    {
        var starts = week.Where(entry => entry.Start.Hour >= 5)
                         .GroupBy(entry => entry.Start.Date)
                         .Select(day => day.Min(entry => entry.Start).TimeOfDay)
                         .Order()
                         .ToList();
        if (starts.Count < 4) return null;

        // The middle of the days, ignoring the earliest and latest one.
        var middle = starts.Skip(1).Take(starts.Count - 2).ToList();
        if (middle[^1] - middle[0] > TimeSpan.FromHours(2)) return null;

        TimeSpan median = starts[starts.Count / 2];
        var rounded = TimeSpan.FromMinutes(Math.Round(median.TotalMinutes / 30) * 30);
        return (new ReviewCandidate($"Usually starts on the computer around {rounded:hh\\:mm}",
                                    $"first activity on {starts.Count} days",
                                    MemoryKind.AboutMe),
                starts.Count * 1800.0);
    }

    static string? ProjectName(ActivityEntry entry)
    {
        if (!ProjectInTitle.TryGetValue(entry.App, out var find)) return null;

        string[] parts = entry.Title.Split([" - ", " — ", " – "], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? name = find(parts)?.Trim('●', '*', ' ');
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40) return null;

        // Not a project: welcome pages, settings, and bare file names.
        if (name.Contains("Welcome", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Settings", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Unity ", StringComparison.OrdinalIgnoreCase)
            || IsFileName(name)) return null;
        return name;
    }

    // "App.xaml.cs": one word with a short extension.
    static bool IsFileName(string name)
    {
        int dot = name.LastIndexOf('.');
        return !name.Contains(' ') && dot > 0 && name.Length - dot - 1 is >= 1 and <= 5;
    }

    static int Days(IEnumerable<ActivityEntry> entries) => entries.Select(entry => entry.Start.Date).Distinct().Count();

    static string Friendly(string exe) => AppNames.Name(exe);

    static string Hours(double seconds) => seconds >= 3600 * 1.5 ? $"about {Math.Round(seconds / 3600)} h" : $"about {Math.Round(seconds / 60)} min";
}
