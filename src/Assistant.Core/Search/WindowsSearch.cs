using System.Data.OleDb;
using System.Globalization;
using System.Text;

namespace Assistant.Core.Search;

/// <summary>
/// File search through Windows' own index: built in, nothing to install. The fallback when Everything isn't
/// installed. By default Windows indexes the user's folders (Desktop, Documents, Downloads...), not whole drives,
/// which is where people's own files usually are anyway. Results are ranked like Everything's.
/// </summary>
public sealed class WindowsSearch : IFileSearch
{
    const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\"";
    const int CandidateLimit = 300;
    static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(1);

    bool? available;
    string problem = "";
    DateTime checkedAt = DateTime.MinValue;

    public string Name => "Windows Search";

    public bool IsAvailable(out string why)
    {
        if (available is null || DateTime.UtcNow - checkedAt > RecheckAfter)
        {
            try
            {
                using var connection = new OleDbConnection(ConnectionString);
                connection.Open();
                using var command = new OleDbCommand("SELECT TOP 1 System.ItemPathDisplay FROM SystemIndex", connection);
                using OleDbDataReader reader = command.ExecuteReader();
                available = true;
                problem = "";
            }
            catch (Exception ex) when (ex is OleDbException or InvalidOperationException)
            {
                available = false;
                problem = "the Windows Search service isn't running";
            }
            checkedAt = DateTime.UtcNow;
        }
        why = problem;
        return available == true;
    }

    public Task<FileSearchResult> SearchAsync(FileSearchQuery query, CancellationToken ct) => Task.Run(() => Search(query), ct);

    static FileSearchResult Search(FileSearchQuery query)
    {
        var candidates = new List<FileHit>();
        try
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            using var command = new OleDbCommand(BuildQuery(query), connection);
            using OleDbDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                string path = reader.GetString(0);
                DateTime modified = reader.IsDBNull(1) ? DateTime.MinValue : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc).ToLocalTime(); // the index stores UTC

                if (EverythingSearch.ExcludedPaths.Any(folder => path.Contains(folder, StringComparison.OrdinalIgnoreCase))) continue;
                if (Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal)) continue; // Office lock files
                candidates.Add(new FileHit(path, modified));
            }
        }
        catch (Exception ex) when (ex is OleDbException or InvalidOperationException)
        {
            return FileSearchResult.Unavailable("Windows Search didn't answer");
        }

        if (query.Keywords.Count > 0 && candidates.Any(hit => EverythingSearch.HasWholeWordMatch(hit, query.Keywords)))
            candidates = candidates.Where(hit => EverythingSearch.HasWholeWordMatch(hit, query.Keywords)).ToList();

        List<FileHit> files = candidates
            .OrderByDescending(hit => EverythingSearch.Score(hit, query.Keywords))
            .Take(query.MaxResults)
            .ToList();
        return new FileSearchResult(true, files);
    }

    // Keywords and extensions arrive cleaned by the tool (letters, digits, space, - _ .); quotes are stripped again here.
    static string BuildQuery(FileSearchQuery query)
    {
        var sql = new StringBuilder(
            $"SELECT TOP {CandidateLimit} System.ItemPathDisplay, System.DateModified FROM SystemIndex " +
            "WHERE SCOPE='file:' AND System.ItemType <> 'Directory'");

        foreach (string keyword in query.Keywords)
            sql.Append($" AND System.FileName LIKE '%{Clean(keyword)}%'");

        if (query.Extensions.Count > 0)
            sql.Append(" AND System.FileExtension IN (" + string.Join(",", query.Extensions.Select(ext => $"'.{Clean(ext)}'")) + ")");

        if (query.ModifiedAfter is { } after)
            sql.Append($" AND System.DateModified >= '{after.ToUniversalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)}'");

        sql.Append(" ORDER BY System.DateModified DESC");
        return sql.ToString();
    }

    static string Clean(string text) =>
        new(text.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.').ToArray());
}

/// <summary>Everything when it's installed and running, otherwise Windows Search.</summary>
public sealed class FallbackFileSearch(IFileSearch preferred, IFileSearch fallback) : IFileSearch
{
    public string Name => preferred.IsAvailable(out _) ? preferred.Name : fallback.Name;

    public bool IsAvailable(out string problem)
    {
        if (preferred.IsAvailable(out _))
        {
            problem = "";
            return true;
        }
        return fallback.IsAvailable(out problem);
    }

    public Task<FileSearchResult> SearchAsync(FileSearchQuery query, CancellationToken ct) =>
        preferred.IsAvailable(out _) ? preferred.SearchAsync(query, ct) : fallback.SearchAsync(query, ct);
}
