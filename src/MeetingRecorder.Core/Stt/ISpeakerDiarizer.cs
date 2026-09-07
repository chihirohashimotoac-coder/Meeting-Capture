using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Stt;

/// <summary>
/// Assigns an anonymous speaker cluster to a stretch of audio.
/// </summary>
/// <remarks>
/// <para>
/// Clusters are per capture stream and are always anonymous: "Speaker 1",
/// "Speaker 2". The implementation must never invent a human name - the
/// requirements are explicit that a real person's name may only ever appear
/// because a user typed it in.
/// </para>
/// <para>
/// The interface is deliberately incremental so diarization can run on the same
/// 16 kHz chunk the recognizer just processed. Nothing extra is stored on disk
/// and no second pass over the meeting audio is needed.
/// </para>
/// </remarks>
public interface ISpeakerDiarizer : IDisposable
{
    /// <summary>Whether the diarizer is currently active (it can be degraded off at run time).</summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Returns the cluster id for a stretch of 16 kHz mono audio, or null when
    /// the audio is too short or too quiet to attribute.
    /// </summary>
    int? Identify(AudioSourceKind source, ReadOnlySpan<float> samples16k);

    /// <summary>Clears all learned clusters (called between meetings).</summary>
    void Reset();
}
