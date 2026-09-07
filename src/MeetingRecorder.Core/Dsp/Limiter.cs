using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Look-ahead brick-wall limiter plus a final hard clamp.
/// </summary>
/// <remarks>
/// The look-ahead delay means the gain is already reduced by the time a
/// transient arrives, so limiting is inaudible in normal use. The hard clamp at
/// the end is the belt-and-braces guarantee the requirements ask for: whatever
/// the envelope follower does, a sample handed to the 16-bit encoder can never
/// exceed the ceiling and therefore can never wrap around into a click.
/// </remarks>
public sealed class Limiter
{
    private readonly double _ceiling;
    private readonly float _ceilingF;
    private readonly bool _enabled;
    private readonly float[] _delay;
    private readonly double _attackCoef;
    private readonly double _releaseCoef;

    private int _delayIndex;
    private double _envelope;
    private double _gain = 1.0;

    public Limiter(AudioProcessingSettings settings, int sampleRate)
    {
        _enabled = settings.LimiterEnabled;
        _ceiling = AudioMath.DbToLinear(settings.LimiterCeilingDb);
        _ceilingF = (float)_ceiling;

        var lookaheadSamples = Math.Max(1, (int)(settings.LimiterLookaheadMs / 1000.0 * sampleRate));
        _delay = new float[lookaheadSamples];

        // Attack is tied to the look-ahead window: the gain reaches its target in
        // about the time it takes the offending sample to travel the delay line.
        _attackCoef = AudioMath.TimeConstantCoefficient(settings.LimiterLookaheadMs / 3.0, sampleRate);
        _releaseCoef = AudioMath.TimeConstantCoefficient(settings.LimiterReleaseMs, sampleRate);
    }

    public double CurrentGainReductionDb => AudioMath.LinearToDb(_gain) * -1.0;

    /// <summary>Number of samples the limiter delays the signal by.</summary>
    public int LatencySamples => _delay.Length;

    public void Process(Span<float> buffer)
    {
        if (!_enabled)
        {
            // Clipping protection is mandatory even with the limiter disabled.
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = AudioMath.Clamp(buffer[i], -_ceilingF, _ceilingF);
            }

            return;
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            var input = buffer[i];

            // Detector runs on the *incoming* (not yet delayed) sample.
            var rectified = Math.Abs((double)input);
            _envelope = rectified > _envelope
                ? rectified
                : (_envelope * _releaseCoef) + (rectified * (1 - _releaseCoef));

            var target = _envelope > _ceiling ? _ceiling / _envelope : 1.0;
            var coef = target < _gain ? _attackCoef : _releaseCoef;
            _gain = (_gain * coef) + (target * (1 - coef));

            // Emit the delayed sample with the (already reduced) gain.
            var delayed = _delay[_delayIndex];
            _delay[_delayIndex] = input;
            _delayIndex = (_delayIndex + 1) % _delay.Length;

            var output = (float)(delayed * _gain);
            buffer[i] = AudioMath.Clamp(output, -_ceilingF, _ceilingF);
        }
    }

    public void Reset()
    {
        Array.Clear(_delay);
        _delayIndex = 0;
        _envelope = 0.0;
        _gain = 1.0;
    }
}
