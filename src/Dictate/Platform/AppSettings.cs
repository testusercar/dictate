using Windows.Foundation.Collections;
using Windows.Storage;

namespace Dictate.Platform;

/// <summary>Small preferences, stored per user in the package's local settings.</summary>
internal sealed class AppSettings
{
    private readonly IPropertySet _values = ApplicationData.Current.LocalSettings.Values;

    public bool KeepMicWarm
    {
        get => Get(nameof(KeepMicWarm), true);
        set => _values[nameof(KeepMicWarm)] = value;
    }

    /// <summary>Two-letter language code for Grok, or empty to auto-detect.</summary>
    public string Language
    {
        get => Get(nameof(Language), "en");
        set => _values[nameof(Language)] = value;
    }

    private T Get<T>(string key, T fallback) => _values.TryGetValue(key, out var v) && v is T t ? t : fallback;
}
