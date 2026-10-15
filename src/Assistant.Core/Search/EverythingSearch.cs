using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Core.Search;

/// <summary>
/// File search through Everything (voidtools). Needs the Everything app running
/// and Everything64.dll from the Everything SDK next to the app.
/// </summary>
public sealed class EverythingSearch : IFileSearch
{
    // The SDK keeps one global query state per process, so calls must not overlap.
    static readonly Lock Gate = new();

    // System and program folders hold thousands of files whose names happen to contain short words
    // like "cv", and they change constantly, so they would crowd out the user's own files.
    // Build output and tool folders change constantly too.
    internal static readonly string[] ExcludedPaths =
    [
        @"\Windows\", @"\Program Files", @"\ProgramData\", @"\AppData\", @"\$Recycle.Bin\",
        @"\obj\", @"\bin\Debug\", @"\bin\Release\", @"\.git\", @"\.vs\", @"\node_modules\",
    ];

    // File types nobody searches for by name; skipped unless the model asked for specific types.
    const string NoiseExtensions = "dll;sys;mui;cat;manifest;pdb;tmp;etl;pf;ini";

    // Ranking pool: enough to find the user's file among newer partial matches.
    const uint CandidateLimit = 500;

    static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\";

    public string Name => "Everything";

    public bool IsAvailable(out string problem)
    {
        try
        {
            lock (Gate)
            {
                if (Native.Everything_GetMajorVersion() != 0)
                {
                    problem = "";
                    return true;
                }
                problem = Native.Everything_GetLastError() == Native.ERROR_IPC
                    ? "Everything is not running"
                    : "Everything did not respond";
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            problem = "Everything64.dll is missing from lib";
            return false;
        }
    }

    public Task<FileSearchResult> SearchAsync(FileSearchQuery query, CancellationToken ct) =>
        Task.Run(() => Search(query), ct);

    static FileSearchResult Search(FileSearchQuery query)
    {
        try
        {
            lock (Gate)
            {
                Native.Everything_SetSearchW(BuildQuery(query));
                Native.Everything_SetRequestFlags(Native.REQUEST_FULL_PATH_AND_FILE_NAME | Native.REQUEST_DATE_MODIFIED);
                Native.Everything_SetSort(Native.SORT_DATE_MODIFIED_DESCENDING);
                Native.Everything_SetMax(CandidateLimit);

                if (!Native.Everything_QueryW(true))
                {
                    return FileSearchResult.Unavailable(Native.Everything_GetLastError() == Native.ERROR_IPC
                        ? "Everything is not running"
                        : "the search failed");
                }

                var candidates = new List<FileHit>();
                var buffer = new StringBuilder(32768);
                uint total = Native.Everything_GetNumResults();

                for (uint i = 0; i < total; i++)
                {
                    DateTime modified = Native.Everything_GetResultDateModified(i, out long fileTime) && fileTime > 0
                        ? DateTime.FromFileTime(fileTime)
                        : DateTime.MinValue;

                    // Results are sorted newest first, so everything after this is older too.
                    if (query.ModifiedAfter is { } after && modified < after) break;

                    buffer.Clear();
                    Native.Everything_GetResultFullPathNameW(i, buffer, (uint)buffer.Capacity);
                    string path = buffer.ToString();
                    if (ExcludedPaths.Any(folder => path.Contains(folder, StringComparison.OrdinalIgnoreCase))) continue;

                    // Office creates hidden "~$TEST.docx" lock files while a document is open.
                    if (Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal)) continue;

                    candidates.Add(new FileHit(path, modified));
                }

                // When some names contain a keyword as a whole word ("CV.docx", "Stefan_CV.html"),
                // names that only contain it inside a longer word ("..._cv7q.info") are noise: drop them.
                if (query.Keywords.Count > 0 && candidates.Any(hit => HasWholeWordMatch(hit, query.Keywords)))
                    candidates = candidates.Where(hit => HasWholeWordMatch(hit, query.Keywords)).ToList();

                // Best name match first; among equal matches the newest stays first (OrderBy is stable).
                List<FileHit> files = candidates
                    .OrderByDescending(hit => Score(hit, query.Keywords))
                    .Take(query.MaxResults)
                    .ToList();

                return new FileSearchResult(true, files);
            }
        }
        catch (DllNotFoundException)
        {
            return FileSearchResult.Unavailable("Everything64.dll is missing from lib");
        }
    }

    // Keywords and extensions arrive already cleaned by the tool, so no user text reaches Everything's syntax raw.
    static string BuildQuery(FileSearchQuery query)
    {
        var parts = new List<string> { "file:" };
        parts.AddRange(query.Keywords.Select(keyword => $"\"{keyword}\""));

        // A backslash makes Everything match against the full path; "!" excludes.
        parts.AddRange(ExcludedPaths.Select(folder => $"!\"{folder}\""));

        parts.Add(query.Extensions.Count > 0
            ? "ext:" + string.Join(';', query.Extensions)
            : "!ext:" + NoiseExtensions);

        return string.Join(' ', parts);
    }

    // "CV.docx" for the keyword "cv" beats "My CV 2026.pdf", which beats "cvtres.exe".
    internal static int Score(FileHit hit, IReadOnlyList<string> keywords)
    {
        string stem = Path.GetFileNameWithoutExtension(hit.FullPath);
        int score = 0;
        foreach (string keyword in keywords)
        {
            if (stem.Equals(keyword, StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (IsWholeWord(stem, keyword)) score += 50;
            else score += 10; // found only inside a longer word
        }

        // The user's own folders (Desktop, Documents, Downloads...) are where their files usually are.
        if (hit.FullPath.StartsWith(UserProfile, StringComparison.OrdinalIgnoreCase)) score += 20;
        return score;
    }

    internal static bool HasWholeWordMatch(FileHit hit, IReadOnlyList<string> keywords)
    {
        string stem = Path.GetFileNameWithoutExtension(hit.FullPath);
        return keywords.Any(keyword => IsWholeWord(stem, keyword));
    }

    static bool IsWholeWord(string text, string word) =>
        Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase);

    static class Native
    {
        const string Dll = "Everything64.dll";

        public const uint REQUEST_FULL_PATH_AND_FILE_NAME = 0x00000004;
        public const uint REQUEST_DATE_MODIFIED = 0x00000040;
        public const uint SORT_DATE_MODIFIED_DESCENDING = 14;
        public const uint ERROR_IPC = 2;

        [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern void Everything_SetSearchW(string search);
        [DllImport(Dll)] public static extern void Everything_SetRequestFlags(uint flags);
        [DllImport(Dll)] public static extern void Everything_SetSort(uint sort);
        [DllImport(Dll)] public static extern void Everything_SetMax(uint max);
        [DllImport(Dll)] public static extern bool Everything_QueryW(bool wait);
        [DllImport(Dll)] public static extern uint Everything_GetNumResults();
        [DllImport(Dll)] public static extern uint Everything_GetLastError();
        [DllImport(Dll)] public static extern uint Everything_GetMajorVersion();
        [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern uint Everything_GetResultFullPathNameW(uint index, StringBuilder buffer, uint bufferSize);
        [DllImport(Dll)] public static extern bool Everything_GetResultDateModified(uint index, out long fileTime);
    }
}
