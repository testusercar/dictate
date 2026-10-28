using System.Runtime.InteropServices;
using Dictate.Core.Dictation;

namespace Dictate.Platform;

/// <summary>
/// Types text into the window that had focus when dictation started, as Unicode key presses.
/// Windows blocks this for apps running as administrator unless Dictate is elevated too.
/// </summary>
internal sealed class KeyboardTextSink : ITextSink
{
    private const int ChunkSize = 64;
    private IntPtr _target;

    /// <summary>Windows owned by Dictate; never remembered as a target.</summary>
    public Func<IntPtr, bool> IsOwnWindow { get; init; } = _ => false;

    /// <summary>The window remembered when dictation started, or zero.</summary>
    public IntPtr Target => _target;

    public void RememberTarget()
    {
        var foreground = GetForegroundWindow();
        _target = IsOwnWindow(foreground) ? IntPtr.Zero : foreground;
    }

    public async Task InsertAsync(string text, CancellationToken ct)
    {
        if (_target != IntPtr.Zero && GetForegroundWindow() != _target && IsWindow(_target))
        {
            SetForegroundWindow(_target);
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        // A trailing space so the next dictation doesn't run into this one.
        var inputs = BuildInputs(text + " ");
        for (var i = 0; i < inputs.Length; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = inputs.AsSpan(i, Math.Min(ChunkSize, inputs.Length - i)).ToArray();
            if (SendInput((uint)chunk.Length, chunk, Marshal.SizeOf<INPUT>()) == 0)
                throw new InvalidOperationException($"Windows refused the typed text (error {Marshal.GetLastWin32Error()}).");
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private static INPUT[] BuildInputs(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                inputs.Add(Key(VK_RETURN, 0, 0));
                inputs.Add(Key(VK_RETURN, 0, KEYEVENTF_KEYUP));
                continue;
            }
            // Surrogate pairs work as two consecutive unicode events.
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        return inputs.ToArray();
    }

    private static INPUT Key(ushort vk, char scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;

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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
}
