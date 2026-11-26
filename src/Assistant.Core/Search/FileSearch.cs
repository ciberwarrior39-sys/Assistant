namespace Assistant.Core.Search;

public sealed record FileHit(string FullPath, DateTime Modified)
{
    public string FileName => Path.GetFileName(FullPath);
}

/// <param name="Folders">Look for folders instead of files ("find the daskalo folder"); extensions are ignored then.</param>
public sealed record FileSearchQuery(
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Extensions,
    DateTime? ModifiedAfter,
    int MaxResults,
    bool Folders = false);

public sealed record FileSearchResult(bool Available, IReadOnlyList<FileHit> Files, string Problem = "")
{
    public static FileSearchResult Unavailable(string problem) => new(false, [], problem);
}

/// <summary>A file index: Everything now, Windows Search as a fallback later.</summary>
public interface IFileSearch
{
    string Name { get; }
    bool IsAvailable(out string problem);
    Task<FileSearchResult> SearchAsync(FileSearchQuery query, CancellationToken ct);
}
