using System.Runtime.InteropServices;
using System.Text;

namespace FlowKey;

/// <summary>
/// Brings an app that is already open to the front directly, which is much quicker than launching
/// it again and waiting for it to hand over to its running instance (Electron apps take ~0.5 s).
/// </summary>
internal static class WindowFocus
{
    /// <summary>The app's main window, or zero when it has none open (closed, or only in the tray).</summary>
    public static IntPtr Find(LaunchTarget target)
    {
        var packaged = target.ExePath is null && target.AppId.Contains('!');
        if (target.ExePath is null && !packaged) return IntPtr.Zero;

        var matches = new Dictionary<uint, bool>();
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidate(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!matches.TryGetValue(pid, out var match))
                matches[pid] = match = packaged
                    ? string.Equals(AppIdOf(pid), target.AppId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(ExeOf(pid), target.ExePath, StringComparison.OrdinalIgnoreCase);
            if (!match) return true;
            found = hwnd; // Top of the Z-order: the window used most recently.
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Restores the window and makes it the foreground window. True if it ended up in front.</summary>
    public static bool Bring(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        // Windows only lets the app that had the last input take the foreground; a harmless key makes that us.
        Keys.SendTap(Keys.Unassigned);
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        var foreground = GetForegroundWindow();
        var theirs = GetWindowThreadProcessId(foreground, out _);
        var ours = GetCurrentThreadId();
        var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(ours, theirs, false);
        return GetForegroundWindow() == hwnd;
    }

    // A visible, unowned app window on this desktop: what Alt + Tab would show.
    private static bool IsCandidate(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return false;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private static string? ExeOf(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string? AppIdOf(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(256);
            var size = buffer.Capacity;
            return GetApplicationUserModelId(process, ref size, buffer) == 0 ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const int SW_RESTORE = 9;
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref int length, StringBuilder appId);
}
