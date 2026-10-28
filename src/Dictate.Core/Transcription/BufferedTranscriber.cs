namespace Dictate.Core.Transcription;

/// <summary>A speech recognizer that transcribes a whole clip at once, such as a local model.</summary>
public interface IBatchRecognizer
{
    /// <summary>Transcribes mono samples in [-1, 1]. Blocking; may be called from any thread.</summary>
    string Recognize(float[] samples, int sampleRate);
}

/// <summary>
/// Adapts an <see cref="IBatchRecognizer"/> to a dictation session: audio is collected while the
/// user talks and recognized when they finish. With a preview interval, the audio so far is also
/// recognized in the background every so often so the pill can show what was heard.
/// </summary>
public sealed class BufferedTranscriber : ITranscriber
{
    /// <summary>Clips shorter than this are treated as silence (a stray tap).</summary>
    public static readonly TimeSpan MinimumSpeech = TimeSpan.FromMilliseconds(250);

    /// <summary>Previews re-recognize everything so far, so they stop once a dictation gets this long.</summary>
    public static readonly TimeSpan MaximumPreview = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly IBatchRecognizer _recognizer;
    private readonly int _sampleRate;
    private readonly TimeSpan? _previewInterval;
    private readonly TimeProvider _time;
    private readonly List<float> _samples = new();
    private DateTimeOffset _lastPreview;
    private Task _preview = Task.CompletedTask;
    private int? _carry;
    private bool _finishing;

    public BufferedTranscriber(IBatchRecognizer recognizer, int sampleRate = 16000, TimeSpan? previewInterval = null, TimeProvider? time = null)
    {
        _recognizer = recognizer;
        _sampleRate = sampleRate;
        _previewInterval = previewInterval;
        _time = time ?? TimeProvider.System;
        _lastPreview = _time.GetUtcNow();
    }

    public event Action<string>? TextChanged;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        lock (_gate)
        {
            Append(pcm.Span);
            if (ShouldPreview()) _preview = Task.Run(PreviewAsync);
        }
        return Task.CompletedTask;
    }

    public async Task<string> FinishAsync(CancellationToken ct)
    {
        Task preview;
        lock (_gate)
        {
            _finishing = true;
            preview = _preview;
        }
        // The recognizer may be busy with a preview; let it finish rather than run two at once.
        try { await preview.WaitAsync(ct).ConfigureAwait(false); } catch (Exception) when (!ct.IsCancellationRequested) { }

        float[] samples;
        lock (_gate) samples = _samples.ToArray();
        if (samples.Length < _sampleRate * MinimumSpeech.TotalSeconds) return "";

        var text = await Task.Run(() => _recognizer.Recognize(samples, _sampleRate), ct).ConfigureAwait(false);
        return text.Trim();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) _finishing = true;
        return ValueTask.CompletedTask;
    }

    // 16-bit little-endian PCM; a buffer may end halfway through a sample.
    private void Append(ReadOnlySpan<byte> pcm)
    {
        var i = 0;
        if (_carry is { } low && pcm.Length > 0)
        {
            _samples.Add((short)(low | pcm[0] << 8) / 32768f);
            _carry = null;
            i = 1;
        }
        for (; i + 1 < pcm.Length; i += 2)
            _samples.Add((short)(pcm[i] | pcm[i + 1] << 8) / 32768f);
        if (i < pcm.Length) _carry = pcm[i];
    }

    private bool ShouldPreview()
    {
        if (_previewInterval is not { } interval || _finishing || !_preview.IsCompleted) return false;
        if (_samples.Count < _sampleRate * MinimumSpeech.TotalSeconds) return false;
        if (_samples.Count > _sampleRate * MaximumPreview.TotalSeconds) return false;
        var now = _time.GetUtcNow();
        if (now - _lastPreview < interval) return false;
        _lastPreview = now;
        return true;
    }

    private void PreviewAsync()
    {
        float[] samples;
        lock (_gate) samples = _samples.ToArray();
        var text = _recognizer.Recognize(samples, _sampleRate).Trim();
        bool finishing;
        lock (_gate) finishing = _finishing;
        if (!finishing && text.Length > 0) TextChanged?.Invoke(text);
    }
}
