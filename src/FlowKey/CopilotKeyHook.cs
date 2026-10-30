using System.Runtime.InteropServices;

namespace FlowKey;

/// <summary>
/// Sees the Copilot key the moment it goes down, instead of waiting for Windows to decide on
/// release whether it was a tap or a hold.
/// <para>
/// The key arrives as Win + Shift + F23. A low-level keyboard hook (the way PowerToys remaps the
/// key) sees F23 first: it swallows F23, its repeats and its release, and reports the press with
/// whether Win and Shift already got through, so they can be let go of. Their physical releases
/// are swallowed later. The callback runs on the hook thread and must only hand the press off.
/// </para>
/// </summary>
internal sealed class CopilotKeyHook : IDisposable
{
    // (release Win, release Shift, key-down time in GetTickCount milliseconds)
    private readonly Action<bool, bool, uint> _onPress;
    private readonly Action<string> _log;
    private readonly Thread _hookThread;
    private readonly HookProc _proc; // Held so the delegate is not collected.
    private readonly ManualResetEventSlim _started = new();
    private uint _hookThreadId;
    private IntPtr _hook;

    // Touched only on the hook thread.
    private bool _copilotDown;
    private bool _winDown, _shiftDown, _owedWinUp, _owedShiftUp;

    public CopilotKeyHook(Action<bool, bool, uint> onPress, Action<string> log)
    {
        _onPress = onPress;
        _log = log;
        _proc = HookCallback;
        // Its own thread and message loop, so nothing else can delay keys (Windows drops hooks that answer slowly).
        _hookThread = new Thread(RunHook) { IsBackground = true, Name = "Copilot key hook" };
        _hookThread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    public bool IsActive => _hook != IntPtr.Zero;

    private void RunHook()
    {
        _hookThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) _log($"error keyboard hook not installed ({Marshal.GetLastWin32Error()}); using Windows' Copilot key events");
        _started.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var injected = (info.flags & LLKHF_INJECTED) != 0;
            var up = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;
            if (!injected && Handle((ushort)info.vkCode, up, info.time)) return 1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // True to swallow the key.
    private bool Handle(ushort vk, bool up, uint time)
    {
        switch (vk)
        {
            case Keys.F23 when !up:
                if (_copilotDown) return true; // Auto-repeat.
                _copilotDown = true;
                var releaseWin = _winDown;
                var releaseShift = _shiftDown;
                _owedWinUp |= releaseWin;
                _owedShiftUp |= releaseShift;
                _winDown = _shiftDown = false;
                _onPress(releaseWin, releaseShift, time);
                return true;

            case Keys.F23:
                if (!_copilotDown) return false;
                _copilotDown = false;
                return true;

            case Keys.LWin:
                if (!up) { _winDown = true; return false; }
                _winDown = false;
                if (!_owedWinUp) return false;
                _owedWinUp = false;
                return true;

            case Keys.LShift:
                if (!up) { _shiftDown = true; return false; }
                _shiftDown = false;
                if (!_owedShiftUp) return false;
                _owedShiftUp = false;
                return true;

            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
}
