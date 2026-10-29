using System.Web;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace FlowKey;

public partial class App : Application
{
    private Log? _log;
    private WisprBridge? _bridge;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        // Flow Key lives in the background with no visible window; only Quit ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _log = new Log(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        var settings = new FlowSettings();
        _bridge = new WisprBridge(settings, _log);
        _window = new MainWindow(_bridge, settings, _log);
        _log.Write($"Flow Key {Windows.ApplicationModel.Package.Current.Id.Version.ToText()} started");
        var (ptt, handsFree) = WisprConfig.Read();
        _log.Write($"wispr push to talk {ptt?.Name ?? "unreadable"}, hands-free {handsFree?.Name ?? "unreadable"}; " +
                   $"sending {settings.PushToTalk.Name} and {settings.HandsFree.Name}");

        var current = AppInstance.GetCurrent();
        current.Activated += (_, a) => _window.DispatcherQueue.TryEnqueue(() => HandleActivation(a, redirected: true));
        HandleActivation(current.GetActivatedEventArgs(), redirected: false);
    }

    private void HandleActivation(AppActivationArguments args, bool redirected)
    {
        switch (args.Kind)
        {
            // A cold start from the Copilot key arrives as ProtocolForResults, a running instance gets Protocol.
            // Never show a window here: that ends a hold about 50 ms in.
            case ExtendedActivationKind.Protocol or ExtendedActivationKind.ProtocolForResults
                when args.Data is IProtocolActivatedEventArgs p:
                var state = HttpUtility.ParseQueryString(p.Uri.Query)["state"];
                if (CopilotKeyFastPath.FromUriState(state) is { } key)
                    _bridge!.OnKey(key, redirected ? "uri" : "uri (cold)");
                else
                    _log!.Write($"uri   unknown state '{state}'");
                break;

            // Signed in: stay in the background.
            case ExtendedActivationKind.StartupTask:
                _log!.Write("start at sign-in");
                break;

            default:
                _window!.Present();
                break;
        }
    }
}

internal static class VersionText
{
    public static string ToText(this Windows.ApplicationModel.PackageVersion v) => $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
}
