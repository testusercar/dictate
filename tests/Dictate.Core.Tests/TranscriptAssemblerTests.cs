using Dictate.Core.Transcription;
using Xunit;

namespace Dictate.Core.Tests;

public class TranscriptAssemblerTests
{
    private readonly TranscriptAssembler _a = new();

    [Fact]
    public void Created_marks_ready()
    {
        _a.Apply("""{"type":"transcript.created"}""");
        Assert.True(_a.IsReady);
    }

    [Fact]
    public void Interim_partials_replace_each_other()
    {
        _a.Apply("""{"type":"transcript.partial","text":"hello","is_final":false,"speech_final":false}""");
        _a.Apply("""{"type":"transcript.partial","text":"hello there","is_final":false,"speech_final":false}""");
        Assert.Equal("hello there", _a.CurrentText);
    }

    [Fact]
    public void Speech_final_commits_and_the_next_utterance_appends()
    {
        _a.Apply("""{"type":"transcript.partial","text":"Hello there.","is_final":true,"speech_final":true}""");
        _a.Apply("""{"type":"transcript.partial","text":"How are","is_final":false,"speech_final":false}""");
        Assert.Equal("Hello there. How are", _a.CurrentText);
    }

    [Fact]
    public void Chunk_final_without_speech_final_stays_pending()
    {
        _a.Apply("""{"type":"transcript.partial","text":"one two","is_final":true,"speech_final":false}""");
        _a.Apply("""{"type":"transcript.partial","text":"one two three","is_final":true,"speech_final":true}""");
        Assert.Equal("one two three", _a.CurrentText);
    }

    [Fact]
    public void Done_carries_the_last_utterance()
    {
        _a.Apply("""{"type":"transcript.partial","text":"First.","is_final":true,"speech_final":true}""");
        _a.Apply("""{"type":"transcript.partial","text":"Second","is_final":false,"speech_final":false}""");
        _a.Apply("""{"type":"transcript.done","text":"Second one."}""");
        Assert.True(_a.IsDone);
        Assert.Equal("First. Second one.", _a.CurrentText);
    }

    [Fact]
    public void Done_restating_the_whole_session_is_not_doubled()
    {
        _a.Apply("""{"type":"transcript.partial","text":"First.","is_final":true,"speech_final":true}""");
        _a.Apply("""{"type":"transcript.done","text":"First. Second."}""");
        Assert.Equal("First. Second.", _a.CurrentText);
    }

    [Fact]
    public void Empty_done_promotes_pending_text()
    {
        _a.Apply("""{"type":"transcript.partial","text":"almost","is_final":false,"speech_final":false}""");
        _a.Apply("""{"type":"transcript.done","text":""}""");
        Assert.Equal("almost", _a.CurrentText);
    }

    [Fact]
    public void Error_is_captured()
    {
        _a.Apply("""{"type":"error","message":"invalid api key"}""");
        Assert.Equal("invalid api key", _a.Error);
    }

    [Fact]
    public void Unknown_events_are_ignored()
    {
        Assert.False(_a.Apply("""{"type":"something.new","text":"x"}"""));
        Assert.Equal("", _a.CurrentText);
    }

    [Fact]
    public void Query_string_carries_session_settings()
    {
        var t = new XaiStreamingTranscriber(new XaiSttOptions { ApiKey = "k", Language = "en" });
        var uri = t.BuildUri();
        Assert.Equal("wss://api.x.ai/v1/stt?sample_rate=16000&encoding=pcm&language=en&model=grok-voice-transcribe-2.0&interim_results=true", uri.ToString());
    }
}
