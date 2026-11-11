using System.ComponentModel;
using System.Diagnostics;
using Assistant.Core.Diagnostics;

namespace Assistant.Core.Setup;

/// <summary>
/// Disk space taken by the app's own Ollama (%LocalAppData%\Assistant\ollama and ollama-models). Never touches an
/// Ollama the user installed themselves.
///
/// Ollama's Windows zip carries the graphics-card libraries for every kind of PC: NVIDIA's CUDA twice (v12 and v13,
/// about 1.8 GB together) plus Vulkan for AMD and Intel. A PC uses one of them. Once the model has run on the graphics
/// card, the folder Ollama actually loaded is known, and the others are deleted. If that can't be seen for certain
/// (the model runs on the processor, or more than one folder is loaded), nothing is deleted.
/// </summary>
public static class OllamaSpace
{
    // The graphics libraries, one folder each under lib\ollama ("cuda_v12", "cuda_v13", "vulkan", "rocm").
    static readonly string[] GpuFolderStarts = ["cuda", "rocm", "vulkan", "hip"];

    // What Ollama's processes are called: the server, and the runners that hold the model.
    static readonly string[] ProcessNames = ["ollama", "llama-server"];

    static string Libraries => Path.Combine(OllamaRuntime.Folder, "lib", "ollama");

    /// <summary>
    /// Deletes the graphics libraries this PC doesn't use, when the one in use can be seen. Returns the bytes freed
    /// (0 when there was nothing to do or it wasn't certain). Cheap once done: one folder left means it returns at once.
    /// </summary>
    public static long RemoveUnusedGpuLibraries()
    {
        List<string> gpuFolders;
        try
        {
            if (!Directory.Exists(Libraries)) return 0;
            gpuFolders = Directory.GetDirectories(Libraries).Where(IsGpuFolder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        if (gpuFolders.Count <= 1) return 0;

        IReadOnlySet<string> loaded = LoadedGpuFolders();
        if (loaded.Count != 1) return 0; // on the processor, not loaded yet, or unclear: keep everything

        long freed = 0;
        foreach (string folder in gpuFolders.Where(folder => !loaded.Contains(Path.GetFileName(folder))))
            freed += DeleteFolder(folder);
        if (freed > 0)
            DiagnosticLog.Info($"Removed graphics libraries this PC doesn't use ({freed / 1_000_000} MB); kept {loaded.First()}");
        return freed;
    }

    /// <summary>The app's Ollama and its models on disk, in bytes (0 when they aren't there).</summary>
    public static long InstalledBytes() => FolderBytes(OllamaRuntime.Folder) + FolderBytes(OllamaRuntime.ModelsFolder);

    /// <summary>
    /// Removes the app's Ollama and its models (after a switch to Lite, when the user agreed). Stops any copy of it
    /// that is still running first. Returns the bytes freed; whatever was in use and couldn't go is left for next time.
    /// </summary>
    public static long Remove()
    {
        foreach (Process process in OwnProcesses())
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Already gone, or not ours to stop.
                }
            }
        }

        long freed = DeleteFolder(OllamaRuntime.Folder) + DeleteFolder(OllamaRuntime.ModelsFolder);
        DiagnosticLog.Info($"Removed the local model and the app's Ollama ({freed / 1_000_000} MB)");
        return freed;
    }

    static bool IsGpuFolder(string folder)
    {
        string name = Path.GetFileName(folder);
        return GpuFolderStarts.Any(start => name.StartsWith(start, StringComparison.OrdinalIgnoreCase));
    }

    // The graphics folders whose libraries are loaded in the app's Ollama right now ("cuda_v13").
    static IReadOnlySet<string> LoadedGpuFolders()
    {
        string prefix = Libraries.TrimEnd('\\') + "\\";
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in OwnProcesses())
        {
            using (process)
            {
                try
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        string file = module.FileName;
                        if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        string[] parts = file[prefix.Length..].Split('\\');
                        // Only the graphics library itself (ggml-cuda.dll, ggml-vulkan.dll …) counts; the Visual C++
                        // files next to it could be loaded from any of the folders.
                        if (parts.Length == 2 && IsGpuFolder(parts[0]) && parts[1].StartsWith("ggml-", StringComparison.OrdinalIgnoreCase))
                            loaded.Add(parts[0]);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Exited meanwhile, or can't be read: it tells us nothing.
                }
            }
        }
        return loaded;
    }

    // Ollama processes started from the app's own folder (not an Ollama the user installed).
    static IEnumerable<Process> OwnProcesses()
    {
        string own = OllamaRuntime.Folder.TrimEnd('\\') + "\\";
        foreach (string name in ProcessNames)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Another user's, or gone.
                }
                if (path is not null && path.StartsWith(own, StringComparison.OrdinalIgnoreCase)) yield return process;
                else process.Dispose();
            }
        }
    }

    static long FolderBytes(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // Deletes a folder, file by file, so one file in use doesn't stop the rest. Returns the bytes freed.
    static long DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        long freed = 0;
        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList())
        {
            try
            {
                long size = file.Length;
                file.Delete();
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Error($"Couldn't delete {file.Name}", ex);
            }
        }
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file was still in use; it goes next time.
        }
        return freed;
    }
}
