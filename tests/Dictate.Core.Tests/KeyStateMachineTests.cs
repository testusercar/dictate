using Dictate.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dictate.Core.Tests;

public class KeyStateMachineTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly KeyStateMachine _sm;
    private readonly List<DictationAction> _actions = new();

    public KeyStateMachineTests()
    {
        _sm = new KeyStateMachine(_time);
        _sm.ActionRequested += _actions.Add;
    }

    [Fact]
    public void Hold_then_release_is_push_to_talk()
    {
        _sm.Handle(KeyEvent.HoldStart);
        Assert.Equal(DictationState.PushToTalk, _sm.State);

        _sm.Handle(KeyEvent.HoldEnd);
        Assert.Equal(DictationState.Idle, _sm.State);
        Assert.Equal(new[] { DictationAction.StartCapture, DictationAction.StopAndTranscribe }, _actions);
    }

    [Fact]
    public void Double_tap_starts_hands_free_and_a_tap_stops_it()
    {
        _sm.Handle(KeyEvent.Tap);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _sm.Handle(KeyEvent.Tap);
        Assert.Equal(DictationState.HandsFree, _sm.State);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(DictationState.HandsFree, _sm.State);

        _sm.Handle(KeyEvent.Tap);
        Assert.Equal(DictationState.Idle, _sm.State);
        Assert.Equal(new[] { DictationAction.StartCapture, DictationAction.StopAndTranscribe }, _actions);
    }

    [Fact]
    public void Single_tap_times_out_and_discards_audio()
    {
        _sm.Handle(KeyEvent.Tap);
        Assert.Equal(DictationState.WaitingForSecondTap, _sm.State);

        _time.Advance(TimeSpan.FromMilliseconds(399));
        Assert.Equal(DictationState.WaitingForSecondTap, _sm.State);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(DictationState.Idle, _sm.State);
        Assert.Equal(new[] { DictationAction.StartCapture, DictationAction.DiscardCapture }, _actions);
    }

    [Fact]
    public void Second_tap_after_window_starts_a_new_wait_not_hands_free()
    {
        _sm.Handle(KeyEvent.Tap);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        _sm.Handle(KeyEvent.Tap);
        Assert.Equal(DictationState.WaitingForSecondTap, _sm.State);
    }

    [Fact]
    public void Tap_then_hold_becomes_push_to_talk_without_restarting_capture()
    {
        _sm.Handle(KeyEvent.Tap);
        _sm.Handle(KeyEvent.HoldStart);
        Assert.Equal(DictationState.PushToTalk, _sm.State);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(DictationState.PushToTalk, _sm.State);

        _sm.Handle(KeyEvent.HoldEnd);
        Assert.Equal(new[] { DictationAction.StartCapture, DictationAction.StopAndTranscribe }, _actions);
    }

    [Fact]
    public void Hold_stops_hands_free_and_its_release_is_ignored()
    {
        _sm.Handle(KeyEvent.Tap);
        _sm.Handle(KeyEvent.Tap);
        _sm.Handle(KeyEvent.HoldStart);
        Assert.Equal(DictationState.Idle, _sm.State);

        _sm.Handle(KeyEvent.HoldEnd);
        Assert.Equal(DictationState.Idle, _sm.State);
        Assert.Equal(new[] { DictationAction.StartCapture, DictationAction.StopAndTranscribe }, _actions);
    }

    [Theory]
    [InlineData(KeyEvent.HoldStart)]
    [InlineData(KeyEvent.Tap)]
    public void Cancel_discards_from_any_recording_state(KeyEvent start)
    {
        _sm.Handle(start);
        _sm.Handle(KeyEvent.Cancel);
        Assert.Equal(DictationState.Idle, _sm.State);
        Assert.Equal(new[] { DictationAction.StartCapture, DictationAction.DiscardCapture }, _actions);

        // The timer must not fire a second discard later.
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _actions.Count);
    }

    [Fact]
    public void Cancel_when_idle_does_nothing()
    {
        _sm.Handle(KeyEvent.Cancel);
        Assert.Empty(_actions);
    }

    [Fact]
    public void State_changes_are_reported()
    {
        var changes = new List<(DictationState, DictationState)>();
        _sm.StateChanged += (f, t) => changes.Add((f, t));

        _sm.Handle(KeyEvent.HoldStart);
        _sm.Handle(KeyEvent.HoldEnd);

        Assert.Equal(new[]
        {
            (DictationState.Idle, DictationState.PushToTalk),
            (DictationState.PushToTalk, DictationState.Idle),
        }, changes);
    }
}
