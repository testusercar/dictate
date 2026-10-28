using System.Collections.ObjectModel;
using System.Diagnostics;
using Dictate.Core;
using Dictate.Core.Dictation;
using Dictate.Core.Transcription;
using Dictate.Platform;
using Microsoft.UI.Dispatching;

namespace Dictate;

/// <summary>
/// Wires the Copilot key, microphone, speech engine and typing together, and keeps the recording pill
/// in step. Created on the UI thread; its events are raised there too.
/// </summary>
internal sealed class DictationHost : IDisposable
{
    private const int MaxLogLines = 300;
    private static readonly TimeSpan LocalPreviewInterval = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherQueue _ui;
    private readonly MicrophoneSource _mic = new();
    private readonly KeyboardTextSink _sink;
    private readonly KeyStateMachine _machine = new();
    private readonly DictationController _controller;
    private readonly RecordingPill _pill = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _lastKey;
    private TimeSpan _sessionStart;
    private int _levelPending;
    private float _sessionPeak;
    private Lazy<ParakeetRecognizer> _parakeet = new(() => new ParakeetRecognizer());

    public DictationHost(DispatcherQueue ui)
    {
        _ui = ui;
        _sink = new KeyboardTextSink { IsOwnWindow = h => h == _pill.Handle || h == MainWindowHandle };

        // Before the controller, which opens the microphone straight away when it's kept warm.
        _mic.Faulted += m => _ui.TryEnqueue(() => OnFailed(m));
        _mic.LevelChanged += OnLevel;

        _controller = new DictationController(_mic, _sink, CreateTranscriber, Settings.KeepMicWarm);
        _controller.StatusChanged += s => _ui.TryEnqueue(() => OnStatus(s));
        _controller.TextChanged += t => _ui.TryEnqueue(() => _pill.SetTranscript(t));
        _controller.Inserted += t => _ui.TryEnqueue(() => OnInserted(t));
        _controller.Failed += m => _ui.TryEnqueue(() => OnFailed(m));
        _controller.NothingHeard += d => Write($"heard nothing in {d.TotalSeconds:0.0} s of audio, loudest {_sessionPeak:P0}");

        _machine.ActionRequested += OnAction;
        _machine.StateChanged += (_, to) => _ui.TryEnqueue(() => OnState(to));

        PreloadLocalEngine();
    }

    public AppSettings Settings { get; } = new();
    public ApiKeyStore Keys { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public DictationStatus Status => _controller.Status;
    public string? LastText { get; private set; }
    public IntPtr MainWindowHandle { get; set; }

    public event Action<DictationStatus>? StatusChanged;
    public event Action<string>? Inserted;
    public event Action<string>? Failed;
    /// <summary>
    /// Whether Escape should cancel right now. Only in hands-free: claiming a hotkey while the
    /// Copilot key is held could disturb the hold, and letting go already ends push-to-talk.
    /// </summary>
    public event Action<bool>? CancelAvailableChanged;

    public bool CancelAvailable =>
        _controller.Status == DictationStatus.Listening && _machine.State == DictationState.HandsFree;

    public SpeechEngine Engine
    {
        get => Settings.Engine;
        set
        {
            Settings.Engine = value;
            Write($"engine {value}");
            PreloadLocalEngine();
        }
    }

    /// <summary>What has to be set up before dictation works with the chosen engine, or null when it's ready.</summary>
    public string? SetupNeeded => Settings.Engine switch
    {
        SpeechEngine.Local when !ParakeetModel.IsInstalled => "Download the local speech model in Dictate's settings to start dictating.",
        SpeechEngine.Grok when string.IsNullOrWhiteSpace(Keys.Get()) => "Add your xAI API key in Dictate's settings, or switch to the local engine.",
        _ => null,
    };

    /// <summary>Releases the local model's files, before it is replaced or deleted.</summary>
    public void UnloadLocalEngine()
    {
        var old = _parakeet;
        _parakeet = new(() => new ParakeetRecognizer());
        if (old.IsValueCreated) old.Value.Dispose();
    }

    /// <summary>Loads the local model in the background so the first dictation doesn't wait for it.</summary>
    public void PreloadLocalEngine()
    {
        if (Settings.Engine != SpeechEngine.Local || !ParakeetModel.IsInstalled) return;
        var lazy = _parakeet;
        if (lazy.IsValueCreated) return;
        _ = Task.Run(() =>
        {
            var started = _clock.Elapsed;
            try
            {
                _ = lazy.Value;
                Write($"local model loaded in {(_clock.Elapsed - started).TotalMilliseconds:0} ms");
            }
            catch (Exception e)
            {
                Write($"local model failed to load: {e.Message}");
            }
        });
    }

    public bool KeepMicWarm
    {
        get => Settings.KeepMicWarm;
        set
        {
            Settings.KeepMicWarm = value;
            _controller.KeepMicWarm = value;
        }
    }

    /// <summary>A Copilot key event, from the fast path or a dictate-key:// launch. UI thread.</summary>
    public void OnKey(KeyEvent e, string source)
    {
        var now = _clock.Elapsed;
        var gap = _lastKey is { } last ? $"+{(now - last).TotalMilliseconds:0} ms" : "";
        _lastKey = now;
        Write($"key   {e,-9} {source,-12} {gap}");
        _machine.Handle(e);
    }

    /// <summary>Checks an API key by streaming half a second of silence to Grok. Returns null when it works.</summary>
    public async Task<string?> TestKeyAsync(string key)
    {
        var started = _clock.Elapsed;
        try
        {
            await using var transcriber = new XaiStreamingTranscriber(new XaiSttOptions { ApiKey = key, Language = Settings.Language });
            await transcriber.ConnectAsync(CancellationToken.None);
            await transcriber.SendAudioAsync(new byte[16000], CancellationToken.None);
            await transcriber.FinishAsync(CancellationToken.None);
            Write($"test  ok in {(_clock.Elapsed - started).TotalMilliseconds:0} ms");
            return null;
        }
        catch (Exception e)
        {
            var message = e is TranscriptionException ? e.Message
                : e is OperationCanceledException or TimeoutException ? "Grok didn't answer in time."
                : $"Couldn't reach Grok: {e.Message}";
            Write($"test  failed: {message}");
            return message;
        }
    }

    public void Write(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        Debug.WriteLine(entry);
        AppendToFile(entry);
        if (_ui.HasThreadAccess) Add(entry);
        else _ui.TryEnqueue(() => Add(entry));

        void Add(string e)
        {
            Log.Insert(0, e);
            if (Log.Count > MaxLogLines) Log.RemoveAt(Log.Count - 1);
        }
    }

    /// <summary>The diagnostics log on disk, so it can be read after the fact: LocalState\dictate.log in the package folder.</summary>
    public static string LogPath { get; } = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "dictate.log");

