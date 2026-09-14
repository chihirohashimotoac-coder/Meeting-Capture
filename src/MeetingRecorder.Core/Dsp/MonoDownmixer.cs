namespace MeetingRecorder.Core.Dsp;

/// <summary>How a multi-channel capture stream is reduced to the engine's mono.</summary>
public enum MonoDownmixMode
{
    /// <summary>The arithmetic mean of every channel. The default, and correct for almost every endpoint.</summary>
    Average,

    /// <summary>Channel 0 only. Chosen automatically, and only on evidence, when averaging cancels the signal.</summary>
    FirstChannel,
}

/// <summary>
/// Converts an interleaved multi-channel capture block to mono, and notices the
/// one configuration where the obvious way of doing that destroys the audio.
/// </summary>
/// <remarks>
/// <para><b>Why averaging is the default and stays the default.</b> For a mono
/// endpoint it is the identity. For a genuine stereo microphone or an array,
/// the channels carry the same voice with a small inter-channel delay, and the
/// mean is a well behaved, phase-coherent sum that also averages down the
/// uncorrelated part of each channel's self-noise. Switching to "channel 0
/// only" wholesale would throw that away on every normal device to protect
/// against a rare one, which is the wrong trade and was explicitly ruled out.
/// </para>
/// <para><b>The configuration that breaks it.</b> A few endpoints present a
/// differential pair - the same signal on two channels with one inverted, or a
/// deliberately anti-phase noise-cancelling capsule pair exposed raw. Averaging
/// those subtracts the voice from itself and leaves the difference: quiet,
/// thin, and missing exactly the low-mid energy that makes speech sound
/// full-bodied. It is indistinguishable from "the microphone is muffled" to a
/// listener.
/// </para>
/// <para><b>How this tells the two apart.</b> Purely from energy, over a bounded
/// window at the start of the stream:</para>
/// <list type="bullet">
/// <item><description>N channels carrying the <i>same</i> signal: the mean has the
/// same energy as one channel, so the ratio is 0 dB.</description></item>
/// <item><description>N channels carrying <i>uncorrelated</i> signals (independent
/// self-noise, a genuinely stereo room): the mean has 1/N of the energy, so the
/// ratio is exactly -10·log10(N) dB - -3 dB for a pair. Normal, and left
/// alone.</description></item>
/// <item><description>N channels that <i>cancel</i>: the mean tends to zero and the
/// ratio falls without bound.</description></item>
/// </list>
/// <para>
/// So the decision threshold is <c>-10·log10(N) - 6 dB</c>: six decibels below
/// the worst a merely-uncorrelated stream can produce. Nothing but cancellation
/// gets there. Only frames that actually carry signal are measured, the verdict
/// needs <see cref="MinimumAnalysisSeconds"/> of them, and once reached it is
/// final for the life of the stream - a decision that flickered mid-meeting
/// would put a level step in the recording.
/// </para>
/// <para>
/// This is a mono-conversion choice, not a processing stage: whichever mode is
/// active, the output is a fixed linear combination of the inputs. There is no
/// gain that varies with time, and every sample of the chosen combination is
/// multiplied by the same constant.
/// </para>
/// </remarks>
public sealed class MonoDownmixer
{
    /// <summary>Signal that must be measured before a verdict is allowed, in seconds.</summary>
    public const double MinimumAnalysisSeconds = 0.5;

    /// <summary>Signal measured before analysis stops, decision or not, in seconds.</summary>
    public const double MaximumAnalysisSeconds = 8.0;

    /// <summary>
    /// How far below the uncorrelated case the averaged energy has to sit
    /// before it is called cancellation, in dB.
    /// </summary>
    public const double CancellationMarginDb = 6.0;

    /// <summary>Frames quieter than this are silence and tell us nothing.</summary>
    private const double AnalysisFloorDb = -55.0;

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly double _uncorrelatedRatioDb;

    private double _averageEnergy;
    private double _channelEnergy;
    private long _analysedSamples;
    private bool _decided;

    public MonoDownmixer(int channels, int sampleRate)
    {
        _channels = Math.Max(1, channels);
        _sampleRate = Math.Max(1, sampleRate);
        _uncorrelatedRatioDb = -10.0 * Math.Log10(_channels);

        // A mono stream has nothing to average and nothing to cancel.
        _decided = _channels <= 1;
    }

