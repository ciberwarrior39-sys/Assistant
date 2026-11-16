using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Assistant.App;

/// <summary>
/// Whether another program is recording from the microphone right now (a call, a voice message, a recorder), from
/// Windows' own list of audio sessions. Only "is one active", never what is said or by which program.
/// The wake word stops listening meanwhile, so it can't wake up in the middle of a call. A program that has the
/// microphone open all the time (noise removal, headset software) isn't a call: see how App uses this.
/// </summary>
static class MicInUse
{
    /// <summary>
    /// The programs (by process number) recording from the microphone right now, this app left out. Empty when none,
    /// or when Windows won't say.
    /// </summary>
    public static HashSet<uint> ActiveApps()
    {
        var active = new HashSet<uint>();
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)) return active;
            using MMDevice microphone = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            AudioSessionManager manager = microphone.AudioSessionManager;
            manager.RefreshSessions();
            SessionCollection sessions = manager.Sessions;
            uint own = (uint)Environment.ProcessId;
            for (int i = 0; i < sessions.Count; i++)
            {
                using AudioSessionControl session = sessions[i];
                uint program = session.GetProcessID;
                if (session.State == AudioSessionState.AudioSessionStateActive && program != own && program != 0 && !session.IsSystemSoundsSession)
                    active.Add(program);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or NotSupportedException)
        {
            // No microphone, or Windows wouldn't say: nobody, as far as we know.
        }
        return active;
    }
}
