namespace MeetingRecorder.Core.Dsp;

/// <summary>
/// Thread-safe VU/peak meter. The capture thread pushes blocks, the UI thread
/// reads a snapshot; neither blocks the other for more than a few instructions.
/// </summary>
public sealed class LevelMeter
{
    private readonly object _sync = new();
    private readonly double _decayDbPerSecond;
    private double _rmsDb = AudioMath.MinDb;
    private double _peakDb = AudioMath.MinDb;
    private double _peakHoldDb = AudioMath.MinDb;
    private DateTime _lastUpdateUtc = DateTime.UtcNow;

    public LevelMeter(double decayDbPerSecond = 40.0)
    {
        _decayDbPerSecond = decayDbPerSecond;
    }

    /// <summary>Feed a block of mono samples. Safe to call from the capture thread.</summary>
    public void Update(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        var rms = AudioMath.RmsDb(samples);
        var peak = AudioMath.PeakDb(samples);
        var now = DateTime.UtcNow;

        lock (_sync)
        {
            var elapsed = (now - _lastUpdateUtc).TotalSeconds;
            _lastUpdateUtc = now;
            var decay = _decayDbPerSecond * Math.Clamp(elapsed, 0.0, 1.0);

            _rmsDb = Math.Max(rms, _rmsDb - decay);
            _peakDb = Math.Max(peak, _peakDb - decay);
            _peakHoldDb = Math.Max(peak, _peakHoldDb - (decay * 0.25));
        }
    }

    /// <summary>Records a period during which the stream produced no data at all.</summary>
    public void Decay(TimeSpan elapsed)
    {
        lock (_sync)
        {
            var decay = _decayDbPerSecond * Math.Clamp(elapsed.TotalSeconds, 0.0, 1.0);
            _rmsDb -= decay;
            _peakDb -= decay;
            _peakHoldDb -= decay * 0.25;
        }
    }

    public LevelSnapshot Read()
    {
        lock (_sync)
        {
            return new LevelSnapshot(
                Math.Max(_rmsDb, AudioMath.MinDb),
                Math.Max(_peakDb, AudioMath.MinDb),
                Math.Max(_peakHoldDb, AudioMath.MinDb));
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _rmsDb = AudioMath.MinDb;
            _peakDb = AudioMath.MinDb;
            _peakHoldDb = AudioMath.MinDb;
        }
    }
}

/// <param name="RmsDb">Smoothed RMS level in dBFS.</param>
/// <param name="PeakDb">Decaying peak level in dBFS.</param>
/// <param name="PeakHoldDb">Slowly decaying peak hold, for the meter's tick mark.</param>
public readonly record struct LevelSnapshot(double RmsDb, double PeakDb, double PeakHoldDb)
{
    /// <summary>Maps dBFS onto 0..1 for a meter widget (-60 dBFS = 0, 0 dBFS = 1).</summary>
    public double Normalized(double floorDb = -60.0)
        => Math.Clamp((RmsDb - floorDb) / (0.0 - floorDb), 0.0, 1.0);

    public double NormalizedPeak(double floorDb = -60.0)
        => Math.Clamp((PeakDb - floorDb) / (0.0 - floorDb), 0.0, 1.0);

    /// <summary>
    /// True when the stream is so quiet that it is almost certainly not
    /// connected - used by the pre-flight check to warn before recording.
    /// </summary>
    public bool LooksSilent(double silenceDb = -65.0) => PeakDb < silenceDb;
}
