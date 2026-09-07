namespace MeetingRecorder.Core.Dsp;

/// <summary>Small, allocation-free helpers shared by the whole DSP chain.</summary>
public static class AudioMath
{
    /// <summary>Value substituted for log(0) so meters never return -infinity.</summary>
    public const double MinDb = -120.0;

    public static double LinearToDb(double linear)
        => linear <= 1e-12 ? MinDb : 20.0 * Math.Log10(linear);

    public static double DbToLinear(double db) => Math.Pow(10.0, db / 20.0);

    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0.0;
        }

        double sum = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            double s = samples[i];
            sum += s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    public static double RmsDb(ReadOnlySpan<float> samples) => LinearToDb(Rms(samples));

    public static double Peak(ReadOnlySpan<float> samples)
    {
        double peak = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var a = Math.Abs((double)samples[i]);
            if (a > peak)
            {
                peak = a;
            }
        }

        return peak;
    }

    public static double PeakDb(ReadOnlySpan<float> samples) => LinearToDb(Peak(samples));

    /// <summary>
    /// One-pole smoothing coefficient for the given time constant.
    /// <c>y += (x - y) * (1 - coef)</c> reaches ~63% of a step in <paramref name="ms"/>.
    /// </summary>
    public static double TimeConstantCoefficient(double ms, int sampleRate)
    {
        if (ms <= 0.0 || sampleRate <= 0)
        {
            return 0.0;
        }

        return Math.Exp(-1.0 / (sampleRate * (ms / 1000.0)));
    }

    public static float Clamp(float value, float min, float max)
        => value < min ? min : value > max ? max : value;

    /// <summary>Converts interleaved multi-channel audio to mono by averaging.</summary>
    public static int DownmixToMono(ReadOnlySpan<float> interleaved, int channels, Span<float> destination)
    {
        if (channels <= 1)
        {
            interleaved.CopyTo(destination);
            return interleaved.Length;
        }

        var frames = interleaved.Length / channels;
        if (destination.Length < frames)
        {
            throw new ArgumentException("Destination is too small for the mono result.", nameof(destination));
        }

        for (var f = 0; f < frames; f++)
        {
            double sum = 0.0;
            var baseIndex = f * channels;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[baseIndex + c];
            }

            destination[f] = (float)(sum / channels);
        }

        return frames;
    }
}
