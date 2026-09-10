using MeetingRecorder.Core.Diagnostics;

namespace MeetingRecorder.Core.Pipeline;

/// <summary>How a transcription ended.</summary>
public enum TranscriptionOutcome
{
    /// <summary>It ran to the end and produced a transcript.</summary>
    Completed,

    /// <summary>The user stopped it.</summary>
    Cancelled,

    /// <summary>It threw: no model, no memory, a bad file.</summary>
    Failed,
}

/// <summary>
/// Decides whether the working audio may be deleted once a transcription is
/// over, and deletes it.
/// </summary>
/// <remarks>
/// <para>
/// The rule is one sentence: only a transcription that completed may delete its
/// own input. It is worth its own type because the failure mode is silent and
/// permanent - a cancelled run that tidied up after itself would leave the user
/// unable to try again, with nothing on screen explaining why, and for a
/// recording the audio cannot be reconstructed.
/// </para>
/// <para>
/// Note what is never deleted here: the meeting recording itself. This only ever
/// touches the 16 kHz working copies.
/// </para>
/// </remarks>
public static class RecognitionAudioCleanup
{
    /// <summary>
    /// True only when the user asked for the cleanup <i>and</i> the
    /// transcription actually finished.
    /// </summary>
    public static bool ShouldDelete(TranscriptionOutcome outcome, bool deleteAfterTranscription)
        => deleteAfterTranscription && outcome == TranscriptionOutcome.Completed;

    /// <summary>
    /// Removes the working audio when <see cref="ShouldDelete"/> allows it, and
    /// returns the files that went.
    /// </summary>
    public static IReadOnlyList<string> DeleteIfAppropriate(
        string? directory,
        TranscriptionOutcome outcome,
        bool deleteAfterTranscription,
        ILogger? logger = null)
    {
        if (!ShouldDelete(outcome, deleteAfterTranscription) || string.IsNullOrWhiteSpace(directory))
        {
            return Array.Empty<string>();
        }

        var log = logger ?? NullLogger.Instance;
        var deleted = new List<string>();

        foreach (var name in RecognitionAudioNames.All)
        {
            var path = Path.Combine(directory!, name);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted.Add(path);
                }
            }
            catch (IOException ex)
            {
                // Leaving a few hundred megabytes behind is untidy, not harmful.
                log.Warn(nameof(RecognitionAudioCleanup), $"Could not delete '{name}': {ex.Message}");
            }
        }

        if (deleted.Count > 0)
        {
            log.Info(nameof(RecognitionAudioCleanup), $"Removed {deleted.Count} working audio file(s) after a completed transcription.");
        }

        return deleted;
    }
}
