using Windows.Foundation.Collections;
using Windows.Storage;

namespace FlowKey;

/// <summary>A keyboard shortcut Flow Key can send, as Wispr Flow lets you set it.</summary>
internal sealed record Shortcut(string Id, ushort[] Keys)
{
    public string Name => VirtualKeys.Describe(Keys);

    // Wispr Flow's defaults first. Wispr needs at least one modifier and at most three keys.
    public static readonly Shortcut[] PushToTalkChoices =
    {
        new("ctrl-win", new[] { VirtualKeys.LControl, VirtualKeys.LWin }),
        new("ctrl-alt", new[] { VirtualKeys.LControl, VirtualKeys.LMenu }),
        new("ctrl-shift", new[] { VirtualKeys.LControl, VirtualKeys.LShift }),
        new("alt-shift", new[] { VirtualKeys.LMenu, VirtualKeys.LShift }),
    };

    public static readonly Shortcut[] HandsFreeChoices =
    {
        new("ctrl-win-space", new[] { VirtualKeys.LControl, VirtualKeys.LWin, VirtualKeys.Space }),
        new("ctrl-alt-space", new[] { VirtualKeys.LControl, VirtualKeys.LMenu, VirtualKeys.Space }),
        new("ctrl-shift-space", new[] { VirtualKeys.LControl, VirtualKeys.LShift, VirtualKeys.Space }),
        new("alt-shift-space", new[] { VirtualKeys.LMenu, VirtualKeys.LShift, VirtualKeys.Space }),
    };
}

/// <summary>Which shortcuts to send, stored per user in the package's local settings.</summary>
internal sealed class FlowSettings
{
    private readonly IPropertySet _values = ApplicationData.Current.LocalSettings.Values;

    public Shortcut PushToTalk
    {
        get => Find(Shortcut.PushToTalkChoices, nameof(PushToTalk));
        set => _values[nameof(PushToTalk)] = value.Id;
    }

    public Shortcut HandsFree
    {
        get => Find(Shortcut.HandsFreeChoices, nameof(HandsFree));
        set => _values[nameof(HandsFree)] = value.Id;
    }

    private Shortcut Find(Shortcut[] choices, string key) =>
        _values.TryGetValue(key, out var v) && v is string id && choices.FirstOrDefault(c => c.Id == id) is { } s ? s : choices[0];
}
