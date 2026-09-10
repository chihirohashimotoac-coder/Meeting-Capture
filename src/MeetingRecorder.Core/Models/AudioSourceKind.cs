namespace MeetingRecorder.Core.Models;

/// <summary>
/// Which physical capture stream a piece of audio (or a transcript segment)
/// came from.
/// </summary>
/// <remarks>
/// This value is never inferred by AI. It is stamped by the capture layer at
/// the moment the samples are produced, so "who spoke into the room mic" versus
/// "what came out of the PC speakers" is always ground truth.
/// </remarks>
public enum AudioSourceKind
{
    /// <summary>Local microphone (people physically in the room).</summary>
    Microphone = 0,

    /// <summary>WASAPI loopback of the Windows render device (remote participants).</summary>
    SystemAudio = 1,

    /// <summary>The mixed meeting track that is written to disk.</summary>
    Mixed = 2,

    /// <summary>
    /// An audio file the user imported. One file, one source: where it came
    /// from and who is speaking in it are not recorded anywhere, so nothing
    /// here guesses.
    /// </summary>
    Imported = 3,
}

public static class AudioSourceKindExtensions
{
    /// <summary>Label shown in the transcript UI and in exported files.</summary>
    public static string ToDisplayLabel(this AudioSourceKind kind) => kind switch
    {
        AudioSourceKind.Microphone => "マイク",
        AudioSourceKind.SystemAudio => "PC音声",
        AudioSourceKind.Mixed => "ミックス",
        AudioSourceKind.Imported => "インポート音声",
        _ => "不明",
    };
}
