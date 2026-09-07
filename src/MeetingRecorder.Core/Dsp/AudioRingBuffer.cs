namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Bounded single-producer / single-consumer ring buffer for mono float audio.
/// </summary>
/// <remarks>
/// The capture callback is the producer and must never block: WASAPI will drop
/// the whole packet if the callback overruns its deadline. The buffer therefore
/// uses a very short lock (a memcpy) and, if the consumer has stalled long
/// enough to fill the entire buffer, discards the oldest audio and increments
/// <see cref="OverrunCount"/> instead of blocking. With the default 30 second
/// capacity that only happens if the disk stops responding for half a minute,
/// and it is surfaced to the user as a warning rather than hidden.
/// </remarks>
public sealed class AudioRingBuffer
{
    private readonly object _sync = new();
    private readonly float[] _buffer;
    private int _readIndex;
    private int _writeIndex;
    private int _count;

    public AudioRingBuffer(int capacitySamples)
    {
        if (capacitySamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        }

        _buffer = new float[capacitySamples];
    }

    public static AudioRingBuffer ForSeconds(int sampleRate, double seconds)
        => new((int)Math.Ceiling(sampleRate * seconds));

    public int Capacity => _buffer.Length;

    /// <summary>Samples currently buffered.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>Total samples ever accepted (excluding discarded overruns).</summary>
    public long TotalWritten { get; private set; }

    /// <summary>Total samples ever handed to the consumer (excluding silence padding).</summary>
    public long TotalRead { get; private set; }

    /// <summary>Number of times the buffer had to discard audio because the consumer stalled.</summary>
    public long OverrunCount { get; private set; }

    /// <summary>Total samples discarded by overruns.</summary>
    public long OverrunSamples { get; private set; }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            var incoming = samples;
            if (incoming.Length > _buffer.Length)
            {
                // Pathological block: keep the newest tail we can hold.
                OverrunCount++;
                OverrunSamples += incoming.Length - _buffer.Length;
                incoming = incoming[^_buffer.Length..];
            }

            var free = _buffer.Length - _count;
            if (incoming.Length > free)
            {
                var discard = incoming.Length - free;
                _readIndex = (_readIndex + discard) % _buffer.Length;
                _count -= discard;
                OverrunCount++;
                OverrunSamples += discard;
            }

            var first = Math.Min(incoming.Length, _buffer.Length - _writeIndex);
            incoming[..first].CopyTo(_buffer.AsSpan(_writeIndex));
            if (first < incoming.Length)
            {
                incoming[first..].CopyTo(_buffer.AsSpan(0));
            }

            _writeIndex = (_writeIndex + incoming.Length) % _buffer.Length;
            _count += incoming.Length;
            TotalWritten += incoming.Length;
        }
    }

    /// <summary>
    /// Reads up to <c>destination.Length</c> samples. Anything not available is
    /// left untouched; the caller decides whether to pad with silence.
    /// </summary>
    /// <returns>Number of samples actually read.</returns>
    public int Read(Span<float> destination)
    {
        lock (_sync)
        {
            var toRead = Math.Min(destination.Length, _count);
            if (toRead == 0)
            {
                return 0;
            }

            var first = Math.Min(toRead, _buffer.Length - _readIndex);
            _buffer.AsSpan(_readIndex, first).CopyTo(destination);
            if (first < toRead)
            {
                _buffer.AsSpan(0, toRead - first).CopyTo(destination[first..]);
            }

            _readIndex = (_readIndex + toRead) % _buffer.Length;
            _count -= toRead;
            TotalRead += toRead;
            return toRead;
        }
    }

    /// <summary>Discards the oldest <paramref name="samples"/> samples (drift correction).</summary>
    public int Discard(int samples)
    {
        lock (_sync)
        {
            var toDiscard = Math.Min(samples, _count);
            _readIndex = (_readIndex + toDiscard) % _buffer.Length;
            _count -= toDiscard;
            TotalRead += toDiscard;
            return toDiscard;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _readIndex = 0;
            _writeIndex = 0;
            _count = 0;
        }
    }
}