    public int Channels => _channels;

    public MonoDownmixMode Mode { get; private set; } = MonoDownmixMode.Average;

    /// <summary>True once the analysis window has closed, one way or the other.</summary>
    public bool AnalysisComplete => _decided || _analysedSamples >= (long)(MaximumAnalysisSeconds * _sampleRate);

    /// <summary>
    /// Measured ratio of the averaged energy to the mean per-channel energy, in
    /// dB, or <see cref="AudioMath.MinDb"/> before anything has been measured.
    /// </summary>
    public double CorrelationRatioDb => _channelEnergy <= 0.0
        ? AudioMath.MinDb
        : AudioMath.LinearToDb(Math.Sqrt(_averageEnergy / _channelEnergy));

    /// <summary>The ratio a stream of independent channels would produce, in dB.</summary>
    public double UncorrelatedRatioDb => _uncorrelatedRatioDb;

    /// <summary>Raised once, if ever, when the mode is switched. Carries a log-ready message.</summary>
    public event Action<string>? ModeChanged;

    /// <summary>
    /// Reduces one interleaved block to mono.
    /// </summary>
    /// <returns>Frames written to <paramref name="destination"/>.</returns>
    public int Process(ReadOnlySpan<float> interleaved, Span<float> destination)
    {
        if (_channels <= 1)
        {
            interleaved.CopyTo(destination);
            return interleaved.Length;
        }

        var frames = interleaved.Length / _channels;
        if (frames <= 0)
        {
            return 0;
        }

        if (destination.Length < frames)
        {
            throw new ArgumentException("Destination is too small for the mono result.", nameof(destination));
        }

        var measure = !AnalysisComplete;
        double blockAverageEnergy = 0.0;
        double blockChannelEnergy = 0.0;

        for (var f = 0; f < frames; f++)
        {
            var baseIndex = f * _channels;

            double sum = 0.0;
            if (measure)
            {
                for (var c = 0; c < _channels; c++)
                {
                    double s = interleaved[baseIndex + c];
                    sum += s;
                    blockChannelEnergy += s * s;
                }
            }
            else
            {
                for (var c = 0; c < _channels; c++)
                {
                    sum += interleaved[baseIndex + c];
                }
            }

            var mean = (float)(sum / _channels);
            if (measure)
            {
                blockAverageEnergy += (double)mean * mean;
            }

            destination[f] = Mode == MonoDownmixMode.Average ? mean : interleaved[baseIndex];
        }

        if (measure)
        {
            Accumulate(frames, blockAverageEnergy, blockChannelEnergy / _channels);
        }

        return frames;
    }

    private void Accumulate(int frames, double averageEnergy, double perChannelEnergy)
    {
        // Digital silence and room tone say nothing about whether two channels
        // cancel; measuring them would only dilute the verdict.
        var levelDb = AudioMath.LinearToDb(Math.Sqrt(perChannelEnergy / frames));
        if (levelDb < AnalysisFloorDb)
        {
            return;
        }

        _averageEnergy += averageEnergy;
        _channelEnergy += perChannelEnergy;
        _analysedSamples += frames;

        if (_decided || _analysedSamples < (long)(MinimumAnalysisSeconds * _sampleRate))
        {
            return;
        }

        _decided = true;

        var ratioDb = CorrelationRatioDb;
        if (ratioDb >= _uncorrelatedRatioDb - CancellationMarginDb)
        {
            return;
        }

        Mode = MonoDownmixMode.FirstChannel;
        ModeChanged?.Invoke(
            $"Averaging {_channels} channels cancels this endpoint's signal "
            + $"({ratioDb:F1} dB against {_uncorrelatedRatioDb:F1} dB for uncorrelated channels); "
            + "switched this stream to channel 0 so the voice is not subtracted from itself.");
    }

    public void Reset()
    {
        _averageEnergy = 0.0;
        _channelEnergy = 0.0;
        _analysedSamples = 0;
        _decided = _channels <= 1;
        Mode = MonoDownmixMode.Average;
    }
}
