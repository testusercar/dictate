using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Dictate;

/// <summary>
/// The small always-on-top capsule shown while dictating. It never takes focus, so the
/// text still goes to the app the user was in.
/// <para>
/// The window is shown once at startup and then only moved on and off screen. Showing a window
/// while the Copilot key is held makes Windows end the hold about 50 ms in and replay the key as
/// taps, so push-to-talk falls apart.
/// </para>
/// </summary>
public sealed partial class RecordingPill : Window
{
    private const int WidthDip = 460;
    private const int HeightDip = 52;
    private const int BottomMarginDip = 32;
    private const int MaxTranscriptChars = 80;

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _hideTimer;
    private float _level;
    private bool _onScreen;

    public RecordingPill()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new AlwaysActiveAcrylic();

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        var corners = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => Hide();

        // Closing would destroy the window; it is only ever parked.
        AppWindow.Closing += (_, e) => e.Cancel = true;

        Park();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Shows the pill at the bottom of the screen that holds <paramref name="nearWindow"/>.</summary>
    public void ShowListening(IntPtr nearWindow, bool handsFree)
    {
        _hideTimer.Stop();
        SetMode(handsFree);
        TranscriptText.Text = "";
        Busy.IsActive = false;
        MicGlyph.Visibility = Visibility.Visible;
        ErrorGlyph.Visibility = Visibility.Collapsed;
        LevelHalo.Visibility = Visibility.Visible;
        Place(nearWindow);
    }

    public void SetMode(bool handsFree) =>
        ModeText.Text = handsFree ? "Hands-free" : "Listening";

    public void SetTranscript(string text)
    {
        text = text.Trim();
        TranscriptText.Text = text.Length > MaxTranscriptChars ? "…" + text[^(MaxTranscriptChars - 1)..] : text;
    }

    public void SetLevel(float peak)
    {
        // Fast attack, slow release, with quiet speech still visible.
        _level = Math.Max(Math.Min(1f, peak * 3f), _level * 0.85f);
        LevelScale.ScaleX = LevelScale.ScaleY = 0.6 + _level * 0.55;
    }

    public void ShowTranscribing()
    {
        ModeText.Text = "Transcribing";
        MicGlyph.Visibility = Visibility.Collapsed;
        LevelHalo.Visibility = Visibility.Collapsed;
        Busy.IsActive = true;
    }

    public void ShowError(string message, IntPtr nearWindow)
    {
        Busy.IsActive = false;
        MicGlyph.Visibility = Visibility.Collapsed;
        ErrorGlyph.Visibility = Visibility.Visible;
        LevelHalo.Visibility = Visibility.Collapsed;
        ModeText.Text = "Dictate";
        TranscriptText.Text = message;
        if (!_onScreen) Place(nearWindow);
        _hideTimer.Interval = TimeSpan.FromSeconds(5);
        _hideTimer.Start();
    }

    public void Hide()
    {
        _hideTimer.Stop();
        Busy.IsActive = false;
        _level = 0;
        Park();
    }

    /// <summary>Hides the pill unless an error is still on screen.</summary>
    public void HideUnlessShowingError()
    {
        if (!_hideTimer.IsRunning) Hide();
    }

    private void Place(IntPtr nearWindow)
    {
        var anchor = nearWindow != IntPtr.Zero ? nearWindow : _hwnd;
        var monitor = MonitorFromWindow(anchor, MONITOR_DEFAULTTOPRIMARY);
        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(anchor);
        var work = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
        var width = (int)(WidthDip * scale);
        var height = (int)(HeightDip * scale);
        SetWindowPos(_hwnd, HWND_TOPMOST,
            work.X + (work.Width - width) / 2,
            work.Y + work.Height - height - (int)(BottomMarginDip * scale),
            width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _onScreen = true;
    }

    // Far off every monitor, still "visible" to Windows so bringing it back is only a move.
    private void Park()
    {
        SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE | SWP_NOZORDER);
        _onScreen = false;
    }

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
