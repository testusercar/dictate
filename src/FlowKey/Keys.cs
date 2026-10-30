using System.Runtime.InteropServices;

namespace FlowKey;

/// <summary>Injected key presses: letting go of the Copilot key's Win + Shift, and earning foreground rights.</summary>
internal static class Keys
{
    public const ushort LShift = 0xA0;
    public const ushort LWin = 0x5B;
    public const ushort F23 = 0x86;

    /// <summary>Unassigned: pressing it between Win's press and release keeps Start from opening, and does nothing else.</summary>
    public const ushort Unassigned = 0xE8;

    public static void SendUp(ushort vk) => Send(Key(vk, up: true));

    public static void SendTap(ushort vk) => Send(Key(vk, up: false), Key(vk, up: true));

    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC),
                dwFlags = (up ? KEYEVENTF_KEYUP : 0u) | (vk == LWin ? KEYEVENTF_EXTENDEDKEY : 0u),
            },
        },
    };

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
}
