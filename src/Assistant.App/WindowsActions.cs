using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Assistant.Core.Actions;
using Assistant.Core.Diagnostics;
using Assistant.Core.Search;

namespace Assistant.App;

/// <summary>
/// Does what the user asked on the PC: opens apps, folders, websites and Settings pages, searches the web, changes the
/// volume, controls music and video, locks the PC, switches dark/light mode, takes screenshots and empties the Recycle
/// Bin (after asking). No key presses are simulated: volume goes through Windows' audio interface and media control
/// through a Windows command message, so nothing here looks like input automation to anti-cheat software.
/// </summary>
public sealed partial class WindowsActions(
    Func<string> searchEngine,
    Func<IEnumerable<string>> placeFacts,
    Func<bool> gameMode,
    Action openAssistantSettings,
    Action<bool> showAssistant,
    Func<IntPtr> ownWindow) : IActionRunner
{
    const int VolumeStep = 10;

    readonly AppCatalog catalog = new();

    // Websites people open by name; anything that looks like an address works too ("github.com").
    static readonly Dictionary<string, string> Sites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube"] = "https://www.youtube.com", ["google"] = "https://www.google.com", ["gmail"] = "https://mail.google.com",
        ["google drive"] = "https://drive.google.com", ["drive"] = "https://drive.google.com", ["google maps"] = "https://maps.google.com",
        ["maps"] = "https://maps.google.com", ["google translate"] = "https://translate.google.com", ["translate"] = "https://translate.google.com",
        ["github"] = "https://github.com", ["chatgpt"] = "https://chatgpt.com", ["claude"] = "https://claude.ai",
        ["facebook"] = "https://www.facebook.com", ["instagram"] = "https://www.instagram.com", ["twitter"] = "https://x.com",
        ["x"] = "https://x.com", ["tiktok"] = "https://www.tiktok.com", ["reddit"] = "https://www.reddit.com",
        ["twitch"] = "https://www.twitch.tv", ["netflix"] = "https://www.netflix.com", ["wikipedia"] = "https://www.wikipedia.org",
        ["linkedin"] = "https://www.linkedin.com", ["outlook"] = "https://outlook.live.com", ["discord"] = "https://discord.com/app",
        ["spotify"] = "https://open.spotify.com", ["amazon"] = "https://www.amazon.com", ["stack overflow"] = "https://stackoverflow.com",
        ["stackoverflow"] = "https://stackoverflow.com", ["pinterest"] = "https://www.pinterest.com",
    };

    public async Task<ActionReply?> RunAsync(ActionIntent intent, CancellationToken ct)
    {
        try
        {
            return intent switch
            {
                OpenTarget target => Open(target),
                LaunchApp app => Launch(new InstalledApp(app.Name, app.Id)),
                WebSearch search => SearchWeb(search.Query),
                VolumeChange volume => Volume(volume),
                MediaCommand media => Media(media.Op, ownWindow()),
                OpenSettings settings => Settings(settings),
                LockPc => Lock(),
                SetTheme theme => Theme(theme.Dark),
                TakeScreenshot => await ScreenshotAsync(ct),
                EmptyRecycleBin bin => RecycleBin(bin.Confirmed),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or COMException
                                       or InvalidOperationException or ExternalException)
        {
            DiagnosticLog.Error($"Action {intent.GetType().Name} failed", ex);
            return new ActionReply($"That didn't work: {ex.Message}");
        }
    }

    // ---- Open ----------------------------------------------------------------------------------------------------

    // Folders (Windows' own and the user's saved places), then web addresses, then apps, then known websites.
    // "website", "folder" or "app" in the request narrows it to that kind.
    ActionReply? Open(OpenTarget target)
    {
        string name = target.Name;
        switch (target.Kind)
        {
            case OpenKind.Folder:
                return Folder(name) is { } onlyFolder ? OpenFolder(onlyFolder) : null;
            case OpenKind.Site:
                return Address(name) is { } onlySite ? OpenSite(onlySite) : null;
        }

        if (target.Kind == OpenKind.Any)
        {
            if (Folder(name) is { } folder) return OpenFolder(folder);
            if (name.Contains('.') && Address(name) is { } typed) return OpenSite(typed); // "github.com"
        }

        // An installed app, unless it's a well-known website and only a weak match ("gmail" isn't the Mail app).
        (IReadOnlyList<InstalledApp> apps, int score) = catalog.Find(name);
        bool knownSite = target.Kind == OpenKind.Any && Sites.ContainsKey(name.Trim());
        if (apps.Count > 0 && !(knownSite && score < 80))
        {
            if (apps.Count == 1) return Launch(apps[0]);
            return new ActionReply($"Several apps match \"{name}\". Which one?")
            {
                Options = [.. apps.Select(app => new ActionOption(app.Name, new LaunchApp(app.Name, app.Id))), new ActionOption("Cancel", null)],
            };
        }

        if (target.Kind != OpenKind.App && Address(name) is { } site) return OpenSite(site); // "youtube"
        return null; // not something to open: file search may know it ("open my CV")
    }

    static ActionReply OpenFolder(string folder)
    {
        Shell.OpenFolder(folder);
        string leaf = Path.GetFileName(folder.TrimEnd('\\'));
        return new ActionReply($"Opened {(leaf.Length > 0 ? leaf : folder)}.");
    }

    static ActionReply Launch(InstalledApp app)
    {
        AppCatalog.Launch(app);
        return new ActionReply($"Opening {app.Name}.");
    }

    static ActionReply OpenSite(string url)
    {
        using Process? _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return new ActionReply($"Opened {new Uri(url).Host} in your browser.");
    }

    // A known site name, or something that looks like a web address. Only http(s) is ever opened.
    static string? Address(string name)
    {
        string clean = name.Trim().ToLowerInvariant();
        if (Sites.TryGetValue(clean, out string? known)) return known;
        if (!WebAddress().IsMatch(clean)) return null;
        string url = clean.StartsWith("http://", StringComparison.Ordinal) || clean.StartsWith("https://", StringComparison.Ordinal)
            ? clean : "https://" + clean;
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    // Windows' own folders by name, then folders the user saved with "remember…" (places.md).
    string? Folder(string name)
    {
        string clean = name.Trim().ToLowerInvariant().Replace(" folder", "");
        string? known = clean switch
        {
            "downloads" or "download" or "изтегляния" or "изтеглянията" or "свалени" => KnownFolder(Downloads),
            "documents" or "docs" or "документи" or "документите" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "desktop" or "работния плот" or "десктоп" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "pictures" or "photos" or "images" or "снимки" or "снимките" or "картинки" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" or "музика" or "музиката" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" or "видеа" or "видеата" or "видео" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "screenshots" or "скрийншоти" => ScreenshotsFolder,
            "home" or "user folder" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _ => null,
        };
        if (known is not null && Directory.Exists(known)) return known;

        // "open my Unity projects folder" when places.md has "Keeps Unity projects in D:\Unity".
        foreach (string fact in placeFacts())
        {
            if (!Assistant.Core.Organizer.TextMatch.Matches(fact, clean)) continue;
            foreach (Match path in SavedPath().Matches(fact))
            {
                string folder = path.Value.TrimEnd('.', ',', ')');
                if (Directory.Exists(folder)) return folder;
            }
        }
        return null;
    }

    ActionReply SearchWeb(string query)
    {
        string engine = searchEngine();
        string url = engine switch
        {
            "duckduckgo" => "https://duckduckgo.com/?q=",
            "bing" => "https://www.bing.com/search?q=",
            _ => "https://www.google.com/search?q=",
        } + Uri.EscapeDataString(query);
        using Process? _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        string name = engine switch { "duckduckgo" => "DuckDuckGo", "bing" => "Bing", _ => "Google" };
        return new ActionReply($"Searching {name} for \"{query}\" in your browser.");
    }

    ActionReply Settings(OpenSettings page)
    {
        if (page.Link == ActionParser.AssistantSettings)
        {
            openAssistantSettings();
            return new ActionReply("Here are my settings. For Windows' own, say \"Windows settings\".");
        }
        using Process? _ = Process.Start(new ProcessStartInfo(page.Link) { UseShellExecute = true });
        return new ActionReply(page.Link == "ms-settings:" ? "Opened Windows Settings." : $"Opened {page.Page} settings.");
    }

    // ---- Sound and media -----------------------------------------------------------------------------------------

    static ActionReply Volume(VolumeChange change)
    {
        using var devices = new MMDeviceEnumerator();
        using MMDevice speakers = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        AudioEndpointVolume volume = speakers.AudioEndpointVolume;
        int now = (int)Math.Round(volume.MasterVolumeLevelScalar * 100);

        switch (change.Op)
        {
            case VolumeOp.Query:
                return new ActionReply(volume.Mute ? $"The sound is muted (volume {now}%)." : $"The volume is at {now}%.");
            case VolumeOp.Mute:
                volume.Mute = true;
                return new ActionReply("Muted.");
            case VolumeOp.Unmute:
                volume.Mute = false;
                return new ActionReply($"Sound is back on, at {now}%.");
        }

        int target = change.Op switch
        {
            VolumeOp.Up => now + VolumeStep,
            VolumeOp.Down => now - VolumeStep,
            _ => change.Percent,
        };
        target = Math.Clamp(target, 0, 100);
        volume.MasterVolumeLevelScalar = target / 100f;
        if (target > 0 && volume.Mute) volume.Mute = false;
        return new ActionReply($"Volume {target}%.");
    }

    // The same command a media key turns into: sent to the assistant's own window, which passes it on unhandled, so
    // Windows routes it to whatever is playing (Spotify, a browser tab…) exactly as it does for the keyboard's media keys.
    static ActionReply Media(MediaOp op, IntPtr window)
    {
        int command = op switch
        {
            MediaOp.Next => AppCommandNextTrack,
            MediaOp.Previous => AppCommandPreviousTrack,
            _ => AppCommandPlayPause,
        };
        if (window == IntPtr.Zero) return new ActionReply("I couldn't reach the media controls.");
        SendMessage(window, WmAppCommand, window, (IntPtr)(command << 16));
        return new ActionReply(op switch
        {
            MediaOp.Next => "Next track.",
            MediaOp.Previous => "Previous track.",
            _ => "Play/pause.",
        });
    }

    // ---- System --------------------------------------------------------------------------------------------------

    static ActionReply Lock()
    {
        if (!LockWorkStation()) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new ActionReply("Locked.");
    }

    static ActionReply Theme(bool dark)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: true);
        key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        // Tell open windows, so the taskbar and apps switch now.
        SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "ImmersiveColorSet", SmtoAbortIfHung, 200, out _);
        return new ActionReply(dark ? "Switched Windows to dark mode." : "Switched Windows to light mode.");
    }

    static string ScreenshotsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    // The whole screen (every monitor), saved as a PNG in Pictures\Screenshots. The assistant hides itself first.
    async Task<ActionReply> ScreenshotAsync(CancellationToken ct)
    {
        if (gameMode())
            return new ActionReply("Game mode is on, so I don't capture the screen. The game's own screenshot key, or Win+Shift+S, works.");

        showAssistant(false);
        try
        {
            await Task.Delay(300, ct); // let the bubble and character disappear from the screen
            System.Drawing.Rectangle screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var image = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(image))
                graphics.CopyFromScreen(screen.Left, screen.Top, 0, 0, screen.Size);

            Directory.CreateDirectory(ScreenshotsFolder);
            string file = Path.Combine(ScreenshotsFolder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            image.Save(file, ImageFormat.Png);
            return new ActionReply("Screenshot saved in Pictures\\Screenshots.") { Files = [new FileHit(file, DateTime.Now)] };
        }
        finally
        {
            showAssistant(true);
        }
    }

    // Asks first, with the number of items and their size; the button runs it with Confirmed.
    static ActionReply RecycleBin(bool confirmed)
    {
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        if (SHQueryRecycleBin(null, ref info) != 0) info.Items = -1;

        if (!confirmed)
        {
            if (info.Items == 0) return new ActionReply("The Recycle Bin is already empty.");
            string what = info.Items > 0 ? $"{info.Items:N0} item{(info.Items == 1 ? "" : "s")}, {Size(info.Bytes)}" : "everything in it";
            return new ActionReply($"Empty the Recycle Bin ({what})? This can't be undone.")
            {
                Options = [new ActionOption("Empty the Recycle Bin", new EmptyRecycleBin(true)), new ActionOption("Cancel", null)],
            };
        }

        int result = SHEmptyRecycleBin(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
        // S_OK, or "already empty" (0x8000FFFF on some versions).
        return new ActionReply(result == 0 || info.Items == 0 ? "Emptied the Recycle Bin." : $"Windows couldn't empty it (error 0x{result:X8}).");
    }

    static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{bytes / 1024.0:0} KB";

    /// <summary>The user's Downloads folder (it can be moved, so it's asked from Windows).</summary>
    public static string? DownloadsFolder => KnownFolder(Downloads);

    static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr path) != 0) return null;
        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    // ---- Windows ---------------------------------------------------------------------------------------------------

    static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    const int WmAppCommand = 0x0319;
    const int AppCommandNextTrack = 11;
    const int AppCommandPreviousTrack = 12;
    const int AppCommandPlayPause = 14;
    const int WmSettingChange = 0x001A;
    static readonly IntPtr HwndBroadcast = new(0xFFFF);
    const uint SmtoAbortIfHung = 0x0002;
    const uint SherbNoConfirmation = 0x1, SherbNoProgressUi = 0x2, SherbNoSound = 0x4;

    // SHQUERYRBINFO, with the natural 8-byte packing of 64-bit Windows.
    [StructLayout(LayoutKind.Sequential)]
    struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [GeneratedRegex(@"^(?:https?://)?(?:[a-z0-9-]+\.)+[a-z]{2,}(?:/\S*)?$")]
    private static partial Regex WebAddress();

    [GeneratedRegex(@"[A-Za-z]:\\[^""<>|?*\r\n]*")]
    private static partial Regex SavedPath();

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr window, int message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)] static extern bool LockWorkStation();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string? rootPath, ref RecycleBinInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr window, string? rootPath, uint flags);
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
