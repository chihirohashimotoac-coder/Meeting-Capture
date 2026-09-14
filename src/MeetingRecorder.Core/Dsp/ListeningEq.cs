namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// One fixed high-shelf, applied to the microphone leg of the meeting mix and
/// to nothing else.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> A microphone that has the bandwidth but sounds
/// dull next to the PC-audio leg in the same file is a spectral tilt, and a
/// spectral tilt is the one thing a fixed filter can honestly correct. This is
/// that filter. It is off by default, because turning it on without first
/// measuring would be guessing:
/// <see cref="Audio.SpectralBalance"/> runs over both working-audio files after
/// every recording and writes the measurement to the log, and that measurement
/// is what the setting is for.
/// </para>
/// <para><b>What it is not.</b> It has no threshold, no detector, no envelope
/// and no memory of how loud anything was. It is a two-pole filter with
/// constant coefficients, so it satisfies
/// <c>y = h * x</c> with one fixed <c>h</c>: superposition holds, doubling the
/// input doubles the output, and the amplitude relationship between a whisper
/// and a shout is exactly what it was. Everything ruled out - a gate, an AGC, a
/// compressor, a limiter, saturation, an exciter, a dynamic EQ, noise
/// suppression - is ruled out precisely because it is not that.
/// </para>
/// <para><b>Where it is applied.</b> Only to the microphone samples on their way
/// into the meeting mix. The 16 kHz working audio is written before this
/// runs, so what the recognizer hears is untouched - an equalizer is for a
/// human ear, and re-shaping a spectrum whisper.cpp was trained on the raw form
/// of has no reason to help it. The PC-audio leg never passes through one at
/// all: it is not the leg with the problem.
/// </para>
/// <para><b>Why it attenuates instead of boosting.</b> The shelf is built with a
/// positive gain and then scaled by <c>1 / sum|h[n]|</c>, the L1 norm of its own
/// impulse response, measured at construction. That makes the whole filter
/// non-expansive - <c>max|y| &lt;= max|x|</c> for any input at all, not merely
/// for a sine - so the mixer's guarantee that two full-scale legs sum to exactly
/// full scale survives intact and there is still nothing anywhere that can
/// clip. What comes out is the same tilt heard from the other end: the low-mids
/// come down rather than the highs going up. The level that costs is handed
/// back once, at the end, by the peak normalizer - which is where every other
/// gain decision in this application is made.
/// </para>
/// </remarks>
public sealed class ListeningEq
{
    /// <summary>Samples of impulse response summed when measuring the L1 norm.</summary>
    /// <remarks>
    /// A second-order shelf at any usable corner frequency has decayed to
    /// nothing long before this; 8192 is simply well past "long before".
    /// </remarks>
    private const int ImpulseLength = 8192;

    private readonly Biquad _shelf;
    private readonly float _scale;

    public ListeningEq(int sampleRate, double cornerHz, double gainDb)
    {
        SampleRate = sampleRate;
        CornerHz = cornerHz;
        GainDb = gainDb;

        _shelf = Biquad.HighShelf(sampleRate, cornerHz, gainDb);
        var norm = MeasureL1Norm(Biquad.HighShelf(sampleRate, cornerHz, gainDb));
        Scale = norm > 1.0 ? 1.0 / norm : 1.0;
        _scale = (float)Scale;
    }

    public int SampleRate { get; }

    public double CornerHz { get; }

    /// <summary>Shelf gain before normalization, in dB. The tilt, not the level.</summary>
    public double GainDb { get; }

    /// <summary>The constant the normalized filter is scaled by. Never above 1.</summary>
    public double Scale { get; }

    /// <summary>Filters a block of mono samples in place.</summary>
    public void Process(Span<float> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = _shelf.ProcessSample(buffer[i]) * _scale;
        }
    }

    public void Reset() => _shelf.Reset();

    /// <summary>
    /// Sum of the absolute impulse response. This is the exact worst-case gain
    /// of an LTI filter over all bounded inputs, which is why it - and not the
    /// peak of the magnitude response - is what the normalization uses.
    /// </summary>
    private static double MeasureL1Norm(Biquad filter)
    {
        double sum = 0.0;
        sum += Math.Abs(filter.ProcessSample(1f));
        for (var i = 1; i < ImpulseLength; i++)
        {
            sum += Math.Abs(filter.ProcessSample(0f));
        }

        return sum;
    }
}