    private static readonly object FileGate = new();

    private static void AppendToFile(string entry)
    {
        try
        {
            lock (FileGate)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2_000_000) File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.log"), overwrite: true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd} {entry}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never break dictation.
        }
    }

    // Throws with a message for the user when the engine isn't set up yet.
    private ITranscriber CreateTranscriber()
    {
        if (SetupNeeded is { } setup) throw new TranscriptionException(setup);
        if (Settings.Engine == SpeechEngine.Grok)
            return new XaiStreamingTranscriber(new XaiSttOptions { ApiKey = Keys.Get()!, Language = Settings.Language });

        var lazy = _parakeet;
        return new BufferedTranscriber(new DeferredRecognizer(() => lazy.Value), previewInterval: LocalPreviewInterval);
    }

    // Raised on the UI thread for key presses and on a timer thread when a single tap expires.
    private void OnAction(DictationAction action)
    {
        Write($"do    {action}");
        if (action == DictationAction.StartCapture && Settings.KeepMicWarm && !_mic.IsRunning)
            _mic.Start(); // Reopen after the device failed or changed.
        _controller.Handle(action);
    }

    private void OnState(DictationState state)
    {
        switch (state)
        {
            // A single tap also opens the mic, but the pill waits until it's clearly a dictation.
            case DictationState.PushToTalk or DictationState.HandsFree when _controller.Status == DictationStatus.Listening:
                _pill.ShowListening(_sink.Target, state == DictationState.HandsFree);
                Write($"pill  {state}");
                break;
        }
        CancelAvailableChanged?.Invoke(CancelAvailable);
    }

    private void OnStatus(DictationStatus status)
    {
        switch (status)
        {
            case DictationStatus.Listening:
                _sessionStart = _clock.Elapsed;
                _sessionPeak = 0;
                if (_machine.State is DictationState.PushToTalk or DictationState.HandsFree)
                    _pill.ShowListening(_sink.Target, _machine.State == DictationState.HandsFree);
                break;
            case DictationStatus.Transcribing:
                _pill.ShowTranscribing();
                break;
            case DictationStatus.Idle:
                _pill.HideUnlessShowingError();
                break;
        }
        Write($"state {status} at {(_clock.Elapsed - _sessionStart).TotalMilliseconds:0} ms");
        StatusChanged?.Invoke(status);
        CancelAvailableChanged?.Invoke(CancelAvailable);
    }

    private void OnInserted(string text)
    {
        LastText = text;
        Write($"typed {text.Length} chars, {(_clock.Elapsed - _sessionStart).TotalMilliseconds:0} ms after start");
        Inserted?.Invoke(text);
    }

    private void OnFailed(string message)
    {
        Write($"error {message}");
        _pill.ShowError(message, _sink.Target);
        Failed?.Invoke(message);
    }

    private void OnLevel(float peak)
    {
        // Capture thread, every 50 ms; skip if the UI hasn't caught up.
        if (peak > _sessionPeak) _sessionPeak = peak;
        if (Interlocked.Exchange(ref _levelPending, 1) == 1) return;
        _ui.TryEnqueue(() =>
        {
            Volatile.Write(ref _levelPending, 0);
            _pill.SetLevel(peak);
        });
    }

    public void Dispose()
    {
        _machine.Dispose();
        _controller.Dispose();
        _mic.Dispose();
        _pill.Hide();
        if (_parakeet.IsValueCreated) _parakeet.Value.Dispose();
    }

    /// <summary>Waits for the model to finish loading on first use, off the UI thread.</summary>
    private sealed class DeferredRecognizer(Func<IBatchRecognizer> get) : IBatchRecognizer
    {
        public string Recognize(float[] samples, int sampleRate) => get().Recognize(samples, sampleRate);
    }
}
