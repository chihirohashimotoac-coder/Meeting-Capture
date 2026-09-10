using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.Core.Pipeline;

/// <summary>
/// One finished meeting on disk - recorded here or imported - and everything
/// the rest of the application needs to know about it.
/// </summary>
/// <param name="Folder">Where the meeting was written.</param>
/// <param name="Metadata">Metadata as saved. Mutable; call <see cref="Save"/> after changing it.</param>
/// <param name="AudioPath">The audio the user keeps and can play.</param>
/// <param name="Warnings">Anything the user should know about this meeting.</param>
/// <param name="RecognitionAudioDirectory">
/// Where the 16 kHz working audio lives, or null when none was kept. This is
/// what a transcription reads.
/// </param>
public sealed record MeetingSummary(
    MeetingFolder Folder,
    MeetingMetadata Metadata,
    string AudioPath,
    IReadOnlyList<string> Warnings,
    string? RecognitionAudioDirectory = null)
{
    /// <summary>True when this meeting was imported rather than recorded here.</summary>
    public bool IsImported => Metadata.IsImported;

    /// <summary>
    /// True when the working audio is still present, so a transcription can be
    /// run (or run again with a different model).
    /// </summary>
    public bool CanTranscribe => RecognitionAudioNames.ExistIn(RecognitionAudioDirectory ?? Folder.Path);

    /// <summary>
    /// True when the working audio is per capture stream, so "microphone" and
    /// "PC audio" are recorded facts and speaker naming makes sense. False for
    /// an imported file, which is one anonymous source.
    /// </summary>
    public bool HasSourceAttribution => RecognitionAudioNames.HasPerStreamAudio(RecognitionAudioDirectory ?? Folder.Path);

    /// <summary>The 16 kHz audio a transcription should read.</summary>
    public IReadOnlyList<Stt.TranscriptionSource> TranscriptionSources
        => RecognitionAudioNames.SourcesIn(RecognitionAudioDirectory ?? Folder.Path);

    /// <summary>Writes transcript.txt and transcript.md, plus metadata.json.</summary>
    public void SaveTranscript(IReadOnlyList<TranscriptSegment> segments, IReadOnlyDictionary<string, string> speakerNames)
    {
        Metadata.SpeakerNames = new Dictionary<string, string>(speakerNames);
        TranscriptExporter.Write(Folder, Metadata, segments);
        Folder.WriteMetadata(Metadata);
    }

    public void Save() => Folder.WriteMetadata(Metadata);
}
