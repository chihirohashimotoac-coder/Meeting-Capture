namespace MeetingRecorder.Core.Models;

/// <summary>
/// The automatically chosen runtime configuration. The user never picks
/// "fast / balanced / accurate" - this is derived from a measurement taken on
/// the actual machine (see <see cref="Benchmark.ProfileSelector"/>), and they
/// can still override the model in the settings window.
/// </summary>
/// <remarks>
/// This used to carry chunk lengths, overlaps, silence thresholds, a beam size
/// and a second model id, because a live transcript had to be tuned against the
/// clock. Nothing runs against the clock any more: transcription happens on a
/// finished file, with the decoding settings in
/// <see cref="Stt.SpeechRecognitionOptions"/> and the window lengths in
/// <see cref="Stt.OfflineTranscriptionService"/>, neither of which depends on
/// the machine. What is left here is the choice of model, how many threads to
/// give it, and the evidence behind both.
/// </remarks>
public sealed class PerformanceProfile
{
    /// <summary>Catalog id of the Whisper model that was selected.</summary>
    public string SttModelId { get; set; } = "whisper-small-q5_1";

    /// <summary>Threads handed to whisper.cpp. Never all logical cores.</summary>
    public int SttThreads { get; set; } = 4;

    public bool DiarizationEnabled { get; set; } = true;

    public bool MinutesEnabled { get; set; } = true;

    // ---- Evidence for the decision (shown in the diagnostics panel) -------

    public int LogicalCores { get; set; }

    public double TotalRamGb { get; set; }

    /// <summary>Synthetic FLOP-ish score from the CPU probe (higher is faster).</summary>
    public double CpuScore { get; set; }

    /// <summary>
    /// Estimated processing time as a fraction of audio length for the selected
    /// model on this machine: 1.0 means an hour of meeting takes about an hour
    /// to transcribe. An estimate, not a measurement - it exists so the settings
    /// window can warn before someone picks a model that will take all evening.
    /// </summary>
    public double EstimatedProcessingFactor { get; set; }

    public DateTimeOffset MeasuredAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Human readable justification, surfaced in the UI.</summary>
    public string Rationale { get; set; } = string.Empty;

    public PerformanceProfile Clone() => (PerformanceProfile)MemberwiseClone();
}
