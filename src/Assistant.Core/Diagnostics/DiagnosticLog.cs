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

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    static void Write(string level, string message)
    {
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
