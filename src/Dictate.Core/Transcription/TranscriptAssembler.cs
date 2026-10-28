using System.Text.Json;

namespace Dictate.Core.Transcription;

/// <summary>
/// Builds one dictation's text from xAI STT WebSocket events.
/// <list type="bullet">
/// <item><c>transcript.partial</c> with <c>speech_final=true</c> restates a whole utterance: it is committed.</item>
/// <item>Other partials are the utterance in progress: the latest one is kept as pending text.</item>
/// <item><c>transcript.done</c> closes the session and carries the last utterance.</item>
/// </list>
/// Not thread-safe; feed it from one receive loop.
/// </summary>
public sealed class TranscriptAssembler
{
    private readonly List<string> _utterances = new();
    private string _pending = "";

    public bool IsReady { get; private set; }
    public bool IsDone { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Everything heard so far, committed plus in progress.</summary>
    public string CurrentText => Join(_pending.Length > 0 ? _utterances.Append(_pending) : _utterances);

    /// <summary>Applies one server message. Returns true when <see cref="CurrentText"/> changed.</summary>
    public bool Apply(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        var text = root.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";

        switch (type)
        {
            case "transcript.created":
                IsReady = true;
                return false;

            case "transcript.partial":
                if (text.Length == 0) return false;
                if (Flag(root, "is_final") && Flag(root, "speech_final"))
                {
                    _utterances.Add(text.Trim());
                    _pending = "";
                }
                else
                {
                    _pending = text.Trim();
                }
                return true;

            case "transcript.done":
                IsDone = true;
                if (text.Length == 0)
                {
                    Commit();
                    return false;
                }
                var done = text.Trim();
                var committed = Join(_utterances);
                // Some servers restate the whole session here; don't double it.
                if (committed.Length > 0 && done.StartsWith(committed, StringComparison.Ordinal))
                    _utterances.Clear();
                _utterances.Add(done);
                _pending = "";
                return true;

            case "error":
                Error = root.TryGetProperty("message", out var m) ? m.ToString() : json;
                return false;

            default:
                return false;
        }
    }

    /// <summary>Promotes any in-progress text to final (used when the server closes without a done event).</summary>
    public void Commit()
    {
        if (_pending.Length > 0) _utterances.Add(_pending);
        _pending = "";
    }

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Join(IEnumerable<string> parts) =>
        string.Join(' ', parts.Where(p => p.Length > 0));
}
