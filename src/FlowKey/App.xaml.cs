using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace FlowKey;

/// <summary>The settings window. Copilot key presses never get here; see <see cref="Program"/>.</summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        // Opening Flow Key again from Start brings this window back.
        AppInstance.GetCurrent().Activated += (_, _) => _window.DispatcherQueue.TryEnqueue(_window.Present);
        _window.Present();
    }
}
