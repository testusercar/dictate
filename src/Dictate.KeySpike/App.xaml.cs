using System.Web;
using Dictate.Core;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Dictate.KeySpike;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        var current = AppInstance.GetCurrent();
        current.Activated += (_, a) => _window.DispatcherQueue.TryEnqueue(() => HandleActivation(a, redirected: true));
        HandleActivation(current.GetActivatedEventArgs(), redirected: false);
    }

    private void HandleActivation(AppActivationArguments args, bool redirected)
    {
        if (args.Kind != ExtendedActivationKind.Protocol || args.Data is not IProtocolActivatedEventArgs p)
        {
            _window!.Log("launch", $"{args.Kind}", null);
            return;
        }

        var state = HttpUtility.ParseQueryString(p.Uri.Query)["state"];
        var source = redirected ? "uri (running)" : "uri (cold start)";
        _window!.OnKey(source, CopilotKeyFastPath.FromUriState(state), state);
    }
}
