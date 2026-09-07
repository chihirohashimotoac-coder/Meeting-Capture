namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Streaming sample-rate converter for mono float audio.
/// </summary>
/// <remarks>
/// <para>
/// Two conversions matter in this application: whatever the capture endpoint
/// runs at (44.1 kHz headsets are common) to the 48 kHz internal rate, and
/// 48 kHz to the 16 kHz that whisper.cpp consumes.
/// </para>
/// <para>
/// Down-conversion runs a cascaded second order Butterworth low-pass at
/// 0.45 x the destination rate first. Skipping it would fold everything above
/// Nyquist back into the speech band, which is audible as a metallic
/// "underwater" artefact and measurably hurts recognition. Up-conversion needs
/// no pre-filter, the interpolation itself is the (mild) low-pass.
/// </para>
/// <para>
/// Interpolation is linear. For speech that is already band-limited by the
/// anti-alias stage, the residual error sits ~45 dB below the signal - far
/// below the noise floor of any laptop microphone - and it costs a fraction of
/// what a polyphase sinc would cost on a 15 W mobile CPU that also has to run
/// Whisper.
/// </para>
/// </remarks>
public sealed class Resampler
{
    private readonly Biquad[] _antiAlias;
    private readonly double _ratio;
    private float _previousSample;
    private bool _hasPrevious;
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
            if (!_hasPrevious)
            {
                _previousSample = current;
                _hasPrevious = true;
                _position = 0.0;
            }

            // Emit every output sample that falls between the previous and the
            // current input sample.
            while (_position < 1.0)
            {
                if (written >= destination.Length)
                {
                    throw new ArgumentException(
                        "Destination buffer is too small; use EstimateOutputLength.", nameof(destination));
                }

                var t = (float)_position;
                destination[written++] = _previousSample + ((current - _previousSample) * t);
                _position += _ratio;
            }

            _position -= 1.0;
            _previousSample = current;
        }

        return written;
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

        _hasPrevious = false;
        _previousSample = 0f;
        _position = 0.0;
    }
}
