using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Web;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace FlowKey;

/// <summary>An app from the Start menu, by its AppUserModelID (what shell:AppsFolder opens).</summary>
internal sealed record LaunchTarget(string Name, string AppId);

/// <summary>
/// Opens the chosen app when the Copilot key is pressed. Windows launches flowkey://?state=... for
/// each key event; a tap or the start of a hold opens the app, the end of a hold does nothing.
/// </summary>
internal static class Launcher
{
    // Picked on first use when nothing has been chosen yet.
    private const string DefaultAppName = "Grok";

    private static IPropertySet Values => ApplicationData.Current.LocalSettings.Values;

    /// <summary>The app the Copilot key opens, or null when none has been chosen.</summary>
    public static LaunchTarget? Target
    {
        get => Values.TryGetValue("TargetAppId", out var id) && id is string appId && appId.Length > 0
            ? new LaunchTarget(Values.TryGetValue("TargetName", out var n) && n is string name ? name : appId, appId)
            : null;
        set
        {
            if (value is null)
            {
                Values.Remove("TargetAppId");
                Values.Remove("TargetName");
                return;
            }
            Values["TargetAppId"] = value.AppId;
            Values["TargetName"] = value.Name;
        }
    }

    /// <summary>The chosen app, or the default one (saved as the choice) if it is installed.</summary>
    public static LaunchTarget? TargetOrDefault(IReadOnlyList<LaunchTarget>? startApps = null)
    {
        if (Target is { } target) return target;
        var found = (startApps ?? StartApps.List()).FirstOrDefault(a => a.Name.Contains(DefaultAppName, StringComparison.OrdinalIgnoreCase));
        if (found is not null)
        {
            Target = found;
            Log.Write($"no app chosen yet; using {found.Name} ({found.AppId})");
        }
        return found;
    }

    /// <summary>A Copilot key event. Runs in a short-lived process that has no window and exits right after.</summary>
    public static void HandleKey(AppActivationArguments args)
    {
        var state = args.Data is IProtocolActivatedEventArgs p ? HttpUtility.ParseQueryString(p.Uri.Query)["state"] : null;
        try
        {
            if (state is "Tap" or "Down")
            {
                if (TargetOrDefault() is { } target)
                {
                    Open(target);
                    Log.Write($"key   {state,-4} opened {target.Name}, {SinceStart():0} ms after Flow Key started");
                }
                else
                {
                    Log.Write($"key   {state,-4} no app chosen; open Flow Key from Start to pick one");
                }
            }
            else
            {
                Log.Write($"key   {state ?? "?",-4} ignored");
            }
        }
        catch (Exception e)
        {
            Log.Write($"error {e.Message}");
        }
        finally
        {
            // A cold start from the Copilot key is a launch for results; answer it so Windows isn't left waiting.
            if (args.Data is IProtocolForResultsActivatedEventArgs r)
                r.ProtocolForResultsOperation.ReportCompleted(new ValueSet());
        }
    }

    /// <summary>Opens (or brings back) the app the way Start does.</summary>
    public static void Open(LaunchTarget target)
    {
        // Lets the app, or the running instance it hands over to, come to the front.
        AllowSetForegroundWindow(ASFW_ANY);
        try
        {
            Process.Start(new ProcessStartInfo($@"shell:AppsFolder\{target.AppId}") { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"Couldn't open {target.Name} ({target.AppId}): {e.Message}", e);
        }
    }

    private static double SinceStart() => (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
