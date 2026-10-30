using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;

namespace FlowKey;

/// <summary>Picks the app the Copilot key opens. Closing it quits; the key is handled by the resident instance.</summary>
public sealed partial class MainWindow : Window
{
    private const string StartupTaskId = "FlowKeyStartup";

    private bool _loading = true;
    private bool _startupLoading;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(860, 700));

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            ShowKeyRegistration();
            ShowLog();
        };

        ShowTarget(Launcher.Target);
        ShowKeyRegistration();
        ShowLog();
        FocusSwitch.IsOn = Launcher.FocusTextBox;
        _ = LoadStartupStateAsync();
        _ = EnsureResidentAsync();
        // Listing the Start menu takes a moment; let the window paint first.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, LoadApps);
    }

    // The quick path needs the background instance; start it if it isn't running (e.g. right after installing).
    private static async Task EnsureResidentAsync()
    {
        if (Microsoft.Windows.AppLifecycle.AppInstance.GetInstances().Any(i => i.Key == "flowkey-resident")) return;
        await Windows.System.Launcher.LaunchUriAsync(new Uri("flowkey://?state=Resident"));
    }

    private async Task LoadStartupStateAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            _startupLoading = true;
            StartupSwitch.IsOn = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            StartupSwitch.IsEnabled = task.State is not (StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy);
        }
        catch (Exception)
        {
            StartupSwitch.IsEnabled = false;
        }
        finally
        {
            _startupLoading = false;
        }
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_startupLoading) return;
        var task = await StartupTask.GetAsync(StartupTaskId);
        if (StartupSwitch.IsOn)
            await task.RequestEnableAsync();
        else
            task.Disable();
        await LoadStartupStateAsync();
    }

    /// <summary>Shows the window and brings it to the front.</summary>
    public void Present()
    {
        AppWindow.Show();
        Activate();
    }

    private void LoadApps()
    {
        IReadOnlyList<LaunchTarget> apps;
        try
        {
            apps = StartApps.List();
        }
        catch (Exception e)
        {
            AppBox.PlaceholderText = "Couldn't list apps";
            Log.Write($"error listing Start apps: {e.Message}");
            return;
        }

        var target = Launcher.TargetOrDefault(apps);
        AppBox.Items.Clear();
        foreach (var app in apps)
            AppBox.Items.Add(new ComboBoxItem { Content = app.Name, Tag = app });

        // Keep a chosen app that has since left the Start menu visible, so the choice isn't silently lost.
        if (target is not null && !apps.Any(a => string.Equals(a.AppId, target.AppId, StringComparison.OrdinalIgnoreCase)))
            AppBox.Items.Insert(0, new ComboBoxItem { Content = $"{target.Name} (not in Start)", Tag = target });

        AppBox.SelectedItem = AppBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag is LaunchTarget t && string.Equals(t.AppId, target?.AppId, StringComparison.OrdinalIgnoreCase));
        AppBox.PlaceholderText = "Choose an app";
        AppBox.IsEnabled = true;
        ShowTarget(target);
        _loading = false;
    }

    private void AppBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppBox.SelectedItem is not ComboBoxItem { Tag: LaunchTarget choice }) return;
        var target = Launcher.WithExePath(choice);
        Launcher.Target = target;
        Log.Write($"the Copilot key now opens {target.Name} ({target.AppId})");
        ShowTarget(target);
        ShowLog();
    }

    private void FocusSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (Launcher.FocusTextBox == FocusSwitch.IsOn) return;
        Launcher.FocusTextBox = FocusSwitch.IsOn;
        Log.Write($"put the cursor in the text box: {(FocusSwitch.IsOn ? "on" : "off")}");
    }

    private void ShowTarget(LaunchTarget? target)
    {
        StatusText.Text = target is null ? "No app chosen" : $"Opens {target.Name}";
        OpenButton.IsEnabled = target is not null;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Launcher.Target is not { } target) return;
        try
        {
            Launcher.Open(target);
            Log.Write($"opened {target.Name} from the window");
        }
        catch (Exception ex)
        {
            Log.Write($"error {ex.Message}");
        }
        ShowLog();
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
        KeyBar.Title = "The Copilot key isn't set to the launcher";
        KeyBar.Message = "Open Customize Copilot key below and choose Copilot Key Launcher.";
        CopilotKeyCard.Description = ours
            ? "The launcher owns the Copilot key."
            : "Choose Copilot Key Launcher under Personalization > Text input.";
    }

    private void ShowLog() => LogList.ItemsSource = Log.Tail(300);

    private async void CopilotKeySettings_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:personalization-textinput"));

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, Log.Tail(300).Reverse()));
        Clipboard.SetContent(package);
    }
}
