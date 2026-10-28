using System.Threading.Channels;
using Dictate.Core.Audio;
using Dictate.Core.Transcription;

namespace Dictate.Core.Dictation;

/// <summary>Microphone delivering 16-bit mono PCM at the controller's sample rate.</summary>
public interface IAudioSource
{
    /// <summary>Raised on a capture thread. The buffer may be reused after the handler returns.</summary>
    event Action<ReadOnlyMemory<byte>>? AudioAvailable;
    void Start();
    void Stop();
}

/// <summary>Where finished text goes, normally the app that had focus when dictation started.</summary>
public interface ITextSink
{
    /// <summary>Called when dictation starts, so the sink can remember the focused window.</summary>
    void RememberTarget();
    Task InsertAsync(string text, CancellationToken ct);
}

public enum DictationStatus
{
    Idle,
    Listening,
    Transcribing,
}

/// <summary>
/// Runs dictation sessions: mic audio (plus pre-roll) streams to a transcriber while recording,
/// and the final text is inserted when the key is released or hands-free is stopped.
/// Driven by <see cref="KeyStateMachine"/> actions.
/// </summary>
public sealed class DictationController : IDisposable
{
    private readonly object _gate = new();
    private readonly IAudioSource _audio;
    private readonly ITextSink _sink;
    private readonly Func<ITranscriber?> _createTranscriber;
    private readonly PreRollBuffer _preRoll;
    private readonly int _sampleRate;
    private Session? _session;
    private bool _keepMicWarm;
    private int _finishing;

    public DictationController(
        IAudioSource audio,
        ITextSink sink,
        Func<ITranscriber?> createTranscriber,
        bool keepMicWarm = true,
        TimeSpan? preRoll = null,
        int sampleRate = 16000)
    {
        _audio = audio;
        _sink = sink;
        _createTranscriber = createTranscriber;
        _keepMicWarm = keepMicWarm;
        _sampleRate = sampleRate;
        _preRoll = PreRollBuffer.ForDuration(preRoll ?? TimeSpan.FromMilliseconds(500), sampleRate);
        _audio.AudioAvailable += OnAudio;
        if (_keepMicWarm) _audio.Start();
    }

    public DictationStatus Status { get; private set; } = DictationStatus.Idle;

    public event Action<DictationStatus>? StatusChanged;
    /// <summary>Text heard so far in the current dictation.</summary>
    public event Action<string>? TextChanged;
    public event Action<string>? Inserted;
    /// <summary>A dictation finished with no text; carries how much audio the engine got.</summary>
    public event Action<TimeSpan>? NothingHeard;
    public event Action<string>? Failed;

    /// <summary>
    /// Keeps the microphone open between dictations so the 300 ms before a hold is captured.
    /// The audio stays in memory and is discarded unless a dictation starts.
    /// </summary>
    public bool KeepMicWarm
    {
        get { lock (_gate) return _keepMicWarm; }
        set
        {
            lock (_gate)
            {
                if (_keepMicWarm == value) return;
                _keepMicWarm = value;
                if (_session is not null) return; // Applied when the session ends.
                if (value) _audio.Start();
                else
                {
                    _audio.Stop();
                    _preRoll.Drain();
                }
            }
        }
    }

    public void Handle(DictationAction action)
    {
        switch (action)
        {
            case DictationAction.StartCapture:
                Start();
                break;
            case DictationAction.StopAndTranscribe:
                if (TakeSession() is { } s) _ = FinishAsync(s);
                break;
            case DictationAction.DiscardCapture:
                if (TakeSession() is { } d) _ = DiscardAsync(d);
                break;
        }
    }

    private void Start()
    {
        ITranscriber? transcriber;
        try
        {
            transcriber = _createTranscriber();
        }
        catch (Exception e)
        {
            Failed?.Invoke(e.Message);
            return;
        }
        if (transcriber is null)
        {
            Failed?.Invoke("Add your xAI API key in Dictate's settings to start dictating.");
            return;
        }

        lock (_gate)
        {
            if (_session is not null)
            {
                _ = transcriber.DisposeAsync().AsTask();
                return;
            }

            _sink.RememberTarget();
            var session = new Session(transcriber);
            transcriber.TextChanged += text =>
            {
                if (ReferenceEquals(Volatile.Read(ref _session), session)) TextChanged?.Invoke(text);
            };

            if (_keepMicWarm)
            {
                var pre = _preRoll.Drain();
                if (pre.Length > 0) session.Audio.Writer.TryWrite(pre);
            }
            else
            {
                _audio.Start();
            }

            session.Pump = Task.Run(() => PumpAsync(session));
            _session = session;
        }
        SetStatus(DictationStatus.Listening);
    }

