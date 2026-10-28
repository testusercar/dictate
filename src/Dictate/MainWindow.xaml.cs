using Dictate.Core;
using Dictate.Core.Dictation;
using Dictate.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Dictate;

/// <summary>Settings and status. Closing it only hides it: the app keeps listening for the Copilot key.</summary>
public sealed partial class MainWindow : Window
{
    private static readonly (string Name, string Code)[] Languages =
    {
        ("Auto-detect", ""), ("English", "en"), ("Spanish", "es"), ("French", "fr"), ("German", "de"),
        ("Italian", "it"), ("Portuguese", "pt"), ("Dutch", "nl"), ("Japanese", "ja"), ("Korean", "ko"),
        ("Chinese", "zh"), ("Hindi", "hi"),
    };

    private const string StartupTaskId = "DictateStartup";

    private readonly DictationHost _host;
    private readonly CopilotKeyFastPath _fastPath;
    private readonly EscapeHotKey _escape;
    private bool _loading = true;

    internal MainWindow(DictationHost host)
    {
        _host = host;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 900));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _host.MainWindowHandle = hwnd;
        _fastPath = new CopilotKeyFastPath(hwnd, e => _host.OnKey(e, "fast path"));
        _escape = new EscapeHotKey(hwnd, () => _host.OnKey(KeyEvent.Cancel, "escape"));

        // Hide instead of closing: the window carries the Copilot key fast path.
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) ShowKeyRegistration();
        };

        _host.StatusChanged += OnStatus;
        _host.Inserted += text => LastText.Text = text;
        _host.Failed += message =>
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        };

        LogList.ItemsSource = _host.Log;
        foreach (var (name, code) in Languages)
            LanguageBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.Code == _host.Settings.Language));
        WarmMicSwitch.IsOn = _host.KeepMicWarm;
        ShowApiKeyState();
        ShowKeyRegistration();
        OnStatus(_host.Status);
        _ = LoadStartupStateAsync();
        _loading = false;
    }

    /// <summary>Shows the window and brings it to the front.</summary>
    public void Present()
    {
        AppWindow.Show();
        Activate();
        if (string.IsNullOrWhiteSpace(_host.Keys.Get())) ApiKeyBox.Focus(FocusState.Programmatic);
    }

    private void OnStatus(DictationStatus status)
    {
        // Escape belongs to Dictate only while it is listening.
        _escape.Enabled = status == DictationStatus.Listening;
        StatusText.Text = status switch
        {
            DictationStatus.Listening => "Listening…",
            DictationStatus.Transcribing => "Transcribing…",
            _ when string.IsNullOrWhiteSpace(_host.Keys.Get()) => "Add your xAI API key to start",
            _ => "Ready",
        };
    }

    private void ShowApiKeyState()
    {
        var saved = !string.IsNullOrWhiteSpace(_host.Keys.Get());
        ApiKeyState.Text = saved ? "Saved" : "Not set";
        RemoveKeyButton.IsEnabled = saved;
        TestKeyButton.IsEnabled = saved;
        ApiKeyBox.PlaceholderText = saved ? "Saved. Paste a new key to replace it." : "xai-…";
        OnStatus(_host.Status);
    }

    private void ShowKeyRegistration()
    {
        string aumid;
        try
        {
            aumid = $"{Package.Current.Id.FamilyName}!App";
        }
        catch (InvalidOperationException)
        {
            KeyBar.Severity = InfoBarSeverity.Error;
            KeyBar.Title = "Not installed as a package";
            KeyBar.Message = "Run the installed MSIX; Windows only gives the Copilot key to packaged, signed apps.";
            KeyBar.IsOpen = true;
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\BrandedKey");
        var choice = key?.GetValue("BrandedKeyChoiceType") as string;
        var selected = key?.GetValue("AppAumid") as string;
        var ours = choice == "App" && string.Equals(selected, aumid, StringComparison.OrdinalIgnoreCase);

        KeyBar.IsOpen = !ours;
        KeyBar.Severity = InfoBarSeverity.Warning;
        KeyBar.Title = "The Copilot key isn't set to Dictate";
        KeyBar.Message = "Open Customize Copilot key below and choose Dictate.";
        CopilotKeyCard.Description = ours
            ? "Dictate owns the Copilot key."
            : "Choose Dictate under Personalization > Text input.";
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e) => SaveKey();

    private void ApiKeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        SaveKey();
    }

    private async void SaveKey()
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length == 0) return;
        _host.Keys.Set(key);
        ApiKeyBox.Password = "";
        ShowApiKeyState();
        await TestAsync();
    }

    private async void TestKey_Click(object sender, RoutedEventArgs e) => await TestAsync();

    private async Task TestAsync()
    {
        if (_host.Keys.Get() is not { Length: > 0 } key) return;
        TestBar.IsOpen = false;
        TestRing.IsActive = true;
        TestKeyButton.IsEnabled = false;
        var error = await _host.TestKeyAsync(key);
        TestRing.IsActive = false;
        TestKeyButton.IsEnabled = true;

        TestBar.Severity = error is null ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        TestBar.Title = error is null ? "Grok is connected" : "Grok didn't accept the connection";
        TestBar.Message = error ?? "Hold the Copilot key and start talking.";
        TestBar.IsOpen = true;
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        _host.Keys.Clear();
        TestBar.IsOpen = false;
        ShowApiKeyState();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        _host.Settings.Language = code;
    }

    private void WarmMicSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.KeepMicWarm = WarmMicSwitch.IsOn;
    }

    private async Task LoadStartupStateAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            _loading = true;
            StartupSwitch.IsOn = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            StartupSwitch.IsEnabled = task.State is not (StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy);
        }
        catch (Exception)
        {
            StartupSwitch.IsEnabled = false;
        }
        finally
        {
            _loading = false;
        }
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var task = await StartupTask.GetAsync(StartupTaskId);
        if (StartupSwitch.IsOn)
        {
            var state = await task.RequestEnableAsync();
            if (state == StartupTaskState.DisabledByUser)
            {
                ErrorBar.Message = "Windows has Dictate turned off as a startup app. Turn it on in Settings > Apps > Startup.";
                ErrorBar.IsOpen = true;
            }
        }
        else
        {
            task.Disable();
        }
        await LoadStartupStateAsync();
    }

    private async void CopilotKeySettings_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:personalization-textinput"));

    private async void MicPrivacy_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));

    private void CopyLast_Click(object sender, RoutedEventArgs e)
    {
        if (_host.LastText is { } text) CopyText(text);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) =>
        CopyText(string.Join(Environment.NewLine, _host.Log.Reverse()));

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        _escape.Dispose();
        _fastPath.Dispose();
        _host.Dispose();
        Application.Current.Exit();
    }
}
