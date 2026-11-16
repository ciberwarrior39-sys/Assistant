namespace Assistant.Core.Diagnostics;

/// <summary>
/// Developer log for crashes, errors and timings, kept apart from the user's data folder.
/// Never write what the user typed or what the model answered here.
/// </summary>
public static partial class DiagnosticLog
{
    static readonly Lock Gate = new();

    public static string FolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Assistant", "logs");

    /// <summary>The app's own log files (yyyy-MM-dd.log) are kept this long, then deleted at startup.</summary>
    public const int KeepDays = 30;

    public static void Info(string message) => Write("INFO ", message);

    /// <summary>Deletes log files older than <see cref="KeepDays"/>. Never throws.</summary>
    public static void DeleteOld()
    {
        try
        {
            if (!Directory.Exists(FolderPath)) return;
            DateTime cutoff = DateTime.Today.AddDays(-KeepDays);
            int deleted = 0;
            foreach (string file in Directory.EnumerateFiles(FolderPath, "*.log").ToList())
            {
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime day)
                    || day >= cutoff) continue;
                File.Delete(file);
                deleted++;
            }
            if (deleted > 0) Info($"Deleted {deleted} app log file(s) older than {KeepDays} days");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never crash the app; the next start tries again.
        }
    }

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {Describe(exception)}");

    /// <summary>
    /// An exception for the log: its type, its message with file and folder paths taken out (Windows puts the full
    /// path in "could not find file…" and "access denied…" messages, and those are the user's file names), and where
    /// in the code it happened. Inner exceptions the same way.
    /// </summary>
    public static string Describe(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (text.Length > 0) text.Append(Environment.NewLine).Append(" ---> ");
            text.Append(current.GetType().FullName).Append(": ").Append(WithoutPaths(current.Message));
            if (current.StackTrace is { Length: > 0 } stack) text.Append(Environment.NewLine).Append(stack);
        }
        return text.ToString();
    }

    /// <summary>The text with anything that looks like a Windows or network path replaced by "&lt;path&gt;".</summary>
    public static string WithoutPaths(string text)
    {
        // A quoted path first (it may have spaces), then a bare one up to the next space.
        text = QuotedPath().Replace(text, "'<path>'");
        return BarePath().Replace(text, "<path>");
    }

    [System.Text.RegularExpressions.GeneratedRegex("""['"](?:[A-Za-z]:[\\/]|\\\\)[^'"\r\n]*['"]""")]
    private static partial System.Text.RegularExpressions.Regex QuotedPath();

    [System.Text.RegularExpressions.GeneratedRegex("""(?:\b[A-Za-z]:[\\/]|\\\\)[^\s'"<>|]+""")]
    private static partial System.Text.RegularExpressions.Regex BarePath();

    /// <summary>True while the checks run (tests\Assistant.Checks), so they leave the app's own log alone.</summary>
    public static bool Quiet { get; set; }

    static void Write(string level, string message)
    {
        if (Quiet) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(FolderPath);
                string file = Path.Combine(FolderPath, $"{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never crash the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
