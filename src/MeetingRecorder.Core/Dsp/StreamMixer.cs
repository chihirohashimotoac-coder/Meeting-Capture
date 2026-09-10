using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Sums the microphone and the PC-audio leg into the single meeting track that
/// is written to disk.
/// </summary>
/// <remarks>
/// <para>
/// The whole mixer is <c>out[n] = g * (mic[n] + system[n])</c> with one
/// constant <c>g</c> (<see cref="AudioProcessingSettings.MixGainPerStreamDb"/>,
/// -6.02 dB = 0.5). At that value two full-scale inputs sum to exactly full
/// scale, so the file can never clip - without a limiter, without a clamp, and
/// without the sum's dynamics being touched.
/// </para>
/// <para>
/// The 6 dB is not lost. Once recording stops and the file's real peak is
/// known, <see cref="Audio.PeakNormalizer"/> gives back as much of it as fits
/// under the target peak, again as a single constant gain over the whole file.
/// Doing it then rather than now is the point: a live decision would have to
/// guess at audio it has not heard yet, and guessing is what an AGC does.
/// </para>
/// </remarks>
public sealed class StreamMixer
{
    private readonly float _gain;

    public StreamMixer(AudioProcessingSettings settings, int sampleRate)
    {
        _gain = (float)AudioMath.DbToLinear(settings.MixGainPerStreamDb);
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    /// <summary>The constant each leg is multiplied by before the sum.</summary>
    public double Gain => _gain;

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
            destination[i] = (mic[i] + system[i]) * _gain;
        }
    }

    public void Reset()
    {
    }
}
