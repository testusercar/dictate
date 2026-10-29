using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FlowKey;

/// <summary>Settings and status. Closing it only hides it: the app keeps listening for the Copilot key.</summary>
public sealed partial class MainWindow : Window
{
    private const string StartupTaskId = "FlowKeyStartup";

    private readonly WisprBridge _bridge;
    private readonly FlowSettings _settings;
    private readonly Log _log;
    private readonly CopilotKeyFastPath _fastPath;
    private bool _loading = true;

    internal MainWindow(WisprBridge bridge, FlowSettings settings, Log log)
    {
        _bridge = bridge;
        _settings = settings;
        _log = log;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(860, 760));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _fastPath = new CopilotKeyFastPath(hwnd, e => _bridge.OnKey(e, "fast path"));

        // Hide instead of closing: the window carries the Copilot key fast path.
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            ShowKeyRegistration();
            ShowWisprShortcuts();
        };

        _bridge.Sent += line => DispatcherQueue.TryEnqueue(() => StatusText.Text = $"Last sent: {line[6..]}");

        LogList.ItemsSource = _log.Lines;
        Fill(PushToTalkBox, Shortcut.PushToTalkChoices, _settings.PushToTalkChoice);
        Fill(HandsFreeBox, Shortcut.HandsFreeChoices, _settings.HandsFreeChoice);
        ShowWisprShortcuts();
        ShowKeyRegistration();
        _ = LoadStartupStateAsync();
        _loading = false;
    }

    /// <summary>Shows the window and brings it to the front.</summary>
    public void Present()
    {
        AppWindow.Show();
        Activate();
    }

    private static void Fill(ComboBox box, Shortcut[] choices, string selected)
    {
        box.Items.Add(new ComboBoxItem { Content = "Same as Wispr Flow", Tag = Shortcut.MatchWispr });
        foreach (var s in choices)
            box.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s.Id });
        box.SelectedIndex = Math.Max(0, Array.FindIndex(choices, c => c.Id == selected) + 1);
    }

    // What "Same as Wispr Flow" resolves to right now.
    private void ShowWisprShortcuts()
    {
        var (ptt, handsFree) = WisprConfig.Read();
        PushToTalkCard.Description = ptt is null
            ? "Held down while you hold the Copilot key. Wispr Flow's setting couldn't be read, so pick the same shortcut as its Push to talk."
            : $"Held down while you hold the Copilot key. Wispr Flow's Push to talk is {ptt.Name}.";
        HandsFreeCard.Description = handsFree is null
            ? "Pressed when you double tap, and again when you tap to finish. Wispr Flow's setting couldn't be read, so pick the same shortcut as its Hands-free."
            : $"Pressed when you double tap, and again when you tap to finish. Wispr Flow's Hands-free is {handsFree.Name}.";
    }

    private void PushToTalkBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PushToTalkBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        _settings.PushToTalkChoice = id;
        _log.Write($"push to talk is now {_settings.PushToTalk.Name}");
    }

    private void HandsFreeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HandsFreeBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        _settings.HandsFreeChoice = id;
        _log.Write($"hands-free is now {_settings.HandsFree.Name}");
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

        // Read only: the choice is the user's to make in Settings.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\BrandedKey");
        var choice = key?.GetValue("BrandedKeyChoiceType") as string;
        var selected = key?.GetValue("AppAumid") as string;
        var ours = choice == "App" && string.Equals(selected, aumid, StringComparison.OrdinalIgnoreCase);

        KeyBar.IsOpen = !ours;
        KeyBar.Severity = InfoBarSeverity.Warning;
        KeyBar.Title = "The Copilot key isn't set to Flow Key";
        KeyBar.Message = "Open Customize Copilot key below and choose Wispr Flow (via Flow Key).";
        CopilotKeyCard.Description = ours
            ? "Flow Key owns the Copilot key."
            : "Choose Wispr Flow (via Flow Key) under Personalization > Text input.";
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
            await task.RequestEnableAsync();
        else
            task.Disable();
        await LoadStartupStateAsync();
    }

    private async void CopilotKeySettings_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:personalization-textinput"));

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, _log.Lines.Reverse()));
        Clipboard.SetContent(package);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        _fastPath.Dispose();
        _bridge.Dispose();
        Application.Current.Exit();
    }
}
