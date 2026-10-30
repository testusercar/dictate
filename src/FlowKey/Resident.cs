using System.Runtime.InteropServices;

namespace FlowKey;

/// <summary>
/// The background instance that makes a press quick: already running (from sign-in, or from the
/// first press), no XAML and no visible window. It reads the key with a keyboard hook on key-down,
/// and also takes Windows' own Copilot key events through a hidden window (the "fast path"), so a
/// press never pays for starting a process.
/// </summary>
internal static class Resident
{
    // Windows posts this for the fast path, with the manifest's MessageWParam (0 tap, 1 hold start, 2 hold end).
    private const uint WM_COPILOT = 0x8000 + 0x0001;
    // The keyboard hook posts this: wParam bit 0 = let go of Win, bit 1 = let go of Shift; lParam = key-down time.
    private const uint WM_HOOK_PRESS = 0x8000 + 0x0002;
    private const string WindowClass = "CopilotKeyLauncherResident";

    // A press seen by the hook may also reach Windows' events; only the first one opens the app.
    private const uint DuplicateWindowMs = 800;

    private static readonly Guid CopilotFastPathFmtId = new("38652BCA-4329-4E74-86F9-39CF29345EEA");
    private const ushort VT_UINT = 23;

    private static WndProc? _proc; // Held so the delegate is not collected.
    private static IntPtr _hwnd;
    private static uint _lastOpen;
    private static bool _opened;

    public static void Run()
    {
        _proc = WindowProc;
        var instance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            lpszClassName = WindowClass,
        };
        if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");
        // Never shown: it only carries the fast path registration and receives the key messages.
        _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, WindowClass, "Copilot Key Launcher", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");

        var fastPath = RegisterFastPath(_hwnd);
        using var hook = new CopilotKeyHook(
            (releaseWin, releaseShift, time) => PostMessage(_hwnd, WM_HOOK_PRESS,
                (IntPtr)((releaseWin ? 1 : 0) | (releaseShift ? 2 : 0)), (IntPtr)(long)time),
            Log.Write);

        try
        {
            Launcher.Prepare();
        }
        catch (Exception e)
        {
            Log.Write($"error {e.Message}");
        }
        Log.Write($"resident: ready ({(hook.IsActive ? "keyboard hook" : "no keyboard hook")}, {(fastPath ? "fast path" : "no fast path")})");

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOOK_PRESS:
                var flags = wParam.ToInt64();
                // Let go of what got through before F23 said this was the Copilot key. The unassigned key
                // between Win's press and release keeps Start from opening, and counts as our input, which
                // is what lets the launcher put another app's window in front.
                if ((flags & 2) != 0) Keys.SendUp(Keys.LShift);
                if ((flags & 1) != 0)
                {
                    Keys.SendTap(Keys.Unassigned);
                    Keys.SendUp(Keys.LWin);
                }
                OpenTarget("key down", (uint)lParam.ToInt64());
                return IntPtr.Zero;

            case WM_COPILOT:
                if (wParam.ToInt64() is 0 or 1) OpenTarget(wParam.ToInt64() == 0 ? "Windows tap" : "Windows hold", GetTickCount());
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void OpenTarget(string source, uint pressedAt)
    {
        var now = GetTickCount();
        if (_opened && now - _lastOpen < DuplicateWindowMs)
        {
            Log.Write($"key   {source,-11} same press, already handled");
            return;
        }
        _opened = true;
        _lastOpen = now;

        try
        {
            if (Launcher.Target is not { } target)
            {
                Log.Write($"key   {source,-11} no app chosen; open Copilot Key Launcher from Start to pick one");
                return;
            }
            var how = Launcher.Open(target);
            Log.Write($"key   {source,-11} {how} {target.Name} in {GetTickCount() - pressedAt} ms");
        }
        catch (Exception e)
        {
            Log.Write($"error {e.Message}");
        }
    }

    private static bool RegisterFastPath(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return false;
        try
        {
            var key = new PropertyKey { fmtid = CopilotFastPathFmtId, pid = 2 };
            var value = new PropVariant { vt = VT_UINT, uintVal = WM_COPILOT };
            return store.SetValue(ref key, ref value) == 0 && store.Commit() == 0;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private const uint WS_EX_TOOLWINDOW = 0x80;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    // PROPVARIANT is 16 bytes on x86 and 24 on 64-bit; the value union starts at offset 8.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint uintVal;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore store);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();
}
