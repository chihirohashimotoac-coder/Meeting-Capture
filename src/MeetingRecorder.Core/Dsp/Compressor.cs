using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Soft-knee compressor that narrows the dynamic range between a person leaning
/// into the microphone and someone speaking from the far end of the room.
/// </summary>
/// <remarks>
/// Ratio 2.5:1 with a 6 dB knee is intentionally gentle. Aggressive compression
/// raises the noise floor between words, which both sounds bad and increases
/// Whisper's tendency to emit filler text over background noise.
/// </remarks>
public sealed class Compressor
{
    private readonly AudioProcessingSettings _settings;
    private readonly double _attackCoef;
    private readonly double _releaseCoef;
    private double _envelopeDb = AudioMath.MinDb;

    public Compressor(AudioProcessingSettings settings, int sampleRate)
    {
        _settings = settings;
        _attackCoef = AudioMath.TimeConstantCoefficient(settings.CompressorAttackMs, sampleRate);
        _releaseCoef = AudioMath.TimeConstantCoefficient(settings.CompressorReleaseMs, sampleRate);
    }

    /// <summary>Current gain reduction in dB (positive number = how much is being removed).</summary>
    public double GainReductionDb { get; private set; }

    public void Process(Span<float> buffer)
    {
        if (!_settings.CompressorEnabled)
        {
            return;
        }

        var threshold = _settings.CompressorThresholdDb;
        var knee = Math.Max(0.0, _settings.CompressorKneeDb);
        var ratio = Math.Max(1.0, _settings.CompressorRatio);

        // Auto make-up: restore roughly what a signal sitting 6 dB above the
        // threshold loses, so enabling the compressor does not audibly duck the
        // whole meeting.
        var makeupDb = (6.0 - (6.0 / ratio)) * 0.5;
        var makeup = AudioMath.DbToLinear(makeupDb);

        for (var i = 0; i < buffer.Length; i++)
        {
            var levelDb = AudioMath.LinearToDb(Math.Abs((double)buffer[i]));
            var coef = levelDb > _envelopeDb ? _attackCoef : _releaseCoef;
            _envelopeDb = (_envelopeDb * coef) + (levelDb * (1 - coef));

            var over = _envelopeDb - threshold;
            double reduction;
            if (over <= -knee / 2.0)
            {
                reduction = 0.0;
            }
            else if (over >= knee / 2.0)
            {
                reduction = over - (over / ratio);
            }
            else
            {
                // Quadratic interpolation across the knee (standard soft knee).
                var x = over + (knee / 2.0);
                reduction = (1.0 - (1.0 / ratio)) * x * x / (2.0 * knee);
            }

            GainReductionDb = reduction;
            var gain = AudioMath.DbToLinear(-reduction) * makeup;
            buffer[i] = (float)(buffer[i] * gain);
        }
    }

    public void Reset()
    {
        _envelopeDb = AudioMath.MinDb;
        GainReductionDb = 0.0;
    }
}
