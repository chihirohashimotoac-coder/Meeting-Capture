namespace MeetingRecorder.Core.Models;

/// <summary>
/// The complete set of constants that decide what this application does to
/// audio. It is deliberately short.
/// </summary>
/// <remarks>
/// <para><b>The policy, stated once.</b> Recordings made here exist to be
/// transcribed afterwards, not to be broadcast. Every stage that changes level
/// as a function of time - a noise gate, an automatic gain control, a
/// compressor, a limiter - alters the amplitude envelope that a speech
/// recognizer reads as a feature. None of them exist in this application any
/// more, and the tests in <c>DspTests</c> assert that they do not come back.
/// </para>
/// <para>
/// What is left is linear and time invariant: an optional DC blocker, a fixed
/// per-stream gain in the mixer, and a single constant gain applied once to a
/// finished file. Every one of them satisfies
/// <c>output[n] = k * input[n]</c> (with <c>k</c> constant for the whole file),
/// so the relative amplitude of a whisper, a consonant and a shout is exactly
/// what the microphone produced.
/// </para>
/// <para><b>Why these numbers?</b> Recorded here and in
/// <c>docs/ARCHITECTURE.md</c>; none of them was chosen by the user, so none of
/// them is presented as a user requirement.</para>
/// </remarks>
public sealed class AudioProcessingSettings
{
    // ---- DC blocker ------------------------------------------------------

    /// <summary>
    /// Removes the DC offset some USB microphones and virtual endpoints add.
    /// </summary>
    /// <remarks>
    /// Kept after review rather than by inheritance. It is a first-order
    /// high-pass at <see cref="DcBlockerCutoffHz"/>, which is an octave below
    /// the lowest fundamental in adult speech (a low male voice is ~80 Hz), so
    /// it removes offset and mains rumble and leaves the speech band alone. A
    /// DC offset is not harmless: it eats head-room, so it lowers the constant
    /// gain the peak normalizer is allowed to apply, and it biases every peak
    /// measurement taken from the file.
    /// </remarks>
    public bool DcBlockerEnabled { get; set; } = true;

    /// <summary>-3 dB point of the DC blocker, in Hz.</summary>
    public double DcBlockerCutoffHz { get; set; } = 20.0;

    // ---- Listening EQ (meeting file, microphone leg only) ----------------

    /// <summary>
    /// Applies one fixed high shelf to the microphone leg of the meeting mix,
    /// for a microphone that has the bandwidth but sounds duller than the
    /// PC-audio leg beside it in the same file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, and it should stay off until there is a reason. After
    /// every recording <see cref="Audio.SpectralBalance"/> measures both
    /// working-audio files and writes the comparison to the log; if the
    /// microphone's 4-7 kHz band really does sit well below the PC audio's, that
    /// measurement is the reason. Turning it on because a recording "sounds a
    /// bit flat" is not.
    /// </para>
    /// <para>
    /// It cannot help a device that never delivered the band. A 16 kHz endpoint
    /// has nothing above 8 kHz to lift, and the capture-format block in the log
    /// says so explicitly rather than letting an equalizer imply otherwise.
    /// </para>
    /// <para>
    /// It never touches the 16 kHz working audio, and never touches the PC-audio
    /// leg. See <see cref="Dsp.ListeningEq"/>.
    /// </para>
    /// </remarks>
    public bool MicrophoneListeningEqEnabled { get; set; }

    /// <summary>
    /// Corner frequency of the microphone listening shelf, in Hz.
    /// </summary>
    /// <remarks>
    /// 2.5 kHz. Consonant place-of-articulation cues start around 2 kHz and
    /// sibilant energy runs to 8 kHz, so a shelf hinged here lifts the whole
    /// region intelligibility is carried in while leaving the vowel formants -
    /// and therefore the speaker's voice - where they were. A corner up at
    /// 6 kHz would only add air; one down at 1 kHz would rebalance the voice
    /// itself.
    /// </remarks>
    public double MicrophoneListeningEqCornerHz { get; set; } = 2500.0;

    /// <summary>
    /// Tilt of the microphone listening shelf, in dB.
    /// </summary>
    /// <remarks>
    /// 4 dB, and deliberately at the small end. It is about the smallest tilt
    /// that is clearly audible on speech, and the measurement it answers is a
    /// difference of 6 dB or more, so it closes most of a real gap without
    /// overshooting a marginal one. Anything larger starts to sound thin, and
    /// nothing here is trying to make a meeting sound produced.
    /// </remarks>
    public double MicrophoneListeningEqGainDb { get; set; } = 4.0;

    // ---- Mixer -----------------------------------------------------------

