using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Stt;

namespace MeetingRecorder.Core.Pipeline;

/// <summary>
/// The working audio a meeting folder keeps so it can be transcribed later.
/// </summary>
/// <remarks>
/// <para>
/// For a recording made by this application there are two files, one per
/// capture stream, written at the engine's own 16 kHz while the meeting runs.
/// They are what makes <see cref="TranscriptSegment.Source"/> a fact rather
/// than a guess, and they cannot be reconstructed afterwards: once the two legs
/// are summed into the meeting file, which words came from the room and which
/// came out of the speakers is gone for good.
/// </para>
/// <para>
/// For an imported file there is one, decoded from the user's own file. It
/// carries no source information at all, and nothing here invents any.
/// </para>
/// </remarks>
public static class RecognitionAudioNames
{
    public const string Microphone = "recognition-mic.wav";

    public const string SystemAudio = "recognition-system.wav";

    public const string Imported = "recognition-import.wav";

    /// <summary>Roughly how much one stream of this working audio costs per hour.</summary>
    /// <remarks>16 kHz, 16-bit, mono = 32 kB/s = 115.2 MB/hour.</remarks>
    public const int MegabytesPerStreamPerHour = 115;

    public static IReadOnlyList<string> All { get; } = new[] { Microphone, SystemAudio, Imported };

    /// <summary>
    /// The sources <see cref="OfflineTranscriptionService"/> should be given for
    /// a meeting folder: whichever working files are actually there.
    /// </summary>
    public static IReadOnlyList<TranscriptionSource> SourcesIn(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Array.Empty<TranscriptionSource>();
        }

        var candidates = new (string Name, AudioSourceKind Kind)[]
        {
            (Microphone, AudioSourceKind.Microphone),
            (SystemAudio, AudioSourceKind.SystemAudio),
            (Imported, AudioSourceKind.Imported),
        };

        return candidates
            .Select(c => (Path: Path.Combine(directory!, c.Name), c.Kind))
            .Where(c => File.Exists(c.Path))
            .Select(c => new TranscriptionSource(c.Path, c.Kind))
            .ToList();
    }

    /// <summary>True when a meeting folder holds working audio worth transcribing.</summary>
    public static bool ExistIn(string? directory) => SourcesIn(directory).Count > 0;

    /// <summary>True when the folder holds per-stream audio, i.e. this application recorded it.</summary>
    public static bool HasPerStreamAudio(string? directory)
        => !string.IsNullOrWhiteSpace(directory)
           && (File.Exists(Path.Combine(directory!, Microphone)) || File.Exists(Path.Combine(directory!, SystemAudio)));
}
