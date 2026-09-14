using System.Globalization;
using System.Text;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Stt;

/// <summary>
/// Where a transcription's wall-clock time went.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "transcription is slow" is not a finding, it is a
/// feeling. A ten minute meeting that takes ten minutes to transcribe could be
/// the encoder, the beam search, the temperature fallback, speaker clustering,
/// or simply two streams' worth of audio being read end to end - and every one
/// of those has a different answer. Without the breakdown, tuning is guessing
/// with a stopwatch.
/// </para>
/// <para>
/// Nothing here is estimated. Every duration is a stopwatch around the thing it
/// names, every count is counted, and the fields that could only be filled in
/// by the caller (how long the model took to load, which model it was) are left
/// for the caller to fill in rather than approximated.
/// </para>
/// <para>
/// It goes to the local log, like everything else this application measures.
/// No transcript text appears in it - only shapes and durations.
/// </para>
/// </remarks>
public sealed class TranscriptionMetrics
{
    /// <summary>Length of the meeting itself, in seconds. The denominator of the RTF.</summary>
    /// <remarks>
    /// The longest single source, not the sum of them: the microphone file and
    /// the loopback file both span the whole meeting, so adding them would
    /// report a ten minute meeting as twenty and halve every ratio derived from
    /// it.
    /// </remarks>
    public double MeetingDurationSeconds { get; set; }

    /// <summary>Duration of each working-audio file that was read, in seconds.</summary>
    public Dictionary<AudioSourceKind, double> SourceDurationSeconds { get; } = new();

    /// <summary>Audio the detector classified as speech, across every source, in seconds.</summary>
    public double SpeechSecondsDetected { get; set; }

    /// <summary>Windows handed to the recognizer.</summary>
    public int Chunks { get; set; }

    /// <summary>Audio inside those windows, in seconds. What the engine was actually asked to read.</summary>
    /// <remarks>
    /// The number that decides how long a transcription takes, and the one a
    /// reader is most likely to be surprised by: whisper pads every window out
    /// to its own 30 second receptive field before the encoder runs, so
    /// <see cref="EncoderWindowSeconds"/> beside it says what the engine really
    /// spent its time on.
    /// </remarks>
    public double SubmittedAudioSeconds { get; set; }

    /// <summary>Whisper's 30 second receptive field, for the padding arithmetic below.</summary>
    public const double WhisperWindowSeconds = 30.0;

    /// <summary>
    /// Audio-equivalent the encoder ran on: every window costs a full 30 seconds
    /// however short it is.
    /// </summary>
    public double EncoderWindowSeconds => Chunks * WhisperWindowSeconds;

    /// <summary>How long the model file took to load, when the caller measured it.</summary>
    public TimeSpan ModelLoad { get; set; }

    /// <summary>Time inside <see cref="ISpeechRecognizer.Transcribe"/>, summed.</summary>
    public TimeSpan WhisperInference { get; set; }

    /// <summary>Time inside the speaker clusterer, summed.</summary>
    public TimeSpan Diarization { get; set; }

    /// <summary>Sorting, speaker-name carry-over and the rest of the post-processing.</summary>
    public TimeSpan Formatting { get; set; }

    /// <summary>Wall clock for the whole pass, excluding the model load.</summary>
    public TimeSpan Total { get; set; }

    /// <summary>Wall clock the user experienced: the model load plus the pass.</summary>
    public TimeSpan TotalIncludingModelLoad => ModelLoad + Total;

    public string ModelId { get; set; } = string.Empty;

    public TranscriptionProfile Profile { get; set; } = TranscriptionProfile.Balanced;

    public int BeamSize { get; set; }

    public float TemperatureIncrement { get; set; }

    public int Threads { get; set; }

    public bool DiarizationEnabled { get; set; }

    public int Segments { get; set; }

    /// <summary>
    /// Processing time as a multiple of the meeting's length. 1.0 means a ten
    /// minute meeting took ten minutes.
    /// </summary>
    public double RealTimeFactor => MeetingDurationSeconds <= 0
        ? 0
        : TotalIncludingModelLoad.TotalSeconds / MeetingDurationSeconds;

    /// <summary>Records how long one source's file was, and grows the meeting duration to match.</summary>
    public void NoteSource(AudioSourceKind kind, double seconds)
    {
        SourceDurationSeconds[kind] = seconds;
        if (seconds > MeetingDurationSeconds)
        {
            MeetingDurationSeconds = seconds;
        }
    }

    public string ToLogBlock()
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;

        sb.AppendLine("Transcription performance:");
        sb.AppendLine(c, $"  Meeting duration      = {MeetingDurationSeconds:F1} s");

        foreach (var (kind, seconds) in SourceDurationSeconds.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            sb.AppendLine(c, $"    {kind,-18}= {seconds:F1} s");
        }

        sb.AppendLine(c, $"  Speech detected       = {SpeechSecondsDetected:F1} s");
        sb.AppendLine(c, $"  Speech audio submitted= {SubmittedAudioSeconds:F1} s");
        sb.AppendLine(c, $"  Chunks                = {Chunks} (encoder ran {EncoderWindowSeconds:F0} s of padded window)");
        sb.AppendLine(c, $"  Segments              = {Segments}");
        sb.AppendLine(c, $"  Profile               = {Profile}");
        sb.AppendLine(c, $"  Model                 = {ModelId}");
        sb.AppendLine(c, $"  Beam                  = {BeamSize}");
        sb.AppendLine(c, $"  Temperature increment = {TemperatureIncrement:F2}");
        sb.AppendLine(c, $"  Threads               = {Threads}");
        sb.AppendLine(c, $"  Diarization           = {(DiarizationEnabled ? "on" : "off")}");
        sb.AppendLine(c, $"  Model load            = {ModelLoad.TotalSeconds:F2} s");
        sb.AppendLine(c, $"  Whisper inference     = {WhisperInference.TotalSeconds:F2} s");
        sb.AppendLine(c, $"  Diarization time      = {Diarization.TotalSeconds:F2} s");
        sb.AppendLine(c, $"  Transcript formatting = {Formatting.TotalSeconds:F3} s");
        sb.AppendLine(c, $"  Total                 = {TotalIncludingModelLoad.TotalSeconds:F2} s");
        sb.Append(c, $"  RTF                   = {RealTimeFactor:F2} x meeting duration");

        return sb.ToString();
    }
}
