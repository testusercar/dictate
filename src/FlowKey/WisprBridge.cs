using System.Diagnostics;
using Dictate.Core;

namespace FlowKey;

/// <summary>
/// Turns the Copilot key into Wispr Flow's own shortcuts: a hold presses and keeps down the
/// push-to-talk shortcut until the key is let go, and a double tap (and the tap that ends it)
/// sends the hands-free shortcut. Wispr Flow does the listening and typing.
/// </summary>
internal sealed class WisprBridge : IDisposable
{
    // A safety net in case Windows never reports the release: nothing stays held down forever.
    private static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(10);

    // Keys the Copilot key itself is made of (Win + Shift + F23). If Windows still reports one of
    // them down, Wispr would see it as part of the shortcut and not match, so it is let go first.
    private static readonly ushort[] CopilotKeyParts = { VirtualKeys.LShift, VirtualKeys.F23 };

    private readonly KeyStateMachine _machine = new();
    private readonly KeyInjector _keys = new();
    private readonly FlowSettings _settings;
    private readonly Log _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _maxHold;
    private TimeSpan? _lastKey;

    public WisprBridge(FlowSettings settings, Log log)
    {
        _settings = settings;
        _log = log;
        _maxHold = new Timer(_ => OnMaxHold());
        _machine.StateChanged += OnStateChanged;
    }

    /// <summary>The shortcut most recently sent, for the window.</summary>
    public event Action<string>? Sent;

    /// <summary>A Copilot key event, from the fast path or a flowkey:// launch.</summary>
    public void OnKey(KeyEvent e, string source)
    {
        var now = _clock.Elapsed;
        var gap = _lastKey is { } last ? $"+{(now - last).TotalMilliseconds:0} ms" : "";
        _lastKey = now;
        _log.Write($"key   {e,-9} {source,-12} {gap}");
        if (e == KeyEvent.HoldStart) ReportHeldKeys();
        _machine.Handle(e);
    }

    // Raised on the thread that delivered the key, or a timer thread when a lone tap expires.
    private void OnStateChanged(DictationState from, DictationState to)
    {
        try
        {
            if (to == DictationState.PushToTalk)
            {
                ReleaseCopilotKeyParts();
                var shortcut = _settings.PushToTalk;
                _keys.Press(shortcut.Keys);
                _maxHold.Change(MaxHold, Timeout.InfiniteTimeSpan);
                Report($"down  {shortcut.Name}");
            }
            else if (from == DictationState.PushToTalk)
            {
                _maxHold.Change(Timeout.Infinite, Timeout.Infinite);
                _keys.Release();
                Report($"up    {_settings.PushToTalk.Name}");
            }

            // Into hands-free starts it in Wispr; leaving it (another tap) stops it.
            if (to == DictationState.HandsFree || from == DictationState.HandsFree)
            {
                ReleaseCopilotKeyParts();
                var shortcut = _settings.HandsFree;
                _keys.Tap(shortcut.Keys);
                Report($"tap   {shortcut.Name} ({(to == DictationState.HandsFree ? "start" : "stop")} hands-free)");
            }
        }
        catch (Exception e)
        {
            _keys.Release();
            _log.Write($"error {e.Message}");
        }
    }

    private void Report(string line)
    {
        _log.Write(line);
        Sent?.Invoke(line);
    }

    private void ReportHeldKeys()
    {
        var down = new[] { VirtualKeys.LWin, VirtualKeys.RWin, VirtualKeys.LShift, VirtualKeys.RShift, VirtualKeys.LControl, VirtualKeys.LMenu, VirtualKeys.F23 }
            .Where(KeyInjector.IsDown).ToArray();
        _log.Write(down.Length == 0 ? "held  nothing" : $"held  {VirtualKeys.Describe(down)}");
    }

    private void ReleaseCopilotKeyParts()
    {
        foreach (var key in CopilotKeyParts.Where(KeyInjector.IsDown))
        {
            _keys.ReleaseStray(key);
            _log.Write($"let go of a stray {VirtualKeys.Name(key)}");
        }
    }

    private void OnMaxHold()
    {
        _log.Write($"no release after {MaxHold.TotalMinutes:0} minutes; letting go");
        _machine.Handle(KeyEvent.Cancel);
    }

    public void Dispose()
    {
        _maxHold.Dispose();
        _machine.Dispose();
        _keys.Dispose();
    }
}
