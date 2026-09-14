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

    /// <summary>
    /// Second order band-pass with unity gain at the centre frequency (the
    /// "constant 0 dB peak gain" form of the RBJ cookbook).
    /// </summary>
    /// <remarks>
    /// Used only for measurement - <see cref="Audio.SpectralBalance"/> puts two
    /// of these in series per band to get skirts steep enough that the bands
    /// mean something. Nothing in the recording path passes through one.
    /// </remarks>
    public static Biquad BandPass(double sampleRate, double centreHz, double q)
    {
        var filter = new Biquad();
        centreHz = Math.Clamp(centreHz, 1.0, sampleRate * 0.49);
        q = Math.Max(0.05, q);

        var w0 = 2.0 * Math.PI * centreHz / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2.0 * q);

        var b0 = alpha;
        const double B1 = 0.0;
        var b2 = -alpha;
        var a0 = 1.0 + alpha;
        var a1 = -2.0 * cos;
        var a2 = 1.0 - alpha;

        filter._b0 = b0 / a0;
        filter._b1 = B1 / a0;
        filter._b2 = b2 / a0;
        filter._a1 = a1 / a0;
        filter._a2 = a2 / a0;
        return filter;
    }

    /// <summary>
    /// Second order high shelf: unity below <paramref name="cornerHz"/>,
    /// <paramref name="gainDb"/> above it, from the RBJ cookbook.
    /// </summary>
    /// <remarks>
    /// Linear and time invariant, like everything else that is allowed near
    /// this audio: one fixed set of coefficients, applied identically to every
    /// sample of the file. It changes the spectrum, which is the point, and it
    /// changes no amplitude relationship over time, which is the rule.
    /// </remarks>
    public static Biquad HighShelf(double sampleRate, double cornerHz, double gainDb, double slope = 1.0)
    {
        var filter = new Biquad();
        cornerHz = Math.Clamp(cornerHz, 20.0, sampleRate * 0.49);
        slope = Math.Clamp(slope, 0.1, 2.0);

        var a = Math.Pow(10.0, gainDb / 40.0);
        var w0 = 2.0 * Math.PI * cornerHz / sampleRate;
        var cos = Math.Cos(w0);
        var sin = Math.Sin(w0);
        var alpha = sin / 2.0 * Math.Sqrt(((a + (1.0 / a)) * ((1.0 / slope) - 1.0)) + 2.0);
        var twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

        var b0 = a * ((a + 1.0) + ((a - 1.0) * cos) + twoSqrtAAlpha);
        var b1 = -2.0 * a * ((a - 1.0) + ((a + 1.0) * cos));
        var b2 = a * ((a + 1.0) + ((a - 1.0) * cos) - twoSqrtAAlpha);
        var a0 = (a + 1.0) - ((a - 1.0) * cos) + twoSqrtAAlpha;
        var a1 = 2.0 * ((a - 1.0) - ((a + 1.0) * cos));
        var a2 = (a + 1.0) - ((a - 1.0) * cos) - twoSqrtAAlpha;

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
