namespace MeetingRecorder.Core.Tests.TestSupport;

/// <summary>Deterministic synthetic audio used as test fixtures.</summary>
/// <remarks>
/// Everything here is generated, not recorded, so the fixtures are byte-for-byte
/// reproducible on every runner and no audio file has to be committed.
/// </remarks>
public static class SignalGenerator
{
    public static float[] Sine(double frequencyHz, double seconds, int sampleRate, double amplitude = 0.5, double phase = 0)
    {
        var samples = new float[(int)(seconds * sampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin((2.0 * Math.PI * frequencyHz * i / sampleRate) + phase));
        }

        return samples;
    }

    public static float[] Silence(double seconds, int sampleRate) => new float[(int)(seconds * sampleRate)];

    public static float[] Noise(double seconds, int sampleRate, double amplitude, int seed = 12345)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * sampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2.0 - 1.0) * amplitude);
        }

        return samples;
    }

    /// <summary>A crude voiced sound: a fundamental plus harmonics shaped by two formants.</summary>
    public static float[] Voice(double fundamentalHz, double formant1, double formant2, double seconds, int sampleRate, double amplitude = 0.4, int seed = 7)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * sampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)sampleRate;
            double value = 0;
            for (var harmonic = 1; harmonic <= 24; harmonic++)
            {
                var frequency = fundamentalHz * harmonic;
                if (frequency > sampleRate / 2.0)
                {
                    break;
                }

                // Simple two-pole formant envelope.
                var gain = 1.0 / (1.0 + Math.Pow((frequency - formant1) / 180.0, 2))
                           + 0.7 / (1.0 + Math.Pow((frequency - formant2) / 220.0, 2));
                value += gain * Math.Sin(2.0 * Math.PI * frequency * t);
            }

            // A little breath noise keeps the cepstra from being degenerate.
            value += (random.NextDouble() - 0.5) * 0.02;
            samples[i] = (float)(value * amplitude / 2.0);
        }

        return samples;
    }

    public static float[] Concat(params float[][] parts)
    {
        var result = new float[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
