namespace MeetingRecorder.Core.Models;

/// <summary>User-selectable container for the delivered meeting audio.</summary>
public enum RecordingFormat
{
    /// <summary>16-bit PCM WAV. Always written first (see AudioFileWriterPolicy).</summary>
    Wav = 0,

    /// <summary>MP3, transcoded from the crash-safe WAV once recording stops.</summary>
    Mp3 = 1,
}
