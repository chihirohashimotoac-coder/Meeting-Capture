using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Stt;

namespace MeetingRecorder.Core.Pipeline;

/// <summary>
/// What an hour of meeting actually costs on disk, under the settings in force.
/// </summary>
/// <remarks>
/// <para>
/// One place, because the figure had drifted into four documents and two dialogs
/// and disagreed with the code in all of them. It is computed from the same
/// constants the writers use rather than written down again.
/// </para>
/// <para>
/// <b>Two figures, not one.</b> The peak is what the recording needs while it
/// runs; the resting size is what is left afterwards. They differ because the
/// meeting is always captured to WAV - a half-written WAV with a self-healing
/// header survives a crash and a half-written MP3 frame stream does not - and is
/// only converted once the audio is safely closed. So choosing MP3 does not
/// reduce what the drive must have free during the meeting. It reduces what the
/// meeting leaves behind.
/// </para>
/// </remarks>
public static class RecordingFootprint
{
    /// <summary>48 kHz, 16-bit, mono.</summary>
    private const double MeetingWavBytesPerSecond = 48000 * 2;

    /// <summary>16 kHz, 16-bit, mono, per stream.</summary>
    private const double RecognitionBytesPerSecond = SpeechConstants.SampleRate * 2;

    /// <summary>Microphone and system audio are kept separately.</summary>
    private const int RecognitionStreams = 2;

    private const double SecondsPerHour = 3600.0;

    private const double BytesPerMegabyte = 1_000_000.0;

    /// <summary>True when this configuration writes the per-stream working audio.</summary>
    public static bool KeepsRecognitionAudio(AppSettings settings)
        => settings.SttEnabled && settings.KeepRecognitionAudio;

    /// <summary>
    /// Bytes per second written while recording - the number that decides
    /// whether the drive can survive the meeting.
    /// </summary>
    public static double RecordingBytesPerSecond(AppSettings settings)
    {
        var total = MeetingWavBytesPerSecond;
        if (KeepsRecognitionAudio(settings))
        {
            total += RecognitionBytesPerSecond * RecognitionStreams;
        }

        return total;
    }

    /// <summary>Megabytes per hour written while recording.</summary>
    public static double RecordingMegabytesPerHour(AppSettings settings)
        => RecordingBytesPerSecond(settings) * SecondsPerHour / BytesPerMegabyte;

    /// <summary>Megabytes per hour the finished meeting audio occupies.</summary>
    public static double AudioMegabytesPerHour(AppSettings settings)
        => settings.Format == RecordingFormat.Mp3
            ? settings.Mp3BitrateKbps * 1000.0 / 8.0 * SecondsPerHour / BytesPerMegabyte
            : MeetingWavBytesPerSecond * SecondsPerHour / BytesPerMegabyte;

    /// <summary>Megabytes per hour the per-stream working audio occupies.</summary>
    public static double RecognitionMegabytesPerHour(AppSettings settings)
        => KeepsRecognitionAudio(settings)
            ? RecognitionBytesPerSecond * RecognitionStreams * SecondsPerHour / BytesPerMegabyte
            : 0.0;

    /// <summary>
    /// Megabytes per hour left on disk once the meeting is finished but before
    /// it has been transcribed.
    /// </summary>
    public static double RestingMegabytesPerHour(AppSettings settings)
        => AudioMegabytesPerHour(settings) + RecognitionMegabytesPerHour(settings);

    /// <summary>
    /// Megabytes per hour left once the meeting has been transcribed, taking the
    /// working-audio deletion setting into account.
    /// </summary>
    public static double TranscribedMegabytesPerHour(AppSettings settings)
        => AudioMegabytesPerHour(settings)
           + (settings.DeleteRecognitionAudioAfterTranscription
               ? 0.0
               : RecognitionMegabytesPerHour(settings));

    /// <summary>Hours of recording that will fit in <paramref name="freeBytes"/>.</summary>
    public static double HoursThatFit(AppSettings settings, long freeBytes)
        => freeBytes / RecordingBytesPerSecond(settings) / SecondsPerHour;

    /// <summary>
    /// One sentence for a settings screen: what this configuration costs per
    /// hour, and what is left after a transcription.
    /// </summary>
    public static string Describe(AppSettings settings)
    {
        var format = settings.Format == RecordingFormat.Mp3
            ? $"MP3 {settings.Mp3BitrateKbps} kbps"
            : "WAV（無圧縮）";

        var audio = AudioMegabytesPerHour(settings);
        var working = RecognitionMegabytesPerHour(settings);
        var peak = RecordingMegabytesPerHour(settings);

        if (working <= 0)
        {
            return $"1時間の会議あたり 約{audio:F0}MB（{format}）。録音中は一時的に 約{peak:F0}MB/時 を使います。";
        }

        var after = TranscribedMegabytesPerHour(settings);
        return
            $"1時間の会議あたり 約{audio + working:F0}MB"
            + $"（音声 {format} 約{audio:F0}MB ＋ 文字起こし用の作業音声 約{working:F0}MB）。"
            + $"文字起こし後は 約{after:F0}MB になります。"
            + $"録音中は一時的に 約{peak:F0}MB/時 を使います。";
    }
}
