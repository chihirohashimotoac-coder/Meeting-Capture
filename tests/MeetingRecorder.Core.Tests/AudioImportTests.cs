using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Stt;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// Covers importing an audio file the user already has.
/// </summary>
/// <remarks>
/// WAV is exercised end to end here because this project's own reader handles
/// it, so the behaviour is the same on every machine. MP3, M4A and AAC go
/// through codecs that belong to Windows and are covered in
/// MeetingRecorder.Audio.Tests, where a real decoder exists to be honest about.
/// </remarks>
public sealed class AudioImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-import-" + Guid.NewGuid().ToString("N"));

    public AudioImportTests() => Directory.CreateDirectory(_root);

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

    private string WriteSourceWav(string name = "meeting.wav", int sampleRate = 44100, double amplitude = 0.4)
    {
        var path = Path.Combine(_root, name);
        using var writer = new WavFileWriter(path, sampleRate);

        for (var i = 0; i < 3; i++)
        {
            writer.Write(SignalGenerator.Sine(300, 1.5, sampleRate, amplitude));
            writer.Write(SignalGenerator.Silence(1.0, sampleRate));
        }

        writer.Flush();
        return path;
    }

    private static AppSettings Settings(string saveRoot) => new()
    {
        SaveRoot = saveRoot,
        SttEnabled = true,
        KeepRecognitionAudio = true,
    };

    private static MeetingImporter Importer()
        => new(new AudioImportService(new WavAudioDecoder()));

    [Fact]
    public void ImportingProducesSixteenKilohertzMonoWorkingAudio()
    {
        var source = WriteSourceWav(sampleRate: 44100);
        var summary = Importer().Import(Settings(Path.Combine(_root, "out")), source);

        Assert.True(summary.IsImported);
        Assert.True(summary.CanTranscribe);

        var working = Path.Combine(summary.Folder.Path, RecognitionAudioNames.Imported);
        Assert.True(File.Exists(working));

        using var reader = new WavFileReader(working);
        Assert.Equal(SpeechConstants.SampleRate, reader.SampleRate);
        Assert.Equal(1, reader.Channels);
        Assert.InRange(reader.DurationSeconds, 7.0, 8.0);
        Assert.True(AudioMath.Rms(reader.ReadAllMono()) > 0.05, "the decoded audio is essentially silent");
    }

    [Fact]
    public void TheImportedFileIsNeverModifiedMovedOrDeleted()
    {
        // The user's own file is the one copy that matters. Everything this
        // application does with it is a read.
        var source = WriteSourceWav();
        var before = File.ReadAllBytes(source);
        var writtenAt = File.GetLastWriteTimeUtc(source);

        var summary = Importer().Import(Settings(Path.Combine(_root, "out")), source);

        Assert.True(File.Exists(source), "the source file was moved or deleted");
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(source));

        // And the working copy is somewhere else entirely.
        Assert.NotEqual(
            Path.GetFullPath(source),
            Path.GetFullPath(Path.Combine(summary.Folder.Path, RecognitionAudioNames.Imported)));
    }

    [Fact]
    public void TheWorkingAudioIsNotGainedFilteredOrGated()
    {
        // Whatever the decoder produced is what the recognizer sees. Only the
        // down-mix and the rate conversion happen, and both are linear.
        const double amplitude = 0.4;
        var source = WriteSourceWav(sampleRate: 48000, amplitude: amplitude);

        var summary = Importer().Import(Settings(Path.Combine(_root, "out")), source);

        using var reader = new WavFileReader(Path.Combine(summary.Folder.Path, RecognitionAudioNames.Imported));
        var samples = reader.ReadAllMono();

        // Skip the resampler warm-up, then measure inside the first tone.
        var rms = AudioMath.Rms(samples.AsSpan(2000, 16000));
        Assert.InRange(rms, amplitude / Math.Sqrt(2) * 0.9, amplitude / Math.Sqrt(2) * 1.1);

        // The silence between utterances is still silence: a gate would have
        // made it quieter, an AGC louder.
        var gap = samples.AsSpan((int)(SpeechConstants.SampleRate * 2.0), (int)(SpeechConstants.SampleRate * 0.4));
        Assert.True(AudioMath.Peak(gap) < 0.001);
    }

    [Fact]
    public void AnImportedMeetingHasNoSourceAttributionAndOffersNoSpeakers()
    {
        // One file, one anonymous source. There is nothing in it that says who
        // was speaking, so nothing here claims to know.
        var summary = Importer().Import(Settings(Path.Combine(_root, "out")), WriteSourceWav());

        Assert.False(summary.HasSourceAttribution);

        var sources = summary.TranscriptionSources;
        Assert.Single(sources);
        Assert.Equal(AudioSourceKind.Imported, sources[0].Source);

        // Not "microphone", not "Speaker 1".
        Assert.Equal("インポート音声", AudioSourceKind.Imported.ToDisplayLabel());
    }

    [Fact]
    public void TheOriginalPathIsRememberedSoTheWorkingAudioCanBeRebuilt()
    {
        var source = WriteSourceWav();
        var settings = Settings(Path.Combine(_root, "out"));
        var importer = Importer();
        var summary = importer.Import(settings, source);

        Assert.Equal(Path.GetFullPath(source), summary.Metadata.ImportedFromPath);

        // Simulate the delete-after-transcription setting having run.
        File.Delete(Path.Combine(summary.Folder.Path, RecognitionAudioNames.Imported));
        Assert.False(summary.CanTranscribe);

        Assert.True(importer.TryReimport(summary, settings, out var reason), reason);
        Assert.True(summary.CanTranscribe, "the working audio should have been rebuilt from the original");
    }

    [Fact]
    public void RebuildingSaysSoWhenTheOriginalHasGoneAway()
    {
        var source = WriteSourceWav();
        var settings = Settings(Path.Combine(_root, "out"));
        var importer = Importer();
        var summary = importer.Import(settings, source);

        File.Delete(Path.Combine(summary.Folder.Path, RecognitionAudioNames.Imported));
        File.Delete(source);

        Assert.False(importer.TryReimport(summary, settings, out var reason));
        Assert.NotNull(reason);
        Assert.Contains("元の音声ファイルが見つかりません", reason!);
    }

    [Theory]
    [InlineData(".wav")]
    [InlineData(".mp3")]
    [InlineData(".m4a")]
    [InlineData(".aac")]
    public void TheFourAdvertisedExtensionsAreAccepted(string extension)
    {
        // The file dialog offers these four, so the format list has to agree
        // with what the UI says it takes.
        Assert.Contains(extension, AudioImportFormats.Extensions);
        Assert.True(AudioImportFormats.IsSupportedExtension("meeting" + extension));
        Assert.Contains(extension.TrimStart('.'), AudioImportFormats.FileDialogFilter);
    }

    [Fact]
    public void AnUnsupportedFormatIsRefusedByNameBeforeAnythingIsCreated()
    {
        var path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "not audio");

        var settings = Settings(Path.Combine(_root, "out"));
        var error = Assert.Throws<NotSupportedException>(() => Importer().Import(settings, path));

        Assert.Contains("対応していない形式", error.Message);
        Assert.False(Directory.Exists(settings.SaveRoot), "no meeting folder should have been created");
    }

    [Fact]
    public void ACorruptFileIsRefusedAndLeavesNothingBehind()
    {
        var path = Path.Combine(_root, "broken.wav");
        File.WriteAllBytes(path, new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0 });

        var settings = Settings(Path.Combine(_root, "out"));
        var error = Assert.Throws<NotSupportedException>(() => Importer().Import(settings, path));

        Assert.Contains("WAVファイル", error.Message);
        Assert.True(File.Exists(path), "the file the user gave us must survive being rejected");
    }

    [Fact]
    public void AMissingFileIsRefusedWithSomethingTheUserCanActOn()
    {
        var settings = Settings(Path.Combine(_root, "out"));
        var error = Assert.Throws<NotSupportedException>(
            () => Importer().Import(settings, Path.Combine(_root, "gone.wav")));

        Assert.Contains("ファイルが見つかりません", error.Message);
    }

    [Fact]
    public void AlreadyClippedAudioIsReportedRatherThanRepaired()
    {
        var path = Path.Combine(_root, "clipped.wav");
        using (var writer = new WavFileWriter(path, 48000))
        {
            var square = new float[48000 * 2];
            for (var i = 0; i < square.Length; i++)
            {
                square[i] = (i / 100) % 2 == 0 ? 1f : -1f;
            }

            writer.Write(square);
            writer.Flush();
        }

        var summary = Importer().Import(Settings(Path.Combine(_root, "out")), path);

        Assert.True(summary.Metadata.SourceClipped);
        Assert.Contains(summary.Warnings, w => w.Contains("既に歪んでいます"));
        Assert.DoesNotContain(summary.Warnings, w => w.Contains("修復"));
    }

    [Fact]
    public void ADecoderThatFailsHalfWayLeavesNoPartialWorkingFile()
    {
        var settings = Settings(Path.Combine(_root, "out"));
        var importer = new MeetingImporter(new AudioImportService(new HalfwayFailingDecoder()));

        var error = Assert.Throws<InvalidOperationException>(() => importer.Import(settings, WriteSourceWav()));
        Assert.Contains("元のファイルは変更していません", error.Message);

        var partial = Directory.Exists(settings.SaveRoot)
            ? Directory.GetFiles(settings.SaveRoot, RecognitionAudioNames.Imported, SearchOption.AllDirectories)
            : Array.Empty<string>();

        Assert.Empty(partial);
    }

    /// <summary>Writes a little audio and then throws, like a disk filling up.</summary>
    private sealed class HalfwayFailingDecoder : IAudioDecoder
    {
        public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".wav" };

        public bool CanDecode(string sourcePath, out string? reason)
        {
            reason = null;
            return true;
        }

        public void DecodeToMonoWav(string sourcePath, string destinationPath, int targetSampleRate)
        {
            using (var writer = new WavFileWriter(destinationPath, targetSampleRate))
            {
                writer.Write(new float[targetSampleRate]);
                writer.Flush();
            }

            throw new IOException("There is not enough space on the disk. (test)");
        }
    }
}
