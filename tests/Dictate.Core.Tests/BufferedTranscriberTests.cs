using Dictate.Core.Transcription;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dictate.Core.Tests;

public class BufferedTranscriberTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private sealed class FakeRecognizer : IBatchRecognizer
    {
        private int _calls;
        public float[] Last { get; private set; } = [];
        public int Calls => Volatile.Read(ref _calls);
        public Func<float[], string> Answer { get; init; } = s => $" heard {s.Length} ";

        public string Recognize(float[] samples, int sampleRate)
        {
            Interlocked.Increment(ref _calls);
            Last = samples;
            return Answer(samples);
        }
    }

    private static byte[] Silence(TimeSpan duration, int sampleRate = 16000) =>
        new byte[(int)(duration.TotalSeconds * sampleRate) * 2];

    [Fact]
    public async Task Recognizes_all_audio_on_finish_and_trims_the_text()
    {
        var r = new FakeRecognizer();
        await using var t = new BufferedTranscriber(r);
        await t.ConnectAsync(CancellationToken.None);
        await t.SendAudioAsync(Silence(TimeSpan.FromMilliseconds(300)), CancellationToken.None);
        await t.SendAudioAsync(Silence(TimeSpan.FromMilliseconds(200)), CancellationToken.None);

        Assert.Equal("heard 8000", await t.FinishAsync(CancellationToken.None));
        Assert.Equal(1, r.Calls);
    }

    [Fact]
    public async Task Converts_pcm16_to_floats_across_split_buffers()
    {
        var r = new FakeRecognizer();
        await using var t = new BufferedTranscriber(r, sampleRate: 4);
        // 0x4000 = 0.5, 0xC000 = -0.5, 0x7FFF ≈ 1; the second sample is split between buffers.
        await t.SendAudioAsync(new byte[] { 0x00, 0x40, 0x00 }, CancellationToken.None);
        await t.SendAudioAsync(new byte[] { 0xC0, 0xFF, 0x7F }, CancellationToken.None);
        await t.FinishAsync(CancellationToken.None);

        Assert.Equal(new[] { 0.5f, -0.5f, 32767 / 32768f }, r.Last);
    }

    [Fact]
    public async Task A_stray_tap_is_not_sent_to_the_recognizer()
    {
        var r = new FakeRecognizer();
        await using var t = new BufferedTranscriber(r);
        await t.SendAudioAsync(Silence(TimeSpan.FromMilliseconds(100)), CancellationToken.None);

        Assert.Equal("", await t.FinishAsync(CancellationToken.None));
        Assert.Equal(0, r.Calls);
    }

    [Fact]
    public async Task Previews_the_audio_so_far_at_the_interval()
    {
        var time = new FakeTimeProvider();
        var r = new FakeRecognizer();
        await using var t = new BufferedTranscriber(r, previewInterval: TimeSpan.FromSeconds(1), time: time);
        var preview = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.TextChanged += s => preview.TrySetResult(s);

        await t.SendAudioAsync(Silence(TimeSpan.FromSeconds(1)), CancellationToken.None);
        Assert.Equal(0, r.Calls); // interval not reached yet

        time.Advance(TimeSpan.FromSeconds(1));
        await t.SendAudioAsync(Silence(TimeSpan.FromSeconds(1)), CancellationToken.None);
        Assert.Equal("heard 32000", await preview.Task.WaitAsync(Wait));

        Assert.Equal("heard 32000", await t.FinishAsync(CancellationToken.None));
        Assert.Equal(2, r.Calls);
    }

    [Fact]
    public async Task No_preview_without_an_interval()
    {
        var time = new FakeTimeProvider();
        var r = new FakeRecognizer();
        await using var t = new BufferedTranscriber(r, time: time);
        time.Advance(TimeSpan.FromMinutes(1));
        await t.SendAudioAsync(Silence(TimeSpan.FromSeconds(2)), CancellationToken.None);
        Assert.Equal(0, r.Calls);
    }
}
