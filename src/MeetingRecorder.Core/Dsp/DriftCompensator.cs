namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Keeps a capture stream aligned with the recording's master clock over hours.
/// </summary>
/// <remarks>
/// <para>
/// The microphone and the render endpoint that is loopback-captured are driven
/// by different crystals. A typical consumer clock is accurate to ~50-100 ppm,
/// so after two hours the two streams can be 0.4-0.7 s apart - clearly audible
/// as an echo between the room voice and the remote voice, and enough to put
/// transcript timestamps visibly out of sync with the audio.
/// </para>
/// <para>
/// The pump consumes exactly as many samples per tick as wall-clock time says it
/// should. A stream whose clock runs fast therefore accumulates a backlog, and a
/// stream whose clock runs slow starves and gets silence inserted. This class
/// watches a smoothed backlog and only orders a correction once the backlog has
/// been genuinely, persistently high - a burst arriving after a silent stretch
/// of WASAPI loopback must not be mistaken for clock drift.
/// </para>
/// </remarks>
public sealed class DriftCompensator
{
    private readonly int _sampleRate;
    private readonly double _targetBacklogSeconds;
    private readonly double _correctionThresholdSeconds;
    private readonly double _smoothingSeconds;
    private readonly double _sustainedSeconds;
    private readonly int _maxCorrectionSamplesPerTick;

    private double _smoothedBacklog = -1.0;
    private double _sustainedFor;

    /// <param name="targetBacklogSeconds">Backlog the pump aims to keep (jitter tolerance).</param>
    /// <param name="correctionThresholdSeconds">Excess backlog that triggers a correction.</param>
    /// <param name="sustainedSeconds">How long the excess must persist before correcting.</param>
    public DriftCompensator(
        int sampleRate,
        double targetBacklogSeconds = 0.10,
        double correctionThresholdSeconds = 0.15,
        double sustainedSeconds = 5.0,
        double smoothingSeconds = 2.0)
    {
        _sampleRate = sampleRate;
        _targetBacklogSeconds = targetBacklogSeconds;
        _correctionThresholdSeconds = correctionThresholdSeconds;
        _sustainedSeconds = sustainedSeconds;
        _smoothingSeconds = smoothingSeconds;
        // 1 ms per tick: inaudible on its own and, at a 20 ms tick, able to
        // absorb up to 5% clock error - two orders of magnitude more than any
        // real device needs.
        _maxCorrectionSamplesPerTick = Math.Max(1, sampleRate / 1000);
    }

    /// <summary>Smoothed backlog in seconds (diagnostics).</summary>
    public double SmoothedBacklogSeconds => Math.Max(0.0, _smoothedBacklog);

    /// <summary>Total samples dropped so far to keep the stream aligned.</summary>
    public long CorrectedSamples { get; private set; }

    /// <summary>Total silence samples inserted so far because the stream starved.</summary>
    public long InsertedSilenceSamples { get; private set; }

    public void ReportSilenceInserted(int samples)
    {
        if (samples > 0)
        {
            InsertedSilenceSamples += samples;
            // A starving stream cannot simultaneously be drifting fast.
            _sustainedFor = 0.0;
            _smoothedBacklog = 0.0;
        }
    }

    /// <summary>
    /// Called once per pump tick with the backlog left in the stream's ring
    /// buffer. Returns how many samples should be discarded this tick.
    /// </summary>
    public int Evaluate(int backlogSamples, double tickSeconds)
    {
        var backlogSeconds = backlogSamples / (double)_sampleRate;
        if (_smoothedBacklog < 0.0)
        {
            _smoothedBacklog = backlogSeconds;
        }
        else
        {
            var alpha = Math.Clamp(tickSeconds / _smoothingSeconds, 0.0, 1.0);
            _smoothedBacklog += (backlogSeconds - _smoothedBacklog) * alpha;
        }

        var excess = _smoothedBacklog - (_targetBacklogSeconds + _correctionThresholdSeconds);
        if (excess <= 0.0)
        {
            _sustainedFor = 0.0;
            return 0;
        }

        _sustainedFor += tickSeconds;
        if (_sustainedFor < _sustainedSeconds)
        {
            return 0;
        }

        var wanted = (int)((_smoothedBacklog - _targetBacklogSeconds) * _sampleRate);
        var correction = Math.Min(wanted, _maxCorrectionSamplesPerTick);
        correction = Math.Min(correction, backlogSamples);
        if (correction <= 0)
        {
            return 0;
        }

        CorrectedSamples += correction;
        _smoothedBacklog -= correction / (double)_sampleRate;
        return correction;
    }

    public void Reset()
    {
        _smoothedBacklog = -1.0;
        _sustainedFor = 0.0;
    }
}
