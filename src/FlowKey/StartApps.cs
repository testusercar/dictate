using System.Runtime.InteropServices;

namespace FlowKey;

/// <summary>
/// The apps in the Start menu's All apps list, packaged or not, as Get-StartApps lists them:
/// the shell's Applications folder, whose items' paths are AppUserModelIDs.
/// </summary>
internal static class StartApps
{
    private const string AppsFolder = "shell:::{4234D49B-0245-4DF3-B780-3893943456E1}";

    public static IReadOnlyList<LaunchTarget> List()
    {
        var apps = new List<LaunchTarget>();
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application is not available.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic items = shell.NameSpace(AppsFolder).Items();
            int count = items.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                string? name = item.Name;
                string? appId = item.Path;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(appId))
                    apps.Add(new LaunchTarget(name, appId));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        var self = SelfAppId();
        return apps
            .Where(a => !string.Equals(a.AppId, self, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(a => a.AppId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The program a desktop app's Start shortcut points to, or null (packaged apps have none).</summary>
    public static string? TargetPath(string appId)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return null;
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic items = shell.NameSpace(AppsFolder).Items();
            int count = items.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                string? id = item.Path;
                if (!string.Equals(id, appId, StringComparison.OrdinalIgnoreCase)) continue;
                string? path = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
                return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
            }
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string? SelfAppId()
    {
        try
        {
            return $"{Windows.ApplicationModel.Package.Current.Id.FamilyName}!App";
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
