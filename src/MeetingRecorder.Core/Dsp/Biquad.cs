namespace MeetingRecorder.Core.Dsp;

/// <summary>Transposed direct form II biquad. Used for anti-alias filtering.</summary>
public sealed class Biquad
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    private Biquad()
    {
    }

    /// <summary>Second order Butterworth low-pass (Q = 1/sqrt(2)).</summary>
    public static Biquad LowPass(double sampleRate, double cutoffHz, double q = 0.70710678)
    {
        var filter = new Biquad();
        cutoffHz = Math.Clamp(cutoffHz, 20.0, sampleRate * 0.49);

        var w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2.0 * q);

        var b0 = (1.0 - cos) / 2.0;
        var b1 = 1.0 - cos;
        var b2 = (1.0 - cos) / 2.0;
        var a0 = 1.0 + alpha;
        var a1 = -2.0 * cos;
        var a2 = 1.0 - alpha;

        filter._b0 = b0 / a0;
        filter._b1 = b1 / a0;
        filter._b2 = b2 / a0;
        filter._a1 = a1 / a0;
        filter._a2 = a2 / a0;
        return filter;
    }

    public float ProcessSample(float x)
    {
        var y = (_b0 * x) + _z1;
        _z1 = (_b1 * x) - (_a1 * y) + _z2;
        _z2 = (_b2 * x) - (_a2 * y);
        return (float)y;
    }

    public void Process(Span<float> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = ProcessSample(buffer[i]);
        }
    }

    public void Reset()
    {
        _z1 = 0.0;
        _z2 = 0.0;
    }
}
