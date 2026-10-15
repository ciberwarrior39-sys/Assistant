using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Assistant.Core.Setup;

public sealed record GpuInfo(string Name, string Vendor, long VramBytes)
{
    public double VramGb => VramBytes / 1024.0 / 1024 / 1024;
}

/// <summary>What the wizard found and what it suggests. Model is the local model to download.</summary>
public sealed record HardwarePlan(GpuInfo? Gpu, double RamGb, string Model, bool NeedsRocm, bool CloudRecommended, string Summary);

/// <summary>
/// Picks the local model from the graphics card, following the setup plan: NVIDIA 4–6 GB gets qwen3.5:2b,
/// 8 GB or more gets the bigger qwen3.5:4b, AMD needs Ollama's ROCm package, and without a usable GPU
/// a tiny model runs on the CPU with the free cloud recommended. Reads the driver's registry entries only.
/// </summary>
public static class HardwareCheck
{
    const string DisplayAdapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static HardwarePlan Detect()
    {
        double ram = TotalRamGb();
        GpuInfo? gpu = Gpus()
            .Where(card => card.Vendor is "NVIDIA" or "AMD") // Intel graphics share system memory: not usable for this
            .OrderByDescending(card => card.VramBytes)
            .FirstOrDefault();

        if (gpu is { VramGb: >= 7.5 })
            return new HardwarePlan(gpu, ram, "qwen3.5:4b", gpu.Vendor == "AMD", false,
                $"{gpu.Name} with {gpu.VramGb:0} GB of video memory: room for the bigger qwen3.5:4b model.");
        if (gpu is { VramGb: >= 3.5 })
            return new HardwarePlan(gpu, ram, "qwen3.5:2b", gpu.Vendor == "AMD", false,
                $"{gpu.Name} with {gpu.VramGb:0} GB of video memory: qwen3.5:2b fits it well.");

        return new HardwarePlan(gpu, ram, "qwen3.5:0.8b", false, true,
            (gpu is null ? "No graphics card that can run models was found" : $"{gpu.Name} has too little video memory")
            + ", so a tiny model (qwen3.5:0.8b) will run on the processor. It's slower and simpler: "
            + "connecting the free cloud chat on the last page is recommended.");
    }

    /// <summary>Graphics cards from their driver entries. The 64-bit memory value is used because the 32-bit one stops at 4 GB.</summary>
    public static IReadOnlyList<GpuInfo> Gpus()
    {
        var cards = new List<GpuInfo>();
        try
        {
            using RegistryKey? adapters = Registry.LocalMachine.OpenSubKey(DisplayAdapters);
            if (adapters is null) return cards;

            foreach (string name in adapters.GetSubKeyNames().Where(name => name.Length == 4 && name.All(char.IsDigit)))
            {
                try
                {
                    using RegistryKey? adapter = adapters.OpenSubKey(name);
                    if (adapter?.GetValue("DriverDesc") is not string description) continue;

                    long vram = adapter.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long value => value,
                        byte[] { Length: >= 8 } bytes => BitConverter.ToInt64(bytes, 0),
                        _ => adapter.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int value => (uint)value,
                            byte[] { Length: >= 4 } bytes => BitConverter.ToUInt32(bytes, 0),
                            _ => 0,
                        },
                    };
                    string provider = adapter.GetValue("ProviderName") as string ?? "";
                    cards.Add(new GpuInfo(description, Vendor(description + " " + provider), vram));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    // Some entries are protected; they're never the graphics card itself.
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
        return cards;
    }

    static string Vendor(string text) =>
        text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
        : text.Contains("AMD", StringComparison.OrdinalIgnoreCase) || text.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
          || text.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ? "AMD"
        : text.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
        : "Other";

    static double TotalRamGb()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys / 1024.0 / 1024 / 1024 : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
