namespace Dictate.Core.Audio;

/// <summary>
/// Keeps the most recent audio while no dictation is running, so the speech that happens
/// before Windows reports a hold (300 ms) is not lost. Thread-safe.
/// </summary>
public sealed class PreRollBuffer
{
    private readonly object _gate = new();
    private readonly byte[] _ring;
    private int _start;
    private int _count;

    public PreRollBuffer(int capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        _ring = new byte[capacityBytes];
    }

    /// <summary>Capacity for <paramref name="duration"/> of 16-bit mono PCM at <paramref name="sampleRate"/>.</summary>
    public static PreRollBuffer ForDuration(TimeSpan duration, int sampleRate)
    {
        var bytes = (int)(duration.TotalSeconds * sampleRate) * 2;
        return new PreRollBuffer(Math.Max(2, bytes));
    }

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (data.Length >= _ring.Length)
            {
                data[^_ring.Length..].CopyTo(_ring);
                _start = 0;
                _count = _ring.Length;
                return;
            }

            var end = (_start + _count) % _ring.Length;
            var first = Math.Min(data.Length, _ring.Length - end);
            data[..first].CopyTo(_ring.AsSpan(end));
            data[first..].CopyTo(_ring);

            var overflow = _count + data.Length - _ring.Length;
            if (overflow > 0)
            {
                _start = (_start + overflow) % _ring.Length;
                _count = _ring.Length;
            }
            else
            {
                _count += data.Length;
            }
        }
    }

    /// <summary>Returns the buffered audio, oldest first, and empties the buffer.</summary>
    public byte[] Drain()
    {
        lock (_gate)
        {
            var result = new byte[_count];
            var first = Math.Min(_count, _ring.Length - _start);
            _ring.AsSpan(_start, first).CopyTo(result);
            _ring.AsSpan(0, _count - first).CopyTo(result.AsSpan(first));
            _start = 0;
            _count = 0;
            return result;
        }
    }
}
