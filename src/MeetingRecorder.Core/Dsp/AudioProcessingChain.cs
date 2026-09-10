using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Everything that is done to a capture stream while it is being recorded:
/// removal of any DC offset, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This class used to be a chain - soft gate, loudness normalizer, compressor,
/// limiter. Those stages were there to make a recording pleasant to a human
/// ear, and every one of them changes gain as a function of time. The audio
/// this application produces exists to be transcribed, and a speech recognizer
/// reads exactly the amplitude envelope those stages reshape; the quieter the
/// talker, the more it is reshaped. They are gone.
/// </para>
/// <para>
/// It is kept as a class, rather than dissolved into a call to
/// <see cref="DcBlocker"/>, because it is the single place that answers "what
/// does this application do to my audio?" - and because a test can then assert
/// that the answer stays "almost nothing".
/// </para>
/// </remarks>
public sealed class AudioProcessingChain
{
    private readonly AudioProcessingSettings _settings;
    private readonly DcBlocker _dcBlocker;

    public AudioProcessingChain(AudioProcessingSettings settings, int sampleRate)
    {
        _settings = settings;
        SampleRate = sampleRate;
        _dcBlocker = new DcBlocker(sampleRate, settings.DcBlockerCutoffHz);
    }

    public int SampleRate { get; }

    /// <summary>
    /// Processing latency introduced by the chain. Zero: the DC blocker is a
    /// first-order recursive filter, so a sample in is a sample out.
    /// </summary>
    public int LatencySamples => 0;

    /// <summary>Processes a block of mono float samples in place.</summary>
    public void Process(Span<float> buffer)
    {
        if (_settings.DcBlockerEnabled)
        {
            _dcBlocker.Process(buffer);
        }
    }

    public void Reset() => _dcBlocker.Reset();
}
