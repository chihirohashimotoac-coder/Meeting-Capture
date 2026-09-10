namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// First order high-pass that removes the DC offset some USB microphones and
/// virtual endpoints produce.
/// </summary>
/// <remarks>
/// This is the only filter left in the recording path, and it is here because a
/// DC offset is not free: it consumes head-room, which directly reduces the
/// constant gain <see cref="Audio.PeakNormalizer"/> is allowed to apply, and it
/// displaces every peak measurement taken from the file. At the default 20 Hz
/// the -3 dB point sits an octave below the lowest fundamental of an adult
/// voice (~80 Hz), so speech passes through it essentially unchanged.
/// </remarks>
public sealed class DcBlocker
{
    private readonly double _r;
    private double _x1;
    private double _y1;

    /// <param name="cutoffHz">-3 dB point. 20 Hz is below anything speech carries.</param>
    public DcBlocker(int sampleRate, double cutoffHz = 20.0)
    {
        // Standard DC-blocker: y[n] = x[n] - x[n-1] + R * y[n-1]
        _r = 1.0 - (2.0 * Math.PI * cutoffHz / sampleRate);
        if (_r < 0.0)
        {
            _r = 0.0;
        }
    }

    public void Process(Span<float> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            double x = buffer[i];
            var y = x - _x1 + (_r * _y1);
            _x1 = x;
            _y1 = y;
            buffer[i] = (float)y;
        }
    }

    public void Reset()
    {
        _x1 = 0.0;
        _y1 = 0.0;
    }
}
