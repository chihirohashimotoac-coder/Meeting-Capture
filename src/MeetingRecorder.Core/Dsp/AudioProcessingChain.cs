using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// The per-stream chain applied to the microphone and to the system-audio leg
/// independently, before they are mixed:
/// <c>DC blocker -> soft gate -> loudness normalizer -> compressor -> limiter</c>.
/// </summary>
/// <remarks>
/// Order matters. The gate runs before the AGC so that background noise cannot
/// drive the gain; the compressor runs after the AGC so it always sees a signal
/// near the target loudness and therefore behaves consistently across machines;
/// the limiter is last because it is the only stage that may not be bypassed -
/// it is the clipping guarantee.
/// </remarks>
public sealed class AudioProcessingChain
{
    private readonly AudioProcessingSettings _settings;
    private readonly DcBlocker _dcBlocker;
    private readonly NoiseGate _gate;
    private readonly LoudnessNormalizer _normalizer;
    private readonly Compressor _compressor;
    private readonly Limiter _limiter;

    public AudioProcessingChain(AudioProcessingSettings settings, int sampleRate)
    {
        _settings = settings;
        SampleRate = sampleRate;
        _dcBlocker = new DcBlocker(sampleRate);
        _gate = new NoiseGate(settings, sampleRate);
        _normalizer = new LoudnessNormalizer(settings, sampleRate);
        _compressor = new Compressor(settings, sampleRate);
        _limiter = new Limiter(settings, sampleRate);
    }

    public int SampleRate { get; }

    /// <summary>Processing latency introduced by the chain (limiter look-ahead only).</summary>
    public int LatencySamples => _limiter.LatencySamples;

    public double CurrentGainDb => _normalizer.CurrentGainDb;

    public double MeasuredRmsDb => _normalizer.MeasuredRmsDb;

    public bool SpeechDetected => _gate.IsOpen;

    /// <summary>Processes a block of mono float samples in place.</summary>
    public void Process(Span<float> buffer)
    {
        if (_settings.DcBlockerEnabled)
        {
            _dcBlocker.Process(buffer);
        }

        _gate.Process(buffer);
        _normalizer.Process(buffer);
        _compressor.Process(buffer);
        _limiter.Process(buffer);
    }

    public void Reset()
    {
        _dcBlocker.Reset();
        _gate.Reset();
        _normalizer.Reset();
        _compressor.Reset();
        _limiter.Reset();
    }
}
