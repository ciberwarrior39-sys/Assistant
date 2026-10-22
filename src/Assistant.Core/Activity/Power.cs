using System.Runtime.InteropServices;

namespace Assistant.Core.Activity;

/// <summary>
/// Windows' power settings: when the screen turns off by itself, and whether an app (a video player, say)
/// is keeping it on right now. Read-only; the assistant never changes power settings.
/// </summary>
public static class Power
{
    static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    static readonly Guid VideoIdleTimeout = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"); // "Turn off display after"
    const int SystemExecutionState = 16;
    const uint DisplayRequired = 0x2;

    /// <summary>"Turn off my screen after" for the current plan, on battery or plugged in. Null when set to never.</summary>
    public static TimeSpan? ScreenOffAfter()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr schemePointer) != 0) return null;
        try
        {
            Guid scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            Guid subgroup = VideoSubgroup, setting = VideoIdleTimeout;
            bool onBattery = GetSystemPowerStatus(out SYSTEM_POWER_STATUS status) && status.ACLineStatus == 0;
            uint seconds;
            uint result = onBattery
                ? PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out seconds)
                : PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out seconds);
            return result == 0 && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }
        finally
        {
            LocalFree(schemePointer);
        }
    }

    /// <summary>
    /// True when something asks Windows to keep the screen on (most video players do).
    /// Best effort: an app that doesn't tell Windows can't be detected.
    /// </summary>
    public static bool ScreenKeptOn()
    {
        uint state = 0;
        return CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, ref state, sizeof(uint)) == 0
               && (state & DisplayRequired) != 0;
    }

    /// <summary>Whether this PC has a battery, whether it's plugged in, and the charge (0–100).</summary>
    public static (bool HasBattery, bool PluggedIn, int Percent) Battery()
    {
        if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS status)) return (false, true, 100);
        bool hasBattery = status.BatteryFlag != 255 && (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent <= 100;
        return (hasBattery, status.ACLineStatus != 0, status.BatteryLifePercent);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, ref uint output, uint outputLength);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
}
