using System.Globalization;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Storage;

/// <summary>
/// Deletes raw logs (activity, feedback, sent, conversation notes) older than the retention period.
/// Memory files are never touched: what you chose to keep stays.
/// </summary>
public static class Retention
{
    public static readonly TimeSpan RawLogs = TimeSpan.FromDays(7);

    public static int Purge(DataFolder data, TimeSpan keep)
    {
        DateTime cutoff = DateTime.Today - keep;
        int deleted = 0;

        foreach (string folder in new[] { data.ActivityFolder, data.FeedbackFolder, data.SentFolder, data.ConversationsFolder })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                // Daily files are named by date; anything else goes by when it was last written.
                DateTime day = DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
                    ? parsed
                    : File.GetLastWriteTime(file).Date;
                if (day >= cutoff) continue;

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (IOException ex)
                {
                    DiagnosticLog.Error("Could not delete an old log file", ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    DiagnosticLog.Error("Could not delete an old log file", ex);
                }
            }
        }

        if (deleted > 0) DiagnosticLog.Info($"Deleted {deleted} log file(s) older than {keep.TotalDays} days");
        return deleted;
    }
}
