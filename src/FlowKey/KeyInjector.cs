using System.Runtime.InteropServices;

namespace FlowKey;

/// <summary>
/// Presses and releases keyboard shortcuts as if typed, for apps (like Wispr Flow) that listen
/// for a global hotkey. Remembers what it is holding so nothing is left stuck down.
/// </summary>
internal sealed class KeyInjector : IDisposable
{
    private readonly object _gate = new();
    private readonly List<ushort> _held = new();

    public bool IsHolding
    {
        get { lock (_gate) return _held.Count > 0; }
    }

    /// <summary>Presses the keys in order and keeps them down.</summary>
    public void Press(IReadOnlyList<ushort> keys)
    {
        lock (_gate)
        {
            ReleaseLocked();
            Send(keys.Select(k => Key(k, up: false)));
            _held.AddRange(keys);
        }
    }

    /// <summary>Releases whatever <see cref="Press"/> is holding, last key first.</summary>
    public void Release()
    {
        lock (_gate) ReleaseLocked();
    }

    /// <summary>Presses and releases the keys, like a quick chord.</summary>
    public void Tap(IReadOnlyList<ushort> keys)
    {
        lock (_gate)
        {
            ReleaseLocked();
            Send(keys.Select(k => Key(k, up: false)));
            Thread.Sleep(30);
            Send(keys.Reverse().Select(k => Key(k, up: true)));
        }
    }

    /// <summary>Releases a key that is physically reported down but that we never pressed (left over from the Copilot key).</summary>
    public void ReleaseStray(ushort key) => Send(new[] { Key(key, up: true) });

    public static bool IsDown(ushort key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private void ReleaseLocked()
    {
        if (_held.Count == 0) return;
        var keys = Enumerable.Reverse(_held).ToArray();
        _held.Clear();
        // No Start menu masking here: an extra key while the shortcut is down could end Wispr's
        // dictation, and Wispr already keeps Start closed for its own Ctrl + Win.
        Send(keys.Select(k => Key(k, up: true)));
    }

    private static void Send(IEnumerable<INPUT> inputs)
    {
        var array = inputs.ToArray();
        if (array.Length == 0) return;
        if (SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>()) != array.Length)
            throw new InvalidOperationException($"Windows refused the key presses (error {Marshal.GetLastWin32Error()}).");
    }

    // Real scan codes and the extended flag, so the keys look like a physical keyboard's.
    private static INPUT Key(ushort vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0u;
        if (vk is VirtualKeys.LWin or VirtualKeys.RWin or VirtualKeys.RControl or VirtualKeys.RMenu) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC), dwFlags = flags } },
        };
    }

    public void Dispose() => Release();

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // MOUSEINPUT is the largest member, so it sets the union's size.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);
}

internal static class VirtualKeys
{
    public const ushort LShift = 0xA0;
    public const ushort RShift = 0xA1;
    public const ushort LControl = 0xA2;
    public const ushort RControl = 0xA3;
    public const ushort LMenu = 0xA4;
    public const ushort RMenu = 0xA5;
    public const ushort LWin = 0x5B;
    public const ushort RWin = 0x5C;
    public const ushort Space = 0x20;
    public const ushort F23 = 0x86;

    public static string Name(ushort vk) => vk switch
    {
        LShift or RShift => "Shift",
        LControl or RControl => "Ctrl",
        LMenu => "Alt",
        RMenu => "Right Alt",
        LWin or RWin => "Win",
        Space => "Space",
        F23 => "F23",
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"0x{vk:X2}",
    };

    public static string Describe(IEnumerable<ushort> keys) => string.Join(" + ", keys.Select(Name));
}
