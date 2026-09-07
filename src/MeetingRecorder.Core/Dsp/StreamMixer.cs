using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Sums the two already-normalized legs into the single meeting track that is
/// written to disk, then applies the shared output limiter.
/// </summary>
public sealed class StreamMixer
{
    private readonly float _micGain;
    private readonly float _systemGain;
    private readonly float _headroom;
    private readonly Limiter _outputLimiter;

    public StreamMixer(AudioProcessingSettings settings, int sampleRate)
    {
        _micGain = (float)AudioMath.DbToLinear(settings.MicMixGainDb);
        _systemGain = (float)AudioMath.DbToLinear(settings.SystemMixGainDb);
        _headroom = (float)AudioMath.DbToLinear(settings.MixHeadroomDb);
        _outputLimiter = new Limiter(settings, sampleRate);
    }

    /// <summary>
    /// Mixes <paramref name="mic"/> and <paramref name="system"/> into
    /// <paramref name="destination"/>. All three spans must be the same length;
    /// the caller (the synchronized pump) guarantees that by padding whichever
    /// stream is short with silence.
    /// </summary>
    public void Mix(ReadOnlySpan<float> mic, ReadOnlySpan<float> system, Span<float> destination)
    {
        if (mic.Length != system.Length || mic.Length != destination.Length)
        {
            throw new ArgumentException("Mixer inputs and output must have identical lengths.");
        }

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = ((mic[i] * _micGain) + (system[i] * _systemGain)) * _headroom;
        }

        _outputLimiter.Process(destination);
    }

    public void Reset() => _outputLimiter.Reset();
}
