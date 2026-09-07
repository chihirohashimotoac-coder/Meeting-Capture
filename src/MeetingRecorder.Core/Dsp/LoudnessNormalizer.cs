using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Slow, bounded automatic gain control that pulls each stream towards a common
/// speech loudness so that a quiet room microphone and a loud conference call
/// end up at comparable levels in the mix.
/// </summary>
/// <remarks>
/// This is loudness (RMS) normalization, not peak normalization: peak-normalizing
/// a meeting makes one door slam define the level of the whole recording. The
/// gain moves at most <see cref="AudioProcessingSettings.GainAdaptationDbPerSecond"/>
/// and is frozen during silence, which is what keeps it from "pumping" the room
/// tone up between sentences.
/// </remarks>
public sealed class LoudnessNormalizer
{
    private readonly AudioProcessingSettings _settings;
    private readonly int _sampleRate;
    private readonly double _measureCoef;

    private double _meanSquare;
    private double _gainDb;
    private bool _primed;

    public LoudnessNormalizer(AudioProcessingSettings settings, int sampleRate)
    {
        _settings = settings;
        _sampleRate = sampleRate;
        // 400 ms integration window - the same order as a momentary loudness
        // meter (ITU-R BS.1770 uses 400 ms), long enough to ignore syllable
        // level dynamics.
        _measureCoef = AudioMath.TimeConstantCoefficient(400.0, sampleRate);
    }

    /// <summary>Gain currently applied, in dB. Exposed for the diagnostics panel.</summary>
    public double CurrentGainDb => _gainDb;

    /// <summary>Smoothed input loudness in dBFS.</summary>
    public double MeasuredRmsDb => AudioMath.LinearToDb(Math.Sqrt(_meanSquare));

    public void Process(Span<float> buffer)
    {
        if (!_settings.AgcEnabled || buffer.Length == 0)
        {
            return;
        }

        // Measure on the block, adapt once per block: per-sample adaptation buys
        // nothing at a 1.5 dB/s slew rate and costs a log per sample.
        double sum = 0.0;
        for (var i = 0; i < buffer.Length; i++)
        {
            double s = buffer[i];
            sum += s * s;
        }

        var blockMeanSquare = sum / buffer.Length;
        if (!_primed)
        {
            _meanSquare = blockMeanSquare;
            _primed = true;
        }
        else
        {
            _meanSquare = (_meanSquare * _measureCoef) + (blockMeanSquare * (1 - _measureCoef));
        }

        var levelDb = AudioMath.LinearToDb(Math.Sqrt(_meanSquare));
        var blockSeconds = buffer.Length / (double)_sampleRate;
        var maxStepDb = _settings.GainAdaptationDbPerSecond * blockSeconds;

        // Freeze adaptation while the stream is effectively silent, otherwise the
        // AGC would keep winding gain up on room tone during a pause and then
        // slam it back down on the next word.
        if (levelDb > _settings.AgcSilenceFloorDb)
        {
            var desiredGainDb = _settings.TargetRmsDb - levelDb;
            desiredGainDb = Math.Clamp(desiredGainDb, _settings.MinGainDb, _settings.MaxGainDb);

            var delta = Math.Clamp(desiredGainDb - _gainDb, -maxStepDb, maxStepDb);
            _gainDb += delta;
        }

        var linear = (float)AudioMath.DbToLinear(_gainDb);
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] *= linear;
        }
    }

    public void Reset()
    {
        _meanSquare = 0.0;
        _gainDb = 0.0;
        _primed = false;
    }
}
