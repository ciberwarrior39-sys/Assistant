using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Assistant.Core.Actions;
using Assistant.Core.Diagnostics;
using Assistant.Core.Storage;

namespace Assistant.App;

/// <summary>
/// Custom shortcuts: each key combination in config.json is registered with Windows, and pressing it runs its command
/// as if it were typed to the assistant. Like push-to-talk, this is RegisterHotKey, not a keyboard hook: Windows tells
/// the app only when one of these exact combinations is pressed. Paused during games (if set), so games get the keys.
/// </summary>
public sealed class ShortcutKeys : IShortcutBook, IDisposable
{
    const int FirstId = 0x6100;
    const int WmHotkey = 0x0312;
    const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    readonly HwndSource source;
    readonly AppConfig config;
    readonly string configPath;
    readonly string pushToTalkKeys;
    readonly Dictionary<int, Shortcut> registered = [];
    readonly HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase); // refused by Windows: another app has them
    bool paused;

    /// <summary>A shortcut was pressed.</summary>
    public event Action<Shortcut>? Triggered;

    public ShortcutKeys(Window owner, AppConfig config, string configPath, string pushToTalkKeys)
    {
        this.config = config;
        this.configPath = configPath;
        this.pushToTalkKeys = pushToTalkKeys;
        source = HwndSource.FromHwnd(new WindowInteropHelper(owner).EnsureHandle())!;
        source.AddHook(OnMessage);
        Apply();
    }

    /// <summary>During a game (with "pause in games" on): none registered, so the game gets every key.</summary>
    public bool Paused
    {
        get => paused;
        set
        {
            if (paused == value) return;
            paused = value;
            Apply();
        }
    }

    /// <summary>Registers the shortcuts in config.json again (after a change in Settings or by chat).</summary>
    public void Apply()
    {
        foreach (int id in registered.Keys) UnregisterHotKey(source.Handle, id);
        registered.Clear();
        taken.Clear();
        if (paused) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int next = FirstId;
        foreach (Shortcut shortcut in config.Shortcuts)
        {
            if (HotkeyText.Normalize(shortcut.Keys) is not { } keys || string.IsNullOrWhiteSpace(shortcut.Command)) continue;
            if (!seen.Add(keys) || HotkeyText.Same(keys, pushToTalkKeys) || Parse(keys) is not { } parsed) continue;
            int id = next++;
            if (RegisterHotKey(source.Handle, id, parsed.Modifiers | ModNoRepeat, (uint)parsed.VirtualKey))
                registered[id] = shortcut;
            else
                taken.Add(keys);
        }
    }

    /// <summary>Runs a shortcut as if its keys were pressed (Settings > Shortcuts > Test).</summary>
    public void Run(Shortcut shortcut)
    {
        if (!string.IsNullOrWhiteSpace(shortcut.Command)) Triggered?.Invoke(shortcut);
    }

    /// <summary>What's wrong with these keys right now, or null when they work (Settings shows it on the row).</summary>
    public string? Problem(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) return null; // not set yet
        if (HotkeyText.Normalize(keys) is not { } tidy || Parse(tidy) is null)
            return "Use Ctrl, Alt or Win together with one other key.";
        if (HotkeyText.Same(tidy, pushToTalkKeys)) return "These are the push-to-talk keys.";
        if (config.Shortcuts.Count(shortcut => HotkeyText.Same(shortcut.Keys, tidy)) > 1) return "Two shortcuts use these keys; only the first works.";
        if (taken.Contains(tidy)) return "Another program already uses these keys, so Windows won't give them to the assistant.";
        return null;
    }

    // ---- By chat: "make Alt+S open Spotify" -----------------------------------------------------------------------

    public string Add(string keys, string command)
    {
        if (HotkeyText.Same(keys, pushToTalkKeys)) return $"{keys} is already the push-to-talk key, so pick another combination.";
        Shortcut? existing = config.Shortcuts.FirstOrDefault(shortcut => HotkeyText.Same(shortcut.Keys, keys));
        if (existing is not null) existing.Command = command;
        else config.Shortcuts.Add(new Shortcut { Keys = keys, Command = command });
        if (!Save()) return "I couldn't save the shortcut (config.json couldn't be written).";
        Apply();

        if (taken.Contains(keys))
            return $"Saved, but another program already uses {keys}, so Windows won't give it to me. Try another combination " +
                   "(Settings > Shortcuts lists them all).";
        return (existing is null ? $"Done: {keys} now runs “{command}”." : $"Changed: {keys} now runs “{command}”.") +
               (paused ? " Shortcuts are paused while a game runs." : "");
    }

    public string Remove(string keys)
    {
        int removed = config.Shortcuts.RemoveAll(shortcut => HotkeyText.Same(shortcut.Keys, keys));
        if (removed == 0) return $"There's no shortcut on {keys}.";
        if (!Save()) return "I couldn't save the change (config.json couldn't be written).";
        Apply();
        return $"Removed the {keys} shortcut.";
    }

    public string List()
    {
        var usable = config.Shortcuts.Where(shortcut => shortcut.Keys.Length > 0 && shortcut.Command.Length > 0).ToList();
        if (usable.Count == 0)
            return "You don't have any shortcuts yet. Say for example \"make Alt+S open Spotify\", or add them in Settings > Shortcuts.";
        return "Your shortcuts:\n" + string.Join("\n", usable.Select(shortcut =>
            $"• {HotkeyText.Normalize(shortcut.Keys) ?? shortcut.Keys}: {shortcut.Command}" +
            (Problem(shortcut.Keys) is { } problem ? $" ({problem})" : "")));
    }

    bool Save()
    {
        try
        {
            config.Save(configPath);
            return true;
        }
        catch (IOException ex)
        {
            DiagnosticLog.Error("Could not save config.json", ex);
            return false;
        }
    }

    // ---- Windows' side ---------------------------------------------------------------------------------------------

    IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey || !registered.TryGetValue(wParam.ToInt32(), out Shortcut? shortcut)) return IntPtr.Zero;
        handled = true;
        Triggered?.Invoke(shortcut);
        return IntPtr.Zero;
    }

    /// <summary>"Ctrl+Alt+P" → modifier flags and the virtual key code. Digits are the number-row keys.</summary>
    static (uint Modifiers, int VirtualKey)? Parse(string keys)
    {
        uint modifiers = 0;
        int virtualKey = 0;
        foreach (string part in keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win": modifiers |= ModWin; break;
                default:
                    // "1" is the 1 key (D1), not the key whose number is 1.
                    string name = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
                    if (int.TryParse(name, out _) || !Enum.TryParse(name, ignoreCase: true, out Key key) || key == Key.None) return null;
                    virtualKey = KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return virtualKey != 0 && (modifiers & (ModControl | ModAlt | ModWin)) != 0 ? (modifiers, virtualKey) : null;
    }

    public void Dispose()
    {
        foreach (int id in registered.Keys) UnregisterHotKey(source.Handle, id);
        registered.Clear();
        source.RemoveHook(OnMessage);
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
