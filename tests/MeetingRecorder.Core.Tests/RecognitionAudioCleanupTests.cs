using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Stt;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// The setting that deletes the working audio, and the three ways a
/// transcription can end.
/// </summary>
/// <remarks>
/// Deleting after a run that did not finish is the dangerous case: for a
/// recording, the per-stream audio cannot be produced again from anything, so a
/// cancelled transcription that cleaned up after itself would quietly cost the
/// user the ability to try a second time.
/// </remarks>
public sealed class RecognitionAudioCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-cleanup-" + Guid.NewGuid().ToString("N"));

    public RecognitionAudioCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string CreateWorkingAudio()
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        foreach (var name in new[] { RecognitionAudioNames.Microphone, RecognitionAudioNames.SystemAudio })
        {
            using var writer = new WavFileWriter(Path.Combine(directory, name), SpeechConstants.SampleRate);
            writer.Write(SignalGenerator.Sine(300, 0.5, SpeechConstants.SampleRate, 0.3));
            writer.Flush();
        }

        return directory;
    }

    [Fact]
    public void SettingOnAndTheRunCompleted_TheWorkingAudioIsDeleted()
    {
        var directory = CreateWorkingAudio();

        var deleted = RecognitionAudioCleanup.DeleteIfAppropriate(
            directory, TranscriptionOutcome.Completed, deleteAfterTranscription: true);

        Assert.Equal(2, deleted.Count);
        Assert.False(RecognitionAudioNames.ExistIn(directory));
    }

    [Fact]
    public void SettingOffAndTheRunCompleted_TheWorkingAudioIsKept()
    {
        var directory = CreateWorkingAudio();

        var deleted = RecognitionAudioCleanup.DeleteIfAppropriate(
            directory, TranscriptionOutcome.Completed, deleteAfterTranscription: false);

        Assert.Empty(deleted);
        Assert.True(RecognitionAudioNames.ExistIn(directory));
    }

    [Theory]
    [InlineData(TranscriptionOutcome.Cancelled)]
    [InlineData(TranscriptionOutcome.Failed)]
    public void TheRunDidNotFinish_TheWorkingAudioIsKeptEvenWithTheSettingOn(TranscriptionOutcome outcome)
    {
        var directory = CreateWorkingAudio();

        var deleted = RecognitionAudioCleanup.DeleteIfAppropriate(
            directory, outcome, deleteAfterTranscription: true);

        Assert.Empty(deleted);
        Assert.True(RecognitionAudioNames.ExistIn(directory), $"a {outcome} run must leave its input alone");
    }

    [Theory]
    [InlineData(TranscriptionOutcome.Completed, true, true)]
    [InlineData(TranscriptionOutcome.Completed, false, false)]
    [InlineData(TranscriptionOutcome.Cancelled, true, false)]
    [InlineData(TranscriptionOutcome.Cancelled, false, false)]
    [InlineData(TranscriptionOutcome.Failed, true, false)]
    [InlineData(TranscriptionOutcome.Failed, false, false)]
    public void ThePolicyIsExactlyCompletedAndOptedIn(TranscriptionOutcome outcome, bool setting, bool expected)
    {
        Assert.Equal(expected, RecognitionAudioCleanup.ShouldDelete(outcome, setting));
    }

    [Fact]
    public void TheMeetingRecordingItselfIsNeverTouched()
    {
        var directory = CreateWorkingAudio();
        var recording = Path.Combine(directory, "meeting.wav");
        using (var writer = new WavFileWriter(recording, 48000))
        {
            writer.Write(SignalGenerator.Sine(300, 0.5, 48000, 0.3));
            writer.Flush();
        }

        RecognitionAudioCleanup.DeleteIfAppropriate(directory, TranscriptionOutcome.Completed, true);

        Assert.True(File.Exists(recording), "the recording is not working audio and must survive the cleanup");
    }
}
