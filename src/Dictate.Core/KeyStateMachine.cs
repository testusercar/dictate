namespace Dictate.Core;

/// <summary>Raw key events as Windows reports them for the Copilot key (or Win + C).</summary>
public enum KeyEvent
{
    /// <summary>Pressed and released before the 300 ms hold threshold.</summary>
    Tap,
    /// <summary>Held past the 300 ms threshold.</summary>
    HoldStart,
    /// <summary>Released after a hold.</summary>
    HoldEnd,
    /// <summary>User cancelled (Escape).</summary>
    Cancel,
}

public enum DictationState
{
    Idle,
    /// <summary>One tap seen; the mic is open in case a second tap follows.</summary>
    WaitingForSecondTap,
    PushToTalk,
    HandsFree,
}

/// <summary>What the app should do with the microphone and transcript.</summary>
public enum DictationAction
{
    /// <summary>Open the microphone and start buffering audio.</summary>
    StartCapture,
    /// <summary>Close the microphone, transcribe what was captured and type it.</summary>
    StopAndTranscribe,
    /// <summary>Close the microphone and throw the audio away.</summary>
    DiscardCapture,
}

/// <summary>
/// Turns Copilot key events into dictation actions:
/// hold = push-to-talk, double tap = hands-free toggle, Escape = cancel.
/// Thread-safe; the double-tap timer fires on a thread-pool thread.
/// </summary>
public sealed class KeyStateMachine : IDisposable
{
    public static readonly TimeSpan DefaultDoubleTapWindow = TimeSpan.FromMilliseconds(400);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _doubleTapWindow;
    private ITimer? _doubleTapTimer;

    public KeyStateMachine(TimeProvider? time = null, TimeSpan? doubleTapWindow = null)
    {
        _time = time ?? TimeProvider.System;
        _doubleTapWindow = doubleTapWindow ?? DefaultDoubleTapWindow;
    }

    public DictationState State { get; private set; } = DictationState.Idle;

    /// <summary>Raised for every action, on the thread that caused it.</summary>
    public event Action<DictationAction>? ActionRequested;

    /// <summary>Raised after every state change, with (from, to).</summary>
    public event Action<DictationState, DictationState>? StateChanged;

    public void Handle(KeyEvent e)
    {
        var actions = new List<DictationAction>(2);
        DictationState from, to;
        lock (_gate)
        {
            from = State;
            to = Next(e, actions);
            State = to;
        }
        Publish(from, to, actions);
    }

    private DictationState Next(KeyEvent e, List<DictationAction> actions)
    {
        if (e == KeyEvent.Cancel)
        {
            StopTimer();
            if (State != DictationState.Idle) actions.Add(DictationAction.DiscardCapture);
            return DictationState.Idle;
        }

        switch (State)
        {
            case DictationState.Idle:
                if (e == KeyEvent.Tap)
                {
                    actions.Add(DictationAction.StartCapture);
                    StartTimer();
                    return DictationState.WaitingForSecondTap;
                }
                if (e == KeyEvent.HoldStart)
                {
                    actions.Add(DictationAction.StartCapture);
                    return DictationState.PushToTalk;
                }
                // A stray HoldEnd (after a hold that stopped hands-free or was cancelled) is ignored.
                return DictationState.Idle;

            case DictationState.WaitingForSecondTap:
                StopTimer();
                // Capture is already running; keep it.
                if (e == KeyEvent.Tap) return DictationState.HandsFree;
                if (e == KeyEvent.HoldStart) return DictationState.PushToTalk;
                return State;

            case DictationState.PushToTalk:
                if (e == KeyEvent.HoldEnd)
                {
                    actions.Add(DictationAction.StopAndTranscribe);
                    return DictationState.Idle;
                }
                return State;

            case DictationState.HandsFree:
                if (e == KeyEvent.Tap || e == KeyEvent.HoldStart)
                {
                    actions.Add(DictationAction.StopAndTranscribe);
                    return DictationState.Idle;
                }
                return State;

            default:
                return State;
        }
    }

    private void OnDoubleTapTimeout()
    {
        var actions = new List<DictationAction>(1);
        DictationState from;
        lock (_gate)
        {
            if (State != DictationState.WaitingForSecondTap) return;
            StopTimer();
            from = State;
            State = DictationState.Idle;
            actions.Add(DictationAction.DiscardCapture);
        }
        Publish(from, DictationState.Idle, actions);
    }

    private void StartTimer()
    {
        StopTimer();
        _doubleTapTimer = _time.CreateTimer(_ => OnDoubleTapTimeout(), null, _doubleTapWindow, Timeout.InfiniteTimeSpan);
    }

    private void StopTimer()
    {
        _doubleTapTimer?.Dispose();
        _doubleTapTimer = null;
    }

    private void Publish(DictationState from, DictationState to, List<DictationAction> actions)
    {
        foreach (var a in actions) ActionRequested?.Invoke(a);
        if (from != to) StateChanged?.Invoke(from, to);
    }

    public void Dispose()
    {
        lock (_gate) StopTimer();
    }
}
