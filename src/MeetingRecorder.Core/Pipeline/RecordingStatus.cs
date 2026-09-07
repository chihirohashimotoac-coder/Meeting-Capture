using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Stt;

namespace MeetingRecorder.Core.Pipeline;

public enum RecordingState
{
    Idle,
    Preparing,
    Recording,
    Stopping,
    Faulted,
}

/// <summary>Everything the main window needs, sampled once per UI tick.</summary>
/// <param name="State">Current pipeline state.</param>
/// <param name="Elapsed">Recorded length so far.</param>
/// <param name="MicLevel">Raw microphone level (before processing) for the meter.</param>
/// <param name="SystemLevel">Raw system-audio level (before processing) for the meter.</param>
/// <param name="MicGainDb">Gain the normalizer currently applies to the microphone.</param>
/// <param name="SystemGainDb">Gain the normalizer currently applies to system audio.</param>
/// <param name="Stt">Transcription throughput and lateness.</param>
/// <param name="MicSilenceSeconds">How long the microphone has been effectively silent.</param>
/// <param name="SystemSilenceSeconds">How long system audio has been effectively silent.</param>
/// <param name="BufferOverruns">Times a ring buffer had to discard audio.</param>
/// <param name="SilenceInsertedMs">Silence inserted to cover a starved stream.</param>
/// <param name="DriftCorrectedMs">Audio dropped to keep the two clocks aligned.</param>
/// <param name="BytesWritten">Size of the audio file so far.</param>
public readonly record struct RecordingStatus(
    RecordingState State,
    TimeSpan Elapsed,
    LevelSnapshot MicLevel,
    LevelSnapshot SystemLevel,
    double MicGainDb,
    double SystemGainDb,
    SttStatus Stt,
    double MicSilenceSeconds,
    double SystemSilenceSeconds,
    long BufferOverruns,
    double SilenceInsertedMs,
    double DriftCorrectedMs,
    long BytesWritten);
