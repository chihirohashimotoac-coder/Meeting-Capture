namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Streaming sample-rate converter for mono float audio.
/// </summary>
/// <remarks>
/// <para>
/// Two conversions matter in this application: whatever the capture endpoint
/// runs at (44.1 kHz headsets are common, and a headset microphone configured
/// for "communications" is frequently 16 kHz) to the 48 kHz internal rate, and
/// 48 kHz to the 16 kHz that whisper.cpp consumes.
/// </para>
/// <para>
/// Down-conversion runs a cascaded second order Butterworth low-pass at
/// 0.45 x the destination rate first. Skipping it would fold everything above
/// Nyquist back into the speech band, which is audible as a metallic
/// "underwater" artefact and measurably hurts recognition. Up-conversion needs
/// no pre-filter; the interpolation itself is the (mild) low-pass.
/// </para>
/// <para><b>Why the interpolation is cubic and not linear.</b> It used to be
/// linear, on the argument that the residual error sits far below a laptop
/// microphone's noise floor. That argument is about the error <i>floor</i> and
/// misses the error that matters here, which is the pass-band droop: linear
/// interpolation is a triangular kernel, so its magnitude response is
/// sinc²(f / f_source) and it takes the top of the speech band with it. Measured
/// against a swept sine (the numbers are asserted in
/// <c>ResamplerFrequencyResponseTests</c>):
/// </para>
/// <list type="table">
/// <listheader><term>conversion</term><description>linear vs Catmull-Rom</description></listheader>
/// <item><term>44.1 → 48 kHz</term><description>4 kHz: -0.24 vs -0.01 dB; 8 kHz: -0.95 vs -0.16 dB; 14 kHz: -2.98 vs -1.30 dB</description></item>
/// <item><term>16 → 48 kHz</term><description>4 kHz: -1.63 vs -0.54 dB; 6 kHz: -3.77 vs -2.31 dB; 7 kHz: -5.25 vs -3.91 dB</description></item>
/// </list>
/// <para>
/// A dB at 8 kHz on the consonants is exactly the "muffled" a listener reports,
/// and it is spent for nothing: Catmull-Rom is four multiply-adds per output
/// sample instead of two, holds three samples of history instead of one, and
/// allocates nothing. That is far below the noise on a 15 W mobile CPU, and it
/// is emphatically not a polyphase sinc - the capture callback must stay cheap,
/// so a windowed-sinc kernel with a per-sample loop over dozens of taps is
/// still rejected.
/// </para>
/// <para>
/// The system-audio leg normally never reaches any of this: a Windows render
/// endpoint runs at 48 kHz, so <see cref="IsPassThrough"/> is true and the
/// samples are copied. The microphone leg is the one that pays, which is why
/// the difference was audible on one stream and not the other.
/// </para>
/// <para>
/// The cubic is not free of consequences. Unlike linear interpolation it can
/// overshoot the samples it is interpolating between - see
/// <see cref="MaximumInterpolationGain"/> for by how much, and for where the
/// head-room to absorb it comes from.
/// </para>
/// </remarks>
public sealed class Resampler
{
    /// <summary>
    /// Largest factor the interpolation can multiply a bounded input by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly 5/4, and not a rounding artefact: Catmull-Rom interpolates its
    /// control points but is not bounded by them. Its four basis weights sum to
    /// one at every position - so a constant stays constant - but two of them go
    /// negative between the sample instants, and the sum of their magnitudes,
    /// which is the worst-case gain over every bounded input, peaks at 1.25
    /// exactly half way between two samples. The input that reaches it is not
    /// exotic: <c>[-1, +1, +1, -1]</c> - a full-scale signal turning around near
    /// Nyquist - comes out at 1.25.
    /// </para>
    /// <para>
    /// Linear interpolation had no such property, because a weighted average of
    /// two samples cannot leave their range. Replacing it therefore put an
    /// overshoot into a path that did not have one, and the head-room for it has
    /// to come from somewhere. It comes from
    /// <see cref="Models.AudioProcessingSettings.MixGainPerStreamDb"/>, which is
    /// derived from this constant - not from clamping the interpolator. A clamp
    /// would be a signal-dependent nonlinearity in the one path this application
    /// promises is linear, whereas the head-room costs nothing permanent: the
    /// peak normalizer hands it back, as one constant, once the file is closed.
    /// </para>
    /// <para>
    /// <c>ResamplerHeadroomTests</c> measures this rather than taking it on
    /// trust - it sweeps the interpolation position, sums the absolute basis
    /// weights, and asserts both the peak and that the weights still sum to one.
    /// </para>
    /// </remarks>
    public const double MaximumInterpolationGain = 1.25;

    private readonly Biquad[] _antiAlias;
    private readonly double _ratio;

