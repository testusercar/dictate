using System.Net.WebSockets;
using System.Text;

namespace Dictate.Core.Transcription;

public sealed record XaiSttOptions
{
    public required string ApiKey { get; init; }
    public Uri Endpoint { get; init; } = new("wss://api.x.ai/v1/stt");
    public string Model { get; init; } = "grok-voice-transcribe-2.0";
    /// <summary>Two-letter code; enables inverse text normalization (numbers, dates) on the server.</summary>
    public string Language { get; init; } = "en";
    public int SampleRate { get; init; } = 16000;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan FinishTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// xAI Grok streaming speech-to-text over WebSocket (wss://api.x.ai/v1/stt).
/// Session settings go in the query string, audio goes up as binary frames, and
/// <c>{"type":"audio.done"}</c> ends the session.
/// </summary>
public sealed class XaiStreamingTranscriber : ITranscriber
{
    private readonly XaiSttOptions _options;
    private readonly ClientWebSocket _socket = new();
    private readonly TranscriptAssembler _assembler = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveLoop;

    public XaiStreamingTranscriber(XaiSttOptions options)
    {
        _options = options;
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {options.ApiKey}");
        _socket.Options.SetRequestHeader("User-Agent", "Dictate/0.1 (Windows)");
    }

    public event Action<string>? TextChanged;

    public Uri BuildUri()
    {
        var query = string.Join('&', new[]
        {
            $"sample_rate={_options.SampleRate}",
            "encoding=pcm",
            $"language={Uri.EscapeDataString(_options.Language)}",
            $"model={Uri.EscapeDataString(_options.Model)}",
            "interim_results=true",
        });
        return new UriBuilder(_options.Endpoint) { Query = query }.Uri;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        timeout.CancelAfter(_options.ConnectTimeout);
        try
        {
            await _socket.ConnectAsync(BuildUri(), timeout.Token).ConfigureAwait(false);
        }
        catch (WebSocketException e)
        {
            throw new TranscriptionException(Describe(e), e);
        }

        _receiveLoop = Task.Run(ReceiveLoopAsync);
        await _ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        if (pcm.IsEmpty) return;
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(pcm, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string> FinishAsync(CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var done = Encoding.UTF8.GetBytes("""{"type":"audio.done"}""");
            await _socket.SendAsync(new ReadOnlyMemory<byte>(done), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.FinishTimeout);
        await _done.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        return _assembler.CurrentText;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !_life.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), _life.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                if (result.MessageType != WebSocketMessageType.Text) continue;

                var changed = _assembler.Apply(json);
                if (_assembler.IsReady) _ready.TrySetResult();
                if (_assembler.Error is { } error)
                {
                    var ex = new TranscriptionException($"Grok transcription error: {error}");
                    _ready.TrySetException(ex);
                    _done.TrySetException(ex);
                    return;
                }
                if (changed) TextChanged?.Invoke(_assembler.CurrentText);
                if (_assembler.IsDone) break;
            }

            _assembler.Commit();
            _ready.TrySetException(new TranscriptionException("Grok closed the connection before it was ready."));
            _done.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            _ready.TrySetCanceled();
            _done.TrySetCanceled();
        }
        catch (Exception e)
        {
            var ex = new TranscriptionException($"Lost connection to Grok: {e.Message}", e);
            _ready.TrySetException(ex);
            _done.TrySetException(ex);
        }
    }

    private static string Describe(WebSocketException e) =>
        e.Message.Contains("401") || e.Message.Contains("403")
            ? "Grok rejected the API key. Check it in Dictate's settings."
            : $"Couldn't reach Grok: {e.Message}";

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, close.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort; the socket is being thrown away.
        }
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { }
        }
        _socket.Dispose();
        _life.Dispose();
        _sendLock.Dispose();
    }
}