    private Session? TakeSession()
    {
        lock (_gate)
        {
            var s = _session;
            if (s is null) return null;
            _session = null;
            s.Audio.Writer.TryComplete();
            if (!_keepMicWarm) _audio.Stop();
            return s;
        }
    }

    private void OnAudio(ReadOnlyMemory<byte> pcm)
    {
        lock (_gate)
        {
            if (_session is { } s)
                s.Audio.Writer.TryWrite(pcm.ToArray());
            else if (_keepMicWarm)
                _preRoll.Write(pcm.Span);
        }
    }

    private static async Task PumpAsync(Session s)
    {
        await s.Transcriber.ConnectAsync(s.Cancel.Token).ConfigureAwait(false);
        await foreach (var chunk in s.Audio.Reader.ReadAllAsync(s.Cancel.Token).ConfigureAwait(false))
        {
            await s.Transcriber.SendAudioAsync(chunk, s.Cancel.Token).ConfigureAwait(false);
            s.BytesSent += chunk.Length;
        }
    }

    private async Task FinishAsync(Session s)
    {
        Interlocked.Increment(ref _finishing);
        SetStatus(DictationStatus.Transcribing);
        try
        {
            await s.Pump!.ConfigureAwait(false);
            var text = (await s.Transcriber.FinishAsync(CancellationToken.None).ConfigureAwait(false)).Trim();
            if (text.Length > 0)
            {
                await _sink.InsertAsync(text, CancellationToken.None).ConfigureAwait(false);
                Inserted?.Invoke(text);
            }
            else
            {
                NothingHeard?.Invoke(TimeSpan.FromSeconds(s.BytesSent / 2.0 / _sampleRate));
            }
        }
        catch (Exception e)
        {
            Failed?.Invoke(Describe(e));
        }
        finally
        {
            await DisposeQuietlyAsync(s).ConfigureAwait(false);
            Interlocked.Decrement(ref _finishing);
            SettleStatus();
        }
    }

    private async Task DiscardAsync(Session s)
    {
        s.Cancel.Cancel();
        await DisposeQuietlyAsync(s).ConfigureAwait(false);
        SettleStatus();
    }

    private static async Task DisposeQuietlyAsync(Session s)
    {
        if (s.Pump is not null)
        {
            try { await s.Pump.ConfigureAwait(false); } catch { }
        }
        try { await s.Transcriber.DisposeAsync().ConfigureAwait(false); } catch { }
        s.Cancel.Dispose();
    }

    private void SettleStatus()
    {
        DictationStatus next;
        lock (_gate)
        {
            next = _session is not null ? DictationStatus.Listening
                : Volatile.Read(ref _finishing) > 0 ? DictationStatus.Transcribing
                : DictationStatus.Idle;
        }
        SetStatus(next);
    }

    private void SetStatus(DictationStatus status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private static string Describe(Exception e) => e switch
    {
        TranscriptionException t => t.Message,
        OperationCanceledException or TimeoutException => "Grok took too long to answer. Try again.",
        AggregateException { InnerException: { } inner } => Describe(inner),
        _ => $"Dictation failed: {e.Message}",
    };

    public void Dispose()
    {
        _audio.AudioAvailable -= OnAudio;
        _audio.Stop();
        if (TakeSession() is { } s) _ = DiscardAsync(s);
    }

    private sealed class Session(ITranscriber transcriber)
    {
        public ITranscriber Transcriber { get; } = transcriber;
        public Channel<ReadOnlyMemory<byte>> Audio { get; } =
            Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });
        public CancellationTokenSource Cancel { get; } = new();
        public Task? Pump { get; set; }
        /// <summary>Written only by the pump, read after it completes.</summary>
        public long BytesSent { get; set; }
    }
}