    // Catmull-Rom needs the two samples before and the one after the interval
    // it is interpolating inside. _x1 -> _x2 is that interval.
    private float _x0;
    private float _x1;
    private float _x2;
    private float _x3;
    private bool _primed;
    private double _position;

    public Resampler(int sourceRate, int targetRate)
    {
        if (sourceRate <= 0 || targetRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRate), "Sample rates must be positive.");
        }

        SourceRate = sourceRate;
        TargetRate = targetRate;
        _ratio = (double)sourceRate / targetRate;

        if (targetRate < sourceRate)
        {
            var cutoff = targetRate * 0.45;
            _antiAlias = new[]
            {
                Biquad.LowPass(sourceRate, cutoff),
                Biquad.LowPass(sourceRate, cutoff),
            };
        }
        else
        {
            _antiAlias = Array.Empty<Biquad>();
        }
    }

    public int SourceRate { get; }

    public int TargetRate { get; }

    public bool IsPassThrough => SourceRate == TargetRate;

    /// <summary>Conservative upper bound on the output length for a given input length.</summary>
    public int EstimateOutputLength(int inputLength)
        => IsPassThrough ? inputLength : (int)Math.Ceiling((inputLength / _ratio) + 2);

    /// <summary>
    /// Converts one block. Internal state carries across calls, so a continuous
    /// stream may be fed in arbitrary block sizes without clicks at the seams.
    /// </summary>
    /// <returns>Number of samples written to <paramref name="destination"/>.</returns>
    public int Process(ReadOnlySpan<float> source, Span<float> destination)
    {
        if (IsPassThrough)
        {
            source.CopyTo(destination);
            return source.Length;
        }

        // Anti-alias filtering has to happen on a copy: the caller's buffer may
        // still be needed at its original rate (e.g. for the level meter).
        Span<float> filtered = source.Length <= 4096 ? stackalloc float[source.Length] : new float[source.Length];
        source.CopyTo(filtered);
        foreach (var stage in _antiAlias)
        {
            stage.Process(filtered);
        }

        var written = 0;
        for (var i = 0; i < filtered.Length; i++)
        {
            var current = filtered[i];

            if (!_primed)
            {
                // Seed the whole history with the first sample rather than
                // waiting for three more. Waiting would swallow three samples at
                // the head of the stream, and the pump counts samples: an
                // interpolator is not allowed to change how much audio comes out
                // of a given amount of audio going in.
                _x0 = _x1 = _x2 = _x3 = current;
                _primed = true;
                _position = 0.0;
            }
            else
            {
                _x0 = _x1;
                _x1 = _x2;
                _x2 = _x3;
                _x3 = current;
            }

            // Emit every output sample that falls between _x1 and _x2. That is
            // one input sample behind the newest sample read, which is what buys
            // the cubic its extra accuracy - and costs 1 input sample of group
            // delay (21 us at 48 kHz).
            while (_position < 1.0)
            {
                if (written >= destination.Length)
                {
                    throw new ArgumentException(
                        "Destination buffer is too small; use EstimateOutputLength.", nameof(destination));
                }

                destination[written++] = CatmullRom(_x0, _x1, _x2, _x3, (float)_position);
                _position += _ratio;
            }

            _position -= 1.0;
        }

        return written;
    }

    /// <summary>
    /// Catmull-Rom (cubic Hermite with the standard tangent estimate) between
    /// <paramref name="x1"/> and <paramref name="x2"/>.
    /// </summary>
    /// <remarks>
    /// It interpolates its control points exactly, so <c>t == 0</c> returns
    /// <paramref name="x1"/> and a rate ratio of exactly 1 would return the
    /// input untouched. Horner form: three multiplies and three adds after the
    /// coefficients, which themselves are shifts and adds of the four samples.
    /// </remarks>
    private static float CatmullRom(float x0, float x1, float x2, float x3, float t)
    {
        var a0 = (-0.5f * x0) + (1.5f * x1) - (1.5f * x2) + (0.5f * x3);
        var a1 = x0 - (2.5f * x1) + (2.0f * x2) - (0.5f * x3);
        var a2 = (-0.5f * x0) + (0.5f * x2);
        return (((((a0 * t) + a1) * t) + a2) * t) + x1;
    }

    /// <summary>Convenience overload that allocates the destination.</summary>
    public float[] Process(ReadOnlySpan<float> source)
    {
        if (IsPassThrough)
        {
            return source.ToArray();
        }

        var buffer = new float[EstimateOutputLength(source.Length)];
        var written = Process(source, buffer);
        if (written == buffer.Length)
        {
            return buffer;
        }

        var exact = new float[written];
        Array.Copy(buffer, exact, written);
        return exact;
    }

    public void Reset()
    {
        foreach (var stage in _antiAlias)
        {
            stage.Reset();
        }

        _primed = false;
        _x0 = _x1 = _x2 = _x3 = 0f;
        _position = 0.0;
    }
}
