using Dictate.Core.Dictation;
using NAudio.Wave;

namespace Dictate.Platform;

/// <summary>
/// The default Windows microphone as 16 kHz, 16-bit mono PCM in 50 ms buffers.
/// Follows the default device (WAVE_MAPPER) and reopens on the next Start after a failure.
/// </summary>
internal sealed class MicrophoneSource : IAudioSource, IDisposable
{
    private const int WaveMapper = -1;

    private readonly object _gate = new();
    private readonly int _sampleRate;
    private WaveInEvent? _wave;

    public MicrophoneSource(int sampleRate = 16000) => _sampleRate = sampleRate;

    public event Action<ReadOnlyMemory<byte>>? AudioAvailable;
    /// <summary>Peak level of each buffer, 0 to 1. Raised on the capture thread.</summary>
    public event Action<float>? LevelChanged;
    /// <summary>The microphone could not be opened or stopped unexpectedly.</summary>
    public event Action<string>? Faulted;

    public bool IsRunning
    {
        get { lock (_gate) return _wave is not null; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_wave is not null) return;
            var wave = new WaveInEvent
            {
                DeviceNumber = WaveMapper,
                WaveFormat = new WaveFormat(_sampleRate, 16, 1),
                BufferMilliseconds = 50,
                NumberOfBuffers = 3,
            };
            wave.DataAvailable += OnData;
            wave.RecordingStopped += OnStopped;
            try
            {
                wave.StartRecording();
                _wave = wave;
            }
            catch (Exception e)
            {
                wave.Dispose();
                Faulted?.Invoke(Describe(e));
            }
        }
    }

    public void Stop()
    {
        WaveInEvent? wave;
        lock (_gate)
        {
            wave = _wave;
            _wave = null;
        }
        // Disposed in OnStopped once the capture thread has let go of the device.
        wave?.StopRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var pcm = new ReadOnlyMemory<byte>(e.Buffer, 0, e.BytesRecorded);
        AudioAvailable?.Invoke(pcm);

        if (LevelChanged is { } level)
        {
            var peak = 0;
            var span = pcm.Span;
            for (var i = 0; i + 1 < span.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((int)(short)(span[i] | span[i + 1] << 8)));
            level(peak / 32768f);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        var wave = (WaveInEvent)sender!;
        bool wasCurrent;
        lock (_gate)
        {
            wasCurrent = ReferenceEquals(_wave, wave);
            if (wasCurrent) _wave = null;
        }
        wave.DataAvailable -= OnData;
        wave.RecordingStopped -= OnStopped;
        wave.Dispose();
        if (e.Exception is not null && wasCurrent) Faulted?.Invoke(Describe(e.Exception));
    }

    private static string Describe(Exception e) =>
        $"Couldn't use the microphone ({e.Message}). Check Settings > Privacy & security > Microphone.";

    public void Dispose() => Stop();
}
