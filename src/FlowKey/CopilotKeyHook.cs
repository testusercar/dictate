using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Dictate.Core;

namespace FlowKey;

/// <summary>
/// Reads the Copilot key straight from the keyboard and hides it from everything else.
/// <para>
/// The key arrives as Win + Shift + F23. Through the Copilot key provider, Windows keeps all three
/// reported down for the whole hold, so Wispr Flow sees Win + Shift + F23 + its shortcut and doesn't
/// react, and letting go of them by hand makes Windows end the hold. A low-level keyboard hook (the
/// way PowerToys remaps the key) sees F23 first: it swallows F23, lets go of the Win and Shift that
/// already got through, and swallows their physical release later. Holds and taps are then timed
/// here, which also means a hold can start sooner than Windows' 300 ms.
/// </para>
/// </summary>
internal sealed class CopilotKeyHook : IDisposable
{
    public static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(250);

    private readonly Action<KeyEvent> _onKey;
    private readonly Action<string> _log;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _hookThread;
    private readonly Thread _worker;
    private readonly HookProc _proc; // Held so the delegate is not collected.
    private readonly ManualResetEventSlim _started = new();
    private uint _hookThreadId;
    private IntPtr _hook;
    private Timer? _holdTimer;

    // Touched only on the hook thread.
    private bool _copilotDown;
    private bool _winDown, _shiftDown, _owedWinUp, _owedShiftUp;

    // Touched only on the worker thread.
    private int _press;
    private bool _holding;

    public CopilotKeyHook(Action<KeyEvent> onKey, Action<string> log)
    {
        _onKey = onKey;
        _log = log;
        _proc = HookCallback;
        _worker = new Thread(() =>
        {
            foreach (var job in _work.GetConsumingEnumerable())
            {
                try { job(); }
                catch (Exception e) { _log($"error {e.Message}"); }
            }
        }) { IsBackground = true, Name = "Flow Key worker" };
        _worker.Start();

        // Its own thread and message loop, so a busy window never delays keys (Windows drops hooks that answer slowly).
        _hookThread = new Thread(RunHook) { IsBackground = true, Name = "Flow Key hook" };
        _hookThread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    public bool IsActive => _hook != IntPtr.Zero;

    private void RunHook()
    {
        _hookThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        _log(_hook == IntPtr.Zero
            ? $"error keyboard hook not installed ({Marshal.GetLastWin32Error()}); using Windows' Copilot key events"
            : "keyboard hook on");
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
            if (!injected && Handle((ushort)info.vkCode, up)) return 1;
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // True to swallow the key. Runs on the hook thread: keep it quick, hand everything else to the worker.
    private bool Handle(ushort vk, bool up)
    {
        switch (vk)
        {
            case VirtualKeys.F23 when !up:
                if (_copilotDown) return true; // Auto-repeat.
                _copilotDown = true;
                var releaseWin = _winDown;
                var releaseShift = _shiftDown;
                _owedWinUp |= releaseWin;
                _owedShiftUp |= releaseShift;
                _winDown = _shiftDown = false;
                _work.Add(() => Pressed(releaseWin, releaseShift));
                return true;

            case VirtualKeys.F23:
                if (!_copilotDown) return false;
                _copilotDown = false;
                _work.Add(Released);
                return true;

            case VirtualKeys.LWin:
                if (!up) { _winDown = true; return false; }
                _winDown = false;
                if (!_owedWinUp) return false;
                _owedWinUp = false;
                return true;

            case VirtualKeys.LShift:
                if (!up) { _shiftDown = true; return false; }
                _shiftDown = false;
                if (!_owedShiftUp) return false;
                _owedShiftUp = false;
                return true;

            default:
                return false;
        }
    }

    private void Pressed(bool releaseWin, bool releaseShift)
    {
        // Let go of what got through before F23 said this was the Copilot key. An unassigned key
        // between Win's press and release keeps Start from opening.
        if (releaseShift) KeyInjector.SendUp(VirtualKeys.LShift);
        if (releaseWin)
        {
            KeyInjector.SendTap(VirtualKeys.Unassigned);
            KeyInjector.SendUp(VirtualKeys.LWin);
        }

        var press = ++_press;
        _holding = false;
        _holdTimer?.Dispose();
        _holdTimer = new Timer(_ => _work.Add(() =>
        {
            if (press != _press || _holding) return;
            _holding = true;
            _onKey(KeyEvent.HoldStart);
        }), null, HoldThreshold, Timeout.InfiniteTimeSpan);
    }

    private void Released()
    {
        _holdTimer?.Dispose();
        _holdTimer = null;
        _press++;
        _onKey(_holding ? KeyEvent.HoldEnd : KeyEvent.Tap);
        _holding = false;
    }

    public void Dispose()
    {
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _holdTimer?.Dispose();
        _work.CompleteAdding();
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