    /// <summary>
    /// Constant gain applied to each leg before the two are summed into the
    /// saved file. -8.18 dB (a factor of 0.39) is the value that keeps the sum
    /// inside full scale for every bounded input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only reason the mixer needs any gain at all. It is one
    /// constant, identical for both legs and for the whole recording, so it
    /// scales the mix without changing the shape of anything in it. What it
    /// costs is given back once, by the peak normalizer, after the recording is
    /// closed and its true peak is known.
    /// </para>
    /// <para><b>Why 0.4 and not 0.5.</b> It was 0.5, on the reasoning that two
    /// full-scale legs then sum to exactly full scale. That reasoning assumed
    /// nothing ahead of the mixer could hand it a sample above full scale, which
    /// was true while the resampler interpolated linearly - a weighted average of
    /// two samples cannot leave their range. It stopped being true when the
    /// interpolation became cubic:
    /// <see cref="Dsp.Resampler.MaximumInterpolationGain"/> is 1.25, reachable by
    /// an ordinary loud signal turning around near Nyquist. Two such legs at 0.5
    /// would sum to 1.25 and be flattened by the writer's backstop clamp - a
    /// clipped meeting, produced by the stage whose entire job is to make
    /// clipping impossible.
    /// </para>
    /// <para>
    /// So the constant is derived from that bound rather than from an assumption
    /// about it. The arithmetic requirement is <c>1 / (2 x 1.25) = 0.4</c>, and
    /// the value used is a little under it, because the interpolator is not the
    /// last stage before the mixer: the DC blocker is, and a first-order
    /// high-pass has a gain of <c>2 / (1 + R)</c> at Nyquist, which at the 20 Hz
    /// corner is 1.0013. Two legs at exactly 0.4 would therefore reach 1.0013 and
    /// be clamped - by a thirteen-hundredth of a decibel, which is still a
    /// clipped meeting. 0.39 leaves the worst case at 0.976.
    /// </para>
    /// <para>
    /// <c>ResamplerHeadroomTests</c> measures the interpolator's bound and
    /// asserts this constant is inside it, so changing the interpolator without
    /// revisiting the head-room fails there rather than clipping a recording.
    /// </para>
    /// <para>
    /// The extra 2.16 dB is not lost. It is head-room during the recording only,
    /// and the peak normalizer measures the finished file and gives it straight
    /// back - so the delivered file is exactly as loud as it was before, and the
    /// only price is 2.16 dB more room above the 16-bit quantizer while the pump
    /// is writing, which is far below any microphone's own noise floor.
    /// </para>
    /// <para>
    /// One honest limit. This covers the stages' gain at and below Nyquist, not
    /// the DC blocker's true worst case over every bounded input, which is 2.0
    /// and is reached only by a full-scale step between the rails - a signal no
    /// acoustic source produces, and a property this filter had before any of
    /// this changed. The writer's clamp remains the backstop for that, as it
    /// always was.
    /// </para>
    /// </remarks>
    public double MixGainPerStreamDb { get; set; } = -8.1787;

    // ---- Peak normalization (applied once, after recording) --------------

    /// <summary>
    /// Whether a finished recording is scaled by a single constant gain so it
    /// is not uncomfortably quiet to listen back to.
    /// </summary>
    public bool PeakNormalizationEnabled { get; set; } = true;

    /// <summary>
    /// Peak the normalized file is aimed at, in dBFS.
    /// </summary>
    /// <remarks>
    /// -1 dBFS, i.e. one decibel of head-room below digital full scale. The
    /// sample peak of a PCM file is not the peak a converter or a lossy encoder
    /// reconstructs between samples; ITU-R BS.1770-4 / EBU R 128 answer exactly
    /// that with a -1 dBTP ceiling, and this is the same margin for the same
    /// reason. It also absorbs the rounding of the 16-bit quantizer, so
    /// <c>NormalizationNeverProducesASampleAtFullScale</c> holds.
    /// </remarks>
    public double NormalizationTargetPeakDbFs { get; set; } = -1.0;

    /// <summary>
    /// Largest constant gain the normalizer may apply, in dB.
    /// </summary>
    /// <remarks>
    /// +20 dB is a factor of ten. Beyond that the material being amplified is
    /// the capture noise floor rather than speech, and making noise ten times
    /// louder does not make a meeting easier to hear or to transcribe. The cap
    /// is a refusal to over-amplify, never a limiter: the gain stays constant,
    /// it is simply not allowed to be larger than this.
    /// </remarks>
    public double MaxNormalizationGainDb { get; set; } = 20.0;

    /// <summary>
    /// A gain change smaller than this is not worth rewriting a file for.
    /// </summary>
    /// <remarks>
    /// 0.1 dB is about 1.2%: inaudible, and below the point where re-quantizing
    /// to 16 bit could plausibly be a net gain. Skipping the rewrite also means
    /// the recording the pump wrote is the recording the user keeps, byte for
    /// byte.
    /// </remarks>
    public double NormalizationSkipThresholdDb { get; set; } = 0.1;

    /// <summary>
    /// Sample magnitude at or above which the captured audio is reported as
    /// already clipped.
    /// </summary>
    /// <remarks>
    /// A 16-bit sample at or beyond full scale means the signal was clipped
    /// before this application saw it - by the input gain of the sound card, or
    /// inside the application that was playing. No amount of later processing
    /// recovers what was flattened, so this is reported and never "repaired".
    /// 0.999 rather than 1.0 because a full-scale sample decodes to 32767/32768.
    /// </remarks>
    public double ClipDetectionThreshold { get; set; } = 0.999;

    /// <summary>
    /// Fraction of samples that must be at or beyond
    /// <see cref="ClipDetectionThreshold"/> before the recording is called
    /// clipped.
    /// </summary>
    /// <remarks>
    /// A handful of samples touching full scale in an hour of audio is a
    /// coincidence, not distortion. 0.0001 (one sample in ten thousand, which
    /// is 3600 x 0.0001 = 0.36 seconds' worth per hour at any sample rate) is
    /// high enough not to cry wolf and low enough to catch a genuinely
    /// over-driven input.
    /// </remarks>
    public double ClipDetectionSampleFraction { get; set; } = 0.0001;

    public AudioProcessingSettings Clone() => (AudioProcessingSettings)MemberwiseClone();
}
