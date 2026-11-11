namespace Assistant.Core.Diagnostics;

/// <summary>
/// Developer log for crashes, errors and timings, kept apart from the user's data folder.
/// Never write what the user typed or what the model answered here.
/// </summary>
public static class DiagnosticLog
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
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

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
