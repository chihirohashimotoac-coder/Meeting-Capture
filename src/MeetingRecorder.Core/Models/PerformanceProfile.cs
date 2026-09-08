namespace MeetingRecorder.Core.Models;

/// <summary>
/// The automatically chosen runtime configuration. The user never picks
/// "fast / balanced / accurate" - this is derived from a measurement taken on
/// the actual machine (see <see cref="Benchmark.ProfileSelector"/>).
/// </summary>
public sealed class PerformanceProfile
{
    /// <summary>Catalog id of the Whisper model that was selected.</summary>
    public string SttModelId { get; set; } = "whisper-base-q5_1";

    /// <summary>Threads handed to whisper.cpp. Never all logical cores.</summary>
    public int SttThreads { get; set; } = 4;

    /// <summary>Length of audio handed to one inference call.</summary>
    public double ChunkSeconds { get; set; } = 8.0;

    /// <summary>
    /// Audio replayed from the previous chunk so a word split across the seam is
    /// still recognized once.
    /// </summary>
    public int ChunkOverlapMs { get; set; } = 400;

    public bool VadEnabled { get; set; } = true;

    /// <summary>Greedy decoding (beam size 1) is ~2x faster and adequate for meetings.</summary>
    public int BeamSize { get; set; } = 1;

    /// <summary>
    /// Silence that ends a live chunk. Shorter means a sentence reaches the
    /// screen sooner; too short cuts a speaker off mid-thought.
    /// </summary>
    public int SilenceFlushMs { get; set; } = 700;

    /// <summary>
    /// Catalog id of the model for the second, accurate pass. Null when the
    /// machine cannot hold anything better than the live model.
    /// </summary>
    public string? RefinementModelId { get; set; }

    public bool DiarizationEnabled { get; set; } = true;

    public bool MinutesEnabled { get; set; } = true;

    // ---- Evidence for the decision (shown in the diagnostics panel) -------

    public int LogicalCores { get; set; }

    public double TotalRamGb { get; set; }

    /// <summary>Synthetic FLOP-ish score from the CPU probe (higher is faster).</summary>
    public double CpuScore { get; set; }

    /// <summary>
    /// Real-time factor measured with the real STT engine, if a calibration run
    /// has happened: inference seconds / audio seconds. &lt; 1 means real time.
    /// </summary>
    public double? MeasuredRealTimeFactor { get; set; }

    public DateTimeOffset MeasuredAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Human readable justification, surfaced in the UI.</summary>
    public string Rationale { get; set; } = string.Empty;

    public PerformanceProfile Clone() => (PerformanceProfile)MemberwiseClone();
}
