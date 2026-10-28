using System.Runtime.InteropServices;

namespace Dictate.Platform;

/// <summary>
/// Claims Escape system-wide while Dictate is listening, so it cancels the dictation
/// instead of reaching the app being dictated into. Use on the UI thread that owns the window.
/// </summary>
internal sealed class EscapeHotKey : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const int HotKeyId = 0xD1C7;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_ESCAPE = 0x1B;
    private const nuint SubclassId = 2;

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;
    private readonly Action _pressed;
    private bool _registered;

    public EscapeHotKey(IntPtr hwnd, Action pressed)
    {
        _hwnd = hwnd;
        _pressed = pressed;
        _proc = WindowProc;
        SetWindowSubclass(hwnd, _proc, SubclassId, IntPtr.Zero);
    }

    public bool Enabled
    {
        get => _registered;
        set
        {
            if (value == _registered) return;
            if (value)
            {
                // Fails if another app already owns Escape; dictation still works without it.
                _registered = RegisterHotKey(_hwnd, HotKeyId, MOD_NOREPEAT, VK_ESCAPE);
            }
            else
            {
                UnregisterHotKey(_hwnd, HotKeyId);
                _registered = false;
            }
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, nuint wParam, IntPtr lParam, nuint id, IntPtr refData)
    {
        if (msg == WM_HOTKEY && (int)wParam == HotKeyId)
        {
            _pressed();
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Enabled = false;
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, nuint wParam, IntPtr lParam, nuint id, IntPtr refData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id, IntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, nuint wParam, IntPtr lParam);
}
