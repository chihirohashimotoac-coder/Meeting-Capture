using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Pipeline;
using Xunit;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// What a meeting costs on disk, and whether the application tells the truth
/// about it.
/// </summary>
public class RecordingFootprintTests
{
    private static AppSettings Settings() => new()
    {
        SaveRoot = Path.GetTempPath(),
        SttEnabled = true,
        KeepRecognitionAudio = true,
        DeleteRecognitionAudioAfterTranscription = true,
    };

    [Fact]
    public void TheDefaultFormatDoesNotQuietlyFillTheDrive()
    {
        // WAV is four times the size for audio that exists to be listened back
        // to and transcribed, and the transcription does not read this file at
        // all - it reads the 16 kHz working audio, which is always PCM.
        var settings = new AppSettings();

        Assert.Equal(RecordingFormat.Mp3, settings.Format);
        Assert.Equal(192, settings.Mp3BitrateKbps);
    }

    [Fact]
    public void TheHourlyFiguresAreTheOnesTheWritersActuallyProduce()
    {
        var settings = Settings();

        // 192 kbps = 24,000 B/s -> 86.4 MB/hour.
        settings.Format = RecordingFormat.Mp3;
        Assert.Equal(86.4, RecordingFootprint.AudioMegabytesPerHour(settings), 1);

        // 48 kHz x 16 bit x mono = 96,000 B/s -> 345.6 MB/hour.
        settings.Format = RecordingFormat.Wav;
        Assert.Equal(345.6, RecordingFootprint.AudioMegabytesPerHour(settings), 1);

        // Two streams of 16 kHz x 16 bit -> 230.4 MB/hour.
        Assert.Equal(230.4, RecordingFootprint.RecognitionMegabytesPerHour(settings), 1);
    }

    /// <summary>
    /// The check that used to be wrong: the drive has to carry the working audio
    /// as well, and quoting the meeting file alone promised 1.67x the hours it
    /// could really take.
    /// </summary>
    [Fact]
    public void TheRecordingFootprintCountsEveryFileThatIsBeingWritten()
    {
        var settings = Settings();

        // 96,000 + 2 x 32,000 = 160,000 B/s -> 576 MB/hour.
        Assert.Equal(576.0, RecordingFootprint.RecordingMegabytesPerHour(settings), 1);

        settings.KeepRecognitionAudio = false;
        Assert.Equal(345.6, RecordingFootprint.RecordingMegabytesPerHour(settings), 1);
    }

    /// <summary>
    /// Choosing MP3 does not reduce what the drive needs during the meeting: the
    /// audio is always captured to WAV so that a crash leaves a playable file,
    /// and converted only once it is closed.
    /// </summary>
    [Fact]
    public void Mp3DoesNotShrinkWhatTheRecordingItselfNeeds()
    {
        var wav = Settings();
        wav.Format = RecordingFormat.Wav;

        var mp3 = Settings();
        mp3.Format = RecordingFormat.Mp3;

        Assert.Equal(
            RecordingFootprint.RecordingMegabytesPerHour(wav),
            RecordingFootprint.RecordingMegabytesPerHour(mp3),
            3);

        Assert.True(
            RecordingFootprint.RestingMegabytesPerHour(mp3)
            < RecordingFootprint.RestingMegabytesPerHour(wav),
            "MP3 must at least shrink what the meeting leaves behind");
    }

    [Fact]
    public void ATranscribedMeetingCostsOnlyItsAudioWhenTheWorkingCopyIsDeleted()
    {
        var settings = Settings();
        settings.Format = RecordingFormat.Mp3;

        Assert.Equal(86.4, RecordingFootprint.TranscribedMegabytesPerHour(settings), 1);

        settings.DeleteRecognitionAudioAfterTranscription = false;
        Assert.Equal(86.4 + 230.4, RecordingFootprint.TranscribedMegabytesPerHour(settings), 1);
    }

    [Fact]
    public void HoursThatFitUsesTheRecordingRateRatherThanTheRestingOne()
    {
        var settings = Settings();

        // 1.6 GB at 160,000 B/s is 10,000 seconds, which is 2.78 hours.
        var hours = RecordingFootprint.HoursThatFit(settings, 1_600_000_000L);
        Assert.Equal(2.78, hours, 2);
    }

    [Fact]
    public void TheDescriptionNamesBothTheRestingSizeAndThePeak()
    {
        var text = RecordingFootprint.Describe(Settings());

        Assert.Contains("作業音声", text, StringComparison.Ordinal);
        Assert.Contains("録音中", text, StringComparison.Ordinal);
        Assert.Contains("文字起こし後", text, StringComparison.Ordinal);
    }
}
