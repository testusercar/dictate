using System.Collections.ObjectModel;
using System.Diagnostics;
using Dictate.Core;
using Dictate.Core.Dictation;
using Dictate.Core.Transcription;
using Dictate.Platform;
using Microsoft.UI.Dispatching;

namespace Dictate;

/// <summary>
/// Wires the Copilot key, microphone, Grok and typing together, and keeps the recording pill
/// in step. Created on the UI thread; its events are raised there too.
/// </summary>
internal sealed class DictationHost : IDisposable
{
    private const int MaxLogLines = 300;

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

        _machine.ActionRequested += OnAction;
        _machine.StateChanged += (_, to) => _ui.TryEnqueue(() => OnState(to));
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
    /// <summary>Something only the settings window can fix, such as a missing API key.</summary>
    public event Action? SettingsNeeded;

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
        if (_ui.HasThreadAccess) Add(entry);
        else _ui.TryEnqueue(() => Add(entry));

        void Add(string e)
        {
            Log.Insert(0, e);
            if (Log.Count > MaxLogLines) Log.RemoveAt(Log.Count - 1);
        }
    }

    private ITranscriber? CreateTranscriber()
    {
        var key = Keys.Get();
        return string.IsNullOrWhiteSpace(key)
            ? null
            : new XaiStreamingTranscriber(new XaiSttOptions { ApiKey = key, Language = Settings.Language });
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
                break;
        }
    }

    private void OnStatus(DictationStatus status)
    {
        switch (status)
        {
            case DictationStatus.Listening:
                _sessionStart = _clock.Elapsed;
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
        if (string.IsNullOrWhiteSpace(Keys.Get())) SettingsNeeded?.Invoke();
    }

    private void OnLevel(float peak)
    {
        // Capture thread, every 50 ms; skip if the UI hasn't caught up.
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
    }
}
