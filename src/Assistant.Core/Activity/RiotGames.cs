using System.Diagnostics;

namespace Assistant.Core.Activity;

/// <summary>
/// "Close while I play Riot games": is the Riot Client, League or Valorant open?
/// The names come from Windows' list of running programs (the same list Task Manager shows). No Riot process is
/// opened, read or touched in any way.
/// </summary>
public static class RiotGames
{
    // Not Vanguard itself (vgtray, vgc): once installed it runs all the time, so the assistant could never start.
    static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        "RiotClientServices", "Riot Client", "RiotClientUx", "RiotClientUxRender",
        "LeagueClient", "LeagueClientUx", "LeagueClientUxRender", "League of Legends",
        "VALORANT", "VALORANT-Win64-Shipping",
    };

    /// <summary>The first Riot program found (for the log), or null when none is open.</summary>
    public static string? RunningProgram()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                if (Programs.Contains(process.ProcessName)) return process.ProcessName;
            }
            return null;
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }
}
