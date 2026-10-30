using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace FlowKey;

/// <summary>
/// Custom entry point, with no XAML on the key's path so nothing ever flashes up:
/// <list type="bullet">
/// <item>A flowkey:// launch (a Copilot key press while nothing was running) opens the chosen app
/// at once, then stays on as the resident instance so the next press is quick.</item>
/// <item>Sign-in (the startup task) starts the resident instance.</item>
/// <item>Any other launch (from Start) opens the settings window, in one instance.</item>
/// </list>
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind is ExtendedActivationKind.Protocol or ExtendedActivationKind.ProtocolForResults
            or ExtendedActivationKind.StartupTask)
        {
            var resident = AppInstance.FindOrRegisterForKey("flowkey-resident");
            if (activation.Kind != ExtendedActivationKind.StartupTask) Launcher.HandleKey(activation);
            if (resident.IsCurrent) Resident.Run();
            return 0;
        }

        var main = AppInstance.FindOrRegisterForKey("flowkey-main");
        if (!main.IsCurrent)
        {
            RedirectAndWait(main, activation);
            return 0;
        }

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    // Redirect on a worker thread while this STA thread waits with COM pumping, as the Windows App SDK docs recommend.
    private static void RedirectAndWait(AppInstance target, AppActivationArguments args)
    {
        var done = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            target.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(done);
        });
        CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, new[] { done }, out _);
        CloseHandle(done);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);
}
