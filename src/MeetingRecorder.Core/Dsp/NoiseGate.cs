using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Soft downward expander. Background is ducked by at most
/// <see cref="AudioProcessingSettings.GateMaxAttenuationDb"/> - it is never
/// muted, because hard gating is what removes back-channel responses and word
/// endings from Japanese meeting audio.
/// </summary>
public sealed class NoiseGate
{
    private readonly AudioProcessingSettings _settings;
    private readonly int _sampleRate;
    private readonly double _attackCoef;
    private readonly double _releaseCoef;
    private readonly double _envelopeCoef;
    private readonly int _holdSamples;

    private double _envelope;
    private double _gain = 1.0;
    private int _holdCounter;

    public NoiseGate(AudioProcessingSettings settings, int sampleRate)
    {
        _settings = settings;
        _sampleRate = sampleRate;
        _attackCoef = AudioMath.TimeConstantCoefficient(settings.GateAttackMs, sampleRate);
        _releaseCoef = AudioMath.TimeConstantCoefficient(settings.GateReleaseMs, sampleRate);
        // 20 ms detector: long enough not to chatter on a single glottal pulse,
        // short enough to open before the first syllable is audible.
        _envelopeCoef = AudioMath.TimeConstantCoefficient(20.0, sampleRate);
        _holdSamples = (int)(settings.GateHoldMs / 1000.0 * sampleRate);
    }

    /// <summary>True while the detector considers the stream to carry speech.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Current detector level in dBFS, exposed for diagnostics.</summary>
    public double EnvelopeDb => AudioMath.LinearToDb(_envelope);

    public void Process(Span<float> buffer)
    {
        if (!_settings.GateEnabled)
        {
            IsOpen = true;
            return;
        }

        var threshold = AudioMath.DbToLinear(_settings.GateThresholdDb);
        var floorGain = AudioMath.DbToLinear(-Math.Abs(_settings.GateMaxAttenuationDb));

        for (var i = 0; i < buffer.Length; i++)
        {
            var rectified = Math.Abs((double)buffer[i]);
            _envelope = rectified > _envelope
                ? rectified
                : (_envelope * _envelopeCoef) + (rectified * (1 - _envelopeCoef));

            if (_envelope >= threshold)
            {
                _holdCounter = _holdSamples;
            }
            else if (_holdCounter > 0)
            {
                _holdCounter--;
            }

            var open = _envelope >= threshold || _holdCounter > 0;
            IsOpen = open;

            var target = open ? 1.0 : floorGain;
            var coef = target > _gain ? _attackCoef : _releaseCoef;
            _gain = (_gain * coef) + (target * (1 - coef));

            buffer[i] = (float)(buffer[i] * _gain);
        }
    }

    public void Reset()
    {
        _envelope = 0.0;
        _gain = 1.0;
        _holdCounter = 0;
        IsOpen = false;
    }
}
