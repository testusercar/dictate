using System.Collections.ObjectModel;
using System.Diagnostics;
using Dictate.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Dictate.KeySpike;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _log = new();
    private readonly KeyStateMachine _machine = new();
    private readonly CopilotKeyFastPath _fastPath;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _lastEvent;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 820));

        _machine.StateChanged += (_, to) => DispatcherQueue.TryEnqueue(() => StateText.Text = to.ToString());
        _machine.ActionRequested += a => DispatcherQueue.TryEnqueue(() => Log("action", a.ToString(), null));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _fastPath = new CopilotKeyFastPath(hwnd, e => OnKey("fast path", e, null));
        Closed += (_, _) => { _fastPath.Dispose(); _machine.Dispose(); };

        ShowRegistration();
    }

    /// <summary>Called on the UI thread for every key event, whichever way it arrived.</summary>
    public void OnKey(string source, KeyEvent? e, string? raw)
    {
        var now = _clock.Elapsed;
        var gap = _lastEvent is { } last ? $"+{(now - last).TotalMilliseconds:0} ms" : "first";
        _lastEvent = now;

        if (e is null)
        {
            Log(source, $"unknown state '{raw}'", gap);
            return;
        }
        Log(source, e.Value.ToString(), gap);
        _machine.Handle(e.Value);
    }

    public void Log(string source, string what, string? gap)
    {
        _log.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {source,-16} {what,-18} {gap}");
        Debug.WriteLine(_log[0]);
    }

    private void ShowRegistration()
    {
        string aumid;
        try
        {
            aumid = $"{Windows.ApplicationModel.Package.Current.Id.FamilyName}!App";
        }
        catch (InvalidOperationException)
        {
            RegistrationBar.Severity = InfoBarSeverity.Error;
            RegistrationBar.Title = "Not packaged";
            RegistrationBar.Message = "Run the installed MSIX; the Copilot key only works for packaged, signed apps.";
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\BrandedKey");
        var choice = key?.GetValue("BrandedKeyChoiceType") as string;
        var selected = key?.GetValue("AppAumid") as string;

        if (choice == "App" && string.Equals(selected, aumid, StringComparison.OrdinalIgnoreCase))
        {
            RegistrationBar.Severity = InfoBarSeverity.Success;
            RegistrationBar.Title = "Dictate owns the Copilot key";
            RegistrationBar.Message = "Press, hold and double tap it; every event is logged below.";
        }
        else
        {
            RegistrationBar.Severity = InfoBarSeverity.Warning;
            RegistrationBar.Title = "Copilot key points elsewhere";
            RegistrationBar.Message = $"Choose Dictate under Settings > Personalization > Text input > Customize Copilot key. Current: {choice ?? "default"} {selected}";
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Log("keyboard", "Cancel", null);
            _machine.Handle(KeyEvent.Cancel);
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, _log.Reverse()));
        Clipboard.SetContent(package);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        _lastEvent = null;
        ShowRegistration();
    }

    private async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("ms-settings:personalization-textinput"));
        ShowRegistration();
    }
}
