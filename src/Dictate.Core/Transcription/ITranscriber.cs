namespace Dictate.Core.Transcription;

/// <summary>One dictation's worth of streaming transcription.</summary>
public interface ITranscriber : IAsyncDisposable
{
    /// <summary>Raised with the full text heard so far whenever it changes. Any thread.</summary>
    event Action<string>? TextChanged;

    Task ConnectAsync(CancellationToken ct);

    /// <summary>Sends 16-bit little-endian mono PCM.</summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);

    /// <summary>Signals end of audio and waits for the final transcript.</summary>
    Task<string> FinishAsync(CancellationToken ct);
}

public sealed class TranscriptionException(string message, Exception? inner = null) : Exception(message, inner);
