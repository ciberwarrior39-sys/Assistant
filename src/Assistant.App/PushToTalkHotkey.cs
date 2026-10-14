using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Assistant.App;

/// <summary>
/// Hold-to-talk on a global hotkey. Windows' RegisterHotKey reports only the press, so while recording
/// the key is checked every 50 ms to see when it's released. This is not a keyboard hook: no other keys are seen.
/// </summary>
public sealed class PushToTalkHotkey : IDisposable
{
    const int HotkeyId = 0x5A11;
    const int WmHotkey = 0x0312;
    const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    readonly HwndSource source;
    readonly DispatcherTimer releaseCheck = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly int virtualKey;
    bool held;

    public event Action? Pressed;
    public event Action? Released;

    /// <summary>The keys as written in config.json, e.g. "Ctrl+Alt+Space".</summary>
    public string Keys { get; }

    /// <summary>False when the keys couldn't be read or another app already uses them.</summary>
    public bool IsRegistered { get; }

    public PushToTalkHotkey(Window owner, string keys)
    {
        Keys = keys;
        source = HwndSource.FromHwnd(new WindowInteropHelper(owner).EnsureHandle())!;
        source.AddHook(OnMessage);
        releaseCheck.Tick += (_, _) =>
        {
            if ((GetAsyncKeyState(virtualKey) & 0x8000) != 0) return; // still held
            releaseCheck.Stop();
            held = false;
            Released?.Invoke();
        };

        if (Parse(keys) is { } parsed)
        {
            virtualKey = parsed.VirtualKey;
            IsRegistered = RegisterHotKey(source.Handle, HotkeyId, parsed.Modifiers | ModNoRepeat, (uint)parsed.VirtualKey);
        }
    }

    IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey || wParam.ToInt32() != HotkeyId || held) return IntPtr.Zero;
        held = true;
        handled = true;
        Pressed?.Invoke();
        releaseCheck.Start();
        return IntPtr.Zero;
    }

    /// <summary>"Ctrl+Alt+Space" -> modifier flags and the virtual key code. Null if a part isn't recognized.</summary>
    static (uint Modifiers, int VirtualKey)? Parse(string keys)
    {
        uint modifiers = 0;
        int virtualKey = 0;
        foreach (string part in keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows": modifiers |= ModWin; break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out Key key)) return null;
                    virtualKey = KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        // A bare key would fire while typing, so at least one modifier is required.
        return virtualKey != 0 && modifiers != 0 ? (modifiers, virtualKey) : null;
    }

    public void Dispose()
    {
        releaseCheck.Stop();
        if (IsRegistered) UnregisterHotKey(source.Handle, HotkeyId);
        source.RemoveHook(OnMessage);
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
}
