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

    /// <summary>Which speech engine dictation uses.</summary>
    public SpeechEngine Engine
    {
        get => Enum.TryParse<SpeechEngine>(Get(nameof(Engine), nameof(SpeechEngine.Local)), out var e) ? e : SpeechEngine.Local;
        set => _values[nameof(Engine)] = value.ToString();
    }

    /// <summary>Two-letter language code for Grok, or empty to auto-detect.</summary>
    public string Language
    {
        get => Get(nameof(Language), "en");
        set => _values[nameof(Language)] = value;
    }

    private T Get<T>(string key, T fallback) => _values.TryGetValue(key, out var v) && v is T t ? t : fallback;
}

internal enum SpeechEngine
{
    /// <summary>Parakeet on this PC: private, free, works offline.</summary>
    Local,
    /// <summary>xAI's Grok speech-to-text: needs an API key and a connection.</summary>
    Grok,
}
