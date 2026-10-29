using System.Text.Json;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace FlowKey;

/// <summary>A keyboard shortcut Flow Key can send, as Wispr Flow lets you set it.</summary>
internal sealed record Shortcut(string Id, ushort[] Keys)
{
    public string Name => VirtualKeys.Describe(Keys);

    /// <summary>The setting that follows whatever Wispr Flow is set to.</summary>
    public const string MatchWispr = "wispr";

    // Wispr Flow's defaults first. Wispr needs at least one modifier and at most three keys.
    public static readonly Shortcut[] PushToTalkChoices =
    {
        new("ctrl-win", new[] { VirtualKeys.LControl, VirtualKeys.LWin }),
        new("ralt", new[] { VirtualKeys.RMenu }),
        new("ctrl-alt", new[] { VirtualKeys.LControl, VirtualKeys.LMenu }),
        new("ctrl-shift", new[] { VirtualKeys.LControl, VirtualKeys.LShift }),
        new("alt-shift", new[] { VirtualKeys.LMenu, VirtualKeys.LShift }),
    };

    public static readonly Shortcut[] HandsFreeChoices =
    {
        new("ctrl-win-space", new[] { VirtualKeys.LControl, VirtualKeys.LWin, VirtualKeys.Space }),
        new("ralt-space", new[] { VirtualKeys.RMenu, VirtualKeys.Space }),
        new("ctrl-alt-space", new[] { VirtualKeys.LControl, VirtualKeys.LMenu, VirtualKeys.Space }),
        new("ctrl-shift-space", new[] { VirtualKeys.LControl, VirtualKeys.LShift, VirtualKeys.Space }),
        new("alt-shift-space", new[] { VirtualKeys.LMenu, VirtualKeys.LShift, VirtualKeys.Space }),
    };
}

/// <summary>
/// Which shortcuts to send, stored per user in the package's local settings. By default they
/// follow Wispr Flow's own settings, read from its config.json.
/// </summary>
internal sealed class FlowSettings
{
    private readonly IPropertySet _values = ApplicationData.Current.LocalSettings.Values;

    /// <summary>The setting's id: <see cref="Shortcut.MatchWispr"/> or a fixed choice.</summary>
    public string PushToTalkChoice
    {
        get => Get(nameof(PushToTalk));
        set => _values[nameof(PushToTalk)] = value;
    }

    public string HandsFreeChoice
    {
        get => Get(nameof(HandsFree));
        set => _values[nameof(HandsFree)] = value;
    }

    public Shortcut PushToTalk => Resolve(PushToTalkChoice, Shortcut.PushToTalkChoices, WisprConfig.Read().PushToTalk);

    public Shortcut HandsFree => Resolve(HandsFreeChoice, Shortcut.HandsFreeChoices, WisprConfig.Read().HandsFree);

    private string Get(string key) => _values.TryGetValue(key, out var v) && v is string id ? id : Shortcut.MatchWispr;

    private static Shortcut Resolve(string id, Shortcut[] choices, Shortcut? fromWispr) =>
        id == Shortcut.MatchWispr
            ? fromWispr ?? choices[0]
            : choices.FirstOrDefault(c => c.Id == id) ?? choices[0];
}

/// <summary>
/// Wispr Flow's shortcuts, from %APPDATA%\Wispr Flow\config.json (prefs.cache.splitKeybinds, as
/// Windows virtual-key codes). Read only; cached until the file changes.
/// </summary>
internal static class WisprConfig
{
    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow", "config.json");

    private static readonly object Gate = new();
    private static DateTime _readAt;
    private static (Shortcut? PushToTalk, Shortcut? HandsFree) _cached;

    public static (Shortcut? PushToTalk, Shortcut? HandsFree) Read()
    {
        lock (Gate)
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(FilePath);
                if (written == _readAt) return _cached;
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                var binds = doc.RootElement.GetProperty("prefs").GetProperty("cache").GetProperty("splitKeybinds");
                _cached = (Combo(binds, "ptt", "wispr-ptt"), Combo(binds, "popo", "wispr-handsfree"));
                _readAt = written;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                          or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                _cached = default;
                _readAt = default;
            }
            return _cached;
        }
    }

    // splitKeybinds is a list of { "shortcut": [codes], "value": "ptt" } entries, and an action can
    // have several. Takes the first that is all keyboard keys (Wispr also has codes of its own for
    // mouse buttons, which can't be sent).
    private static Shortcut? Combo(JsonElement binds, string name, string id)
    {
        if (binds.ValueKind != JsonValueKind.Array) return null;
        foreach (var bind in binds.EnumerateArray())
        {
            if (bind.ValueKind != JsonValueKind.Object
                || !bind.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String || value.GetString() != name
                || !bind.TryGetProperty("shortcut", out var keys) || keys.ValueKind != JsonValueKind.Array)
                continue;
            var codes = keys.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToArray();
            if (codes.Length is > 0 and <= 3 && codes.All(c => c is > 0 and < 0xFF))
                return new Shortcut(id, codes.Select(c => (ushort)c).ToArray());
        }
        return null;
    }
}
