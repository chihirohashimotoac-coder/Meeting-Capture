namespace MeetingRecorder.Core.Models;

/// <summary>
/// Tuning constants for the per-stream processing chain.
/// </summary>
/// <remarks>
/// <para><b>Why these defaults?</b> (also recorded in docs/ARCHITECTURE.md)</para>
/// <list type="bullet">
/// <item><description>
/// <b>-20 dBFS RMS target.</b> whisper.cpp resamples to 16 kHz float and is
/// happiest with speech that sits well above the noise floor but far from
/// clipping. -20 dBFS RMS (~ -20 LUFS for speech) leaves ~20 dB of crest factor
/// head-room for plosives, which is what broadcast speech loudness practice
/// uses as well.
/// </description></item>
/// <item><description>
/// <b>+18 dB / -12 dB gain clamp.</b> Unbounded make-up gain on a quiet laptop
/// microphone amplifies fan and room noise until the recognizer starts
/// hallucinating. 18 dB recovers a genuinely quiet talker without lifting a
/// -60 dBFS noise floor into audibility.
/// </description></item>
/// <item><description>
/// <b>Slow adaptation (1.5 dB/s).</b> Fast AGC "pumps" between sentences, which
/// is both unpleasant and measurably bad for recognition because it modulates
/// the spectral envelope. Gain is additionally frozen while the gate says the
/// stream is silent, so room tone never drags the gain up.
/// </description></item>
/// <item><description>
/// <b>Gate attenuates by 12 dB, it does not mute.</b> Hard gating removes
/// back-channel responses ("はい", "うん") and word endings, which the
/// requirements explicitly forbid. Ducking the background instead keeps every
/// syllable in the recording.
/// </description></item>
/// <item><description>
/// <b>Limiter ceiling -1 dBFS with 5 ms look-ahead.</b> Guarantees the 16-bit
/// mix can never wrap around, while the look-ahead avoids the audible
/// distortion a zero-latency clipper produces.
/// </description></item>
/// </list>
/// </remarks>
public sealed class AudioProcessingSettings
{
    // ---- DC blocker ------------------------------------------------------
    public bool DcBlockerEnabled { get; set; } = true;

    // ---- Noise gate (soft, expander style) -------------------------------
    public bool GateEnabled { get; set; } = true;

    /// <summary>Below this RMS level the stream is considered background.</summary>
    public double GateThresholdDb { get; set; } = -52.0;

    /// <summary>Maximum attenuation applied to background. Never a full mute.</summary>
    public double GateMaxAttenuationDb { get; set; } = 12.0;

    /// <summary>Keep the gate open this long after speech stops (protects word endings).</summary>
    public int GateHoldMs { get; set; } = 300;

    public int GateAttackMs { get; set; } = 5;

    public int GateReleaseMs { get; set; } = 150;

    // ---- Loudness normalization (slow AGC) -------------------------------
    public bool AgcEnabled { get; set; } = true;

    public double TargetRmsDb { get; set; } = -20.0;

    public double MaxGainDb { get; set; } = 18.0;

    public double MinGainDb { get; set; } = -12.0;

    /// <summary>Gain slew rate. Deliberately slow to avoid pumping.</summary>
    public double GainAdaptationDbPerSecond { get; set; } = 1.5;

    /// <summary>RMS below this is treated as silence and does not move the gain.</summary>
    public double AgcSilenceFloorDb { get; set; } = -55.0;

    // ---- Compressor ------------------------------------------------------
    public bool CompressorEnabled { get; set; } = true;

    public double CompressorThresholdDb { get; set; } = -18.0;

    public double CompressorRatio { get; set; } = 2.5;

    public double CompressorKneeDb { get; set; } = 6.0;

    public int CompressorAttackMs { get; set; } = 10;

    public int CompressorReleaseMs { get; set; } = 200;

    // ---- Limiter ---------------------------------------------------------
    public bool LimiterEnabled { get; set; } = true;

    public double LimiterCeilingDb { get; set; } = -1.0;

    public int LimiterLookaheadMs { get; set; } = 5;

    public int LimiterReleaseMs { get; set; } = 50;

    // ---- Mixer -----------------------------------------------------------

    /// <summary>Extra gain applied to the microphone leg before summing.</summary>
    public double MicMixGainDb { get; set; }

    /// <summary>Extra gain applied to the system-audio leg before summing.</summary>
    public double SystemMixGainDb { get; set; }

    /// <summary>
    /// Head-room removed from the sum before the shared limiter. Two
    /// independently normalized streams that talk at once would otherwise sit
    /// ~6 dB hot; -3 dB is the statistical compromise for partially correlated
    /// sources.
    /// </summary>
    public double MixHeadroomDb { get; set; } = -3.0;

    public AudioProcessingSettings Clone() => (AudioProcessingSettings)MemberwiseClone();
}
