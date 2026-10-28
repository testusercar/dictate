using Dictate.Core.Dictation;
using Dictate.Core.Transcription;
using Xunit;

namespace Dictate.Core.Tests;

public class DictationControllerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private sealed class FakeAudio : IAudioSource
    {
        public event Action<ReadOnlyMemory<byte>>? AudioAvailable;
        public bool Running { get; private set; }
        public int Starts { get; private set; }
        public void Start() { Running = true; Starts++; }
        public void Stop() => Running = false;
        public void Speak(params byte[] pcm) => AudioAvailable?.Invoke(pcm);
    }

    private sealed class FakeSink : ITextSink
    {
        public int Remembered { get; private set; }
        public TaskCompletionSource<string> Text { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void RememberTarget() => Remembered++;
        public Task InsertAsync(string text, CancellationToken ct) { Text.TrySetResult(text); return Task.CompletedTask; }
    }

    private sealed class FakeTranscriber(string result) : ITranscriber
    {
        private readonly List<byte> _received = new();
        public event Action<string>? TextChanged;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Finished { get; private set; }
        public byte[] Received { get { lock (_received) return _received.ToArray(); } }
        public Exception? ConnectError { get; init; }

        public Task ConnectAsync(CancellationToken ct) => ConnectError is null ? Task.CompletedTask : Task.FromException(ConnectError);
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
        {
            lock (_received) _received.AddRange(pcm.ToArray());
            TextChanged?.Invoke("partial");
            return Task.CompletedTask;
        }
        public Task<string> FinishAsync(CancellationToken ct) { Finished = true; return Task.FromResult(result); }
        public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task Push_to_talk_sends_pre_roll_and_live_audio_then_inserts_text()
    {
        var audio = new FakeAudio();
        var sink = new FakeSink();
        var t = new FakeTranscriber("  Hello world.  ");
        using var c = new DictationController(audio, sink, () => t, keepMicWarm: true);
        Assert.True(audio.Running);

        audio.Speak(1, 2);                     // before the hold registers
        c.Handle(DictationAction.StartCapture);
        audio.Speak(3, 4);
        c.Handle(DictationAction.StopAndTranscribe);

        Assert.Equal("Hello world.", await sink.Text.Task.WaitAsync(Wait));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, t.Received);
        Assert.Equal(1, sink.Remembered);
        await t.Disposed.Task.WaitAsync(Wait);
        Assert.True(audio.Running); // stays warm
    }

    [Fact]
    public async Task Discard_never_inserts_and_disposes_the_transcriber()
    {
        var audio = new FakeAudio();
        var sink = new FakeSink();
        var t = new FakeTranscriber("should not appear");
        using var c = new DictationController(audio, sink, () => t);

        c.Handle(DictationAction.StartCapture);
        c.Handle(DictationAction.DiscardCapture);

        await t.Disposed.Task.WaitAsync(Wait);
        Assert.False(t.Finished);
        Assert.False(sink.Text.Task.IsCompleted);
    }

    [Fact]
    public async Task Cold_mic_opens_only_while_recording()
    {
        var audio = new FakeAudio();
        var sink = new FakeSink();
        var t = new FakeTranscriber("ok");
        using var c = new DictationController(audio, sink, () => t, keepMicWarm: false);
        Assert.False(audio.Running);

        audio.Speak(9);                        // ignored: not recording, not warm
        c.Handle(DictationAction.StartCapture);
        Assert.True(audio.Running);
        audio.Speak(5);
        c.Handle(DictationAction.StopAndTranscribe);
        Assert.False(audio.Running);

        await sink.Text.Task.WaitAsync(Wait);
        Assert.Equal(new byte[] { 5 }, t.Received);
    }

    [Fact]
    public void Missing_api_key_reports_and_does_not_record()
    {
        var audio = new FakeAudio();
        string? error = null;
        using var c = new DictationController(audio, new FakeSink(), () => null, keepMicWarm: false);
        c.Failed += e => error = e;

        c.Handle(DictationAction.StartCapture);

        Assert.Contains("API key", error);
        Assert.False(audio.Running);
        Assert.Equal(DictationStatus.Idle, c.Status);
    }

    [Fact]
    public async Task Connection_failure_is_reported_on_stop()
    {
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new FakeTranscriber("x") { ConnectError = new TranscriptionException("Grok rejected the API key.") };
        var sink = new FakeSink();
        using var c = new DictationController(new FakeAudio(), sink, () => t);
        c.Failed += e => failed.TrySetResult(e);

        c.Handle(DictationAction.StartCapture);
        c.Handle(DictationAction.StopAndTranscribe);

        Assert.Equal("Grok rejected the API key.", await failed.Task.WaitAsync(Wait));
        Assert.False(sink.Text.Task.IsCompleted);
    }

    [Fact]
    public async Task Status_goes_listening_then_back_to_idle()
    {
        var statuses = new List<DictationStatus>();
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var c = new DictationController(new FakeAudio(), new FakeSink(), () => new FakeTranscriber("hi"));
        c.StatusChanged += s => { lock (statuses) statuses.Add(s); if (s == DictationStatus.Idle) idle.TrySetResult(); };

        c.Handle(DictationAction.StartCapture);
        c.Handle(DictationAction.StopAndTranscribe);
        await idle.Task.WaitAsync(Wait);

        Assert.Equal(DictationStatus.Listening, statuses[0]);
        Assert.Equal(DictationStatus.Idle, statuses[^1]);
    }

    [Fact]
    public void Turning_keep_warm_off_stops_the_mic_when_idle()
    {
        var audio = new FakeAudio();
        using var c = new DictationController(audio, new FakeSink(), () => null, keepMicWarm: true);
        c.KeepMicWarm = false;
        Assert.False(audio.Running);
        c.KeepMicWarm = true;
        Assert.True(audio.Running);
    }

    [Fact]
    public async Task An_empty_result_reports_how_much_audio_was_sent()
    {
        var audio = new FakeAudio();
        var sink = new FakeSink();
        var t = new FakeTranscriber("   ");
        using var c = new DictationController(audio, sink, () => t, keepMicWarm: true, sampleRate: 4);
        var heard = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.NothingHeard += d => heard.TrySetResult(d);

        c.Handle(DictationAction.StartCapture);
        audio.Speak(1, 2, 3, 4);
        c.Handle(DictationAction.StopAndTranscribe);

        Assert.Equal(TimeSpan.FromSeconds(0.5), await heard.Task.WaitAsync(Wait));
        Assert.False(sink.Text.Task.IsCompleted);
    }
}
