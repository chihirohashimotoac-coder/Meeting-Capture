using MeetingRecorder.Core.Dsp;

namespace MeetingRecorder.Core.Stt;

/// <summary>
/// Adaptive energy voice-activity detector.
/// </summary>
/// <remarks>
/// <para>
/// The VAD exists to stop the recognizer from burning CPU on silence, not to
/// decide what counts as "important speech". The requirements are explicit that
/// quiet voices, back-channel responses ("はい", "ええ"), sentence-final
/// particles and very short utterances must survive, so the design errs
/// heavily towards keeping audio:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The threshold tracks the measured noise floor (+8 dB) instead of using a
/// fixed level, so a quiet room lowers the bar rather than swallowing whispers.
/// </description></item>
/// <item><description>
/// Speech onset needs only 40 ms above the threshold; a single short "はい" is
/// roughly 150-250 ms, so it is caught comfortably.
/// </description></item>
/// <item><description>
/// A 600 ms hang-over keeps the detector open after the level drops, which is
/// what preserves trailing particles and the decay of the last vowel.
/// </description></item>
/// <item><description>
/// 300 ms of audio *before* the onset is always prepended by the chunker, so
/// even the attack of the first consonant is transcribed.
/// </description></item>
/// </list>
/// </remarks>
public sealed class EnergyVad
{
    private readonly int _sampleRate;
    private readonly int _frameSamples;
    private readonly double _marginDb;
    private readonly double _absoluteFloorDb;
    private readonly int _onsetFrames;
    private readonly int _hangoverFrames;

    private double _noiseFloorDb = -60.0;
    private int _consecutiveSpeechFrames;
    private int _hangoverCounter;
    private bool _isSpeech;

    public EnergyVad(
        int sampleRate = SpeechConstants.SampleRate,
        double frameMs = 20.0,
        double marginDb = 8.0,
        double absoluteFloorDb = -65.0,
        double onsetMs = 40.0,
        double hangoverMs = 600.0)
    {
        _sampleRate = sampleRate;
        _frameSamples = Math.Max(1, (int)(sampleRate * frameMs / 1000.0));
        _marginDb = marginDb;
        _absoluteFloorDb = absoluteFloorDb;
        _onsetFrames = Math.Max(1, (int)Math.Round(onsetMs / frameMs));
        _hangoverFrames = Math.Max(1, (int)Math.Round(hangoverMs / frameMs));
    }

    public int FrameSamples => _frameSamples;

    public bool IsSpeech => _isSpeech;

    public double NoiseFloorDb => _noiseFloorDb;

    public double ThresholdDb => Math.Max(_noiseFloorDb + _marginDb, _absoluteFloorDb);

    /// <summary>
    /// Classifies one frame of <see cref="FrameSamples"/> samples.
    /// Returns the state after the frame.
    /// </summary>
    public bool ProcessFrame(ReadOnlySpan<float> frame)
    {
        var levelDb = AudioMath.RmsDb(frame);

        // Noise floor tracking: fall fast towards a new quiet level, rise very
        // slowly, so a long utterance cannot drag the floor up over itself.
        if (levelDb < _noiseFloorDb)
        {
            _noiseFloorDb += (levelDb - _noiseFloorDb) * 0.25;
        }
        else if (!_isSpeech)
        {
            _noiseFloorDb += (levelDb - _noiseFloorDb) * 0.002;
        }

        _noiseFloorDb = Math.Clamp(_noiseFloorDb, -90.0, -20.0);

        var above = levelDb > ThresholdDb;
        if (above)
        {
            _consecutiveSpeechFrames++;
            if (_consecutiveSpeechFrames >= _onsetFrames)
            {
                _isSpeech = true;
                _hangoverCounter = _hangoverFrames;
            }
        }
        else
        {
            _consecutiveSpeechFrames = 0;
            if (_hangoverCounter > 0)
            {
                _hangoverCounter--;
            }
            else
            {
                _isSpeech = false;
            }
        }

        return _isSpeech;
    }

    public void Reset()
    {
        _noiseFloorDb = -60.0;
        _consecutiveSpeechFrames = 0;
        _hangoverCounter = 0;
        _isSpeech = false;
    }
}
