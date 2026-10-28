using System.Runtime.InteropServices;
using Dictate.Core;

namespace Dictate;

/// <summary>
/// Registers a window for the Copilot key "fast path": while the app is running, Windows posts a
/// WM_APP-range message with the manifest's MessageWParam instead of launching a dictate-key:// URI.
/// See https://learn.microsoft.com/windows/apps/develop/windows-integration/copilot-key-state
/// </summary>
internal sealed class CopilotKeyFastPath : IDisposable
{
    public const uint WM_COPILOT = 0x8000 + 0x0001;

    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid CopilotFastPathFmtId = new("38652BCA-4329-4E74-86F9-39CF29345EEA");
    private const ushort VT_UINT = 23;

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc; // Held so the delegate is not collected.
    private readonly Action<KeyEvent> _onKey;

    public CopilotKeyFastPath(IntPtr hwnd, Action<KeyEvent> onKey)
    {
        _hwnd = hwnd;
        _onKey = onKey;
        _proc = WindowProc;

        var iid = IID_IPropertyStore;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        try
        {
            var key = new PropertyKey { fmtid = CopilotFastPathFmtId, pid = 2 };
            var value = new PropVariant { vt = VT_UINT, uintVal = WM_COPILOT };
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }

        if (!SetWindowSubclass(hwnd, _proc, 1, IntPtr.Zero))
            throw new InvalidOperationException("SetWindowSubclass failed.");
    }

    public static KeyEvent? FromWParam(nuint wParam) => wParam switch
    {
        0 => KeyEvent.Tap,
        1 => KeyEvent.HoldStart,
        2 => KeyEvent.HoldEnd,
        _ => null,
    };

    public static KeyEvent? FromUriState(string? state) => state switch
    {
        "Tap" => KeyEvent.Tap,
        "Down" => KeyEvent.HoldStart,
        "Up" => KeyEvent.HoldEnd,
        _ => null,
    };

    private IntPtr WindowProc(IntPtr hWnd, uint msg, nuint wParam, IntPtr lParam, nuint id, IntPtr refData)
    {
        if (msg == WM_COPILOT && FromWParam(wParam) is { } e)
        {
            _onKey(e);
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose() => RemoveWindowSubclass(_hwnd, _proc, 1);

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

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, nuint wParam, IntPtr lParam, nuint id, IntPtr refData);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore store);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id, IntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, nuint wParam, IntPtr lParam);
}
