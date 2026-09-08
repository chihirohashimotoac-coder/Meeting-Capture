using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Stt;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// End-to-end exercises of the recording engine using synthetic capture
/// sources. These run identically on a CI runner with no audio hardware, which
/// is the whole point of keeping the pipeline platform neutral.
/// </summary>
public sealed class PipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-pipeline-" + Guid.NewGuid().ToString("N"));

    public PipelineTests() => Directory.CreateDirectory(_root);

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

    private AppSettings CreateSettings() => new()
    {
        SaveRoot = _root,
        ModelDirectory = Path.Combine(_root, "models"),
        SttEnabled = true,
        AutoSaveIntervalSeconds = 5,
        Profile = new PerformanceProfile { ChunkSeconds = 2.0 },
    };

    [Fact]
    public void RecordsBothStreamsIntoOneMixedFileOfTheRightLength()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.3),
            SignalGenerator.Sine(880, 1.0, 48000, 0.3));

        var transcript = new TranscriptStore();
        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { TranscriptionEnabled = false },
            factory,
            transcript);

        var path = Path.Combine(_root, "meeting.wav");
        pipeline.Start(path, CreateSettings(), null, null, Path.Combine(_root, "spill"));

        Thread.Sleep(2000);
        var result = pipeline.Stop(TimeSpan.FromSeconds(5));

        Assert.InRange(result.Duration.TotalSeconds, 1.5, 3.0);

        using var reader = new WavFileReader(path);
        var audio = reader.ReadAllMono();
        Assert.InRange(reader.DurationSeconds, 1.5, 3.0);

        // Both legs must be present: the mix has to be louder than silence and
        // must never clip.
        Assert.True(AudioMath.Rms(audio) > 0.02, "the mixed file is essentially silent");
        Assert.True(AudioMath.Peak(audio) < 1.0);
    }

    [Fact]
    public void MutingTheMicrophoneRecordsPcAudioOnly()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.35),
            SignalGenerator.Sine(660, 1.0, 48000, 0.35));

        var recognizer = new FakeSpeechRecognizer();
        var transcript = new TranscriptStore();

        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { Profile = new PerformanceProfile { ChunkSeconds = 1.0 } },
            factory,
            transcript);

        var settings = CreateSettings();
        settings.MicrophoneMuted = true;

        var path = Path.Combine(_root, "pc-only.wav");
        pipeline.Start(path, settings, recognizer, null, Path.Combine(_root, "spill"));

        Assert.True(pipeline.MicrophoneMuted);
        Thread.Sleep(2500);
        var result = pipeline.Stop(TimeSpan.FromSeconds(5));

        // The recording still runs for the full wall-clock time - muting one leg
        // must not shorten the meeting.
        Assert.InRange(result.Duration.TotalSeconds, 2.0, 3.5);

        using var reader = new WavFileReader(path);
        Assert.True(AudioMath.Rms(reader.ReadAllMono()) > 0.02, "PC audio should still be recorded");

        // Nothing from the muted leg may reach the transcript either.
        var segments = transcript.Snapshot();
        Assert.NotEmpty(segments);
        Assert.All(segments, segment => Assert.Equal(AudioSourceKind.SystemAudio, segment.Source));
    }

    [Fact]
    public void MutingBothStreamsStillProducesACorrectlyTimedSilentFile()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.4),
            SignalGenerator.Sine(660, 1.0, 48000, 0.4));

        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { TranscriptionEnabled = false },
            factory,
            new TranscriptStore());

        var settings = CreateSettings();
        settings.MicrophoneMuted = true;
        settings.SystemAudioMuted = true;

        var path = Path.Combine(_root, "both-muted.wav");
        pipeline.Start(path, settings, null, null, Path.Combine(_root, "spill"));

        Thread.Sleep(1500);
        var result = pipeline.Stop(TimeSpan.FromSeconds(5));

        // Muting everything is a user's choice, not an error: the timeline stays
        // honest and the file is silence rather than a truncated recording.
        Assert.InRange(result.Duration.TotalSeconds, 1.0, 2.5);

        using var reader = new WavFileReader(path);
        var audio = reader.ReadAllMono();
        Assert.InRange(reader.DurationSeconds, 1.0, 2.5);
        Assert.True(AudioMath.Peak(audio) < 0.001, "a fully muted recording must be silent");
    }

    [Fact]
    public void MuteCanBeToggledWhileRecordingAndTakesEffectImmediately()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.4),
            SignalGenerator.Silence(1.0, 48000));

        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { TranscriptionEnabled = false },
            factory,
            new TranscriptStore());

        var path = Path.Combine(_root, "toggled.wav");
        pipeline.Start(path, CreateSettings(), null, null, Path.Combine(_root, "spill"));

        Thread.Sleep(1200);
        pipeline.MicrophoneMuted = true;
        Thread.Sleep(1200);

        var result = pipeline.Stop(TimeSpan.FromSeconds(5));
        Assert.True(pipeline.MicrophoneMuted);

        using var reader = new WavFileReader(path);
        var audio = reader.ReadAllMono();
        var half = audio.Length / 2;

        // Loud first half, silent second half: the toggle took effect during the
        // recording rather than at the next start.
        Assert.True(AudioMath.Rms(audio.AsSpan(0, half)) > 0.02, "the first half should contain the microphone");
        Assert.True(AudioMath.Peak(audio.AsSpan(half)) < 0.01, "the second half should be silent after muting");
    }

    [Fact]
    public void RecognitionReceivesTheCapturedAudioRatherThanTheProcessedMix()
    {
        // The listening chain gates, normalizes, compresses and limits - right for
        // a human ear, wrong for the recognizer, which reads the spectral shape
        // those stages reshape. This feeds a signal well above the normalizer's
        // -20 dBFS target so the chain has to pull it down hard, then checks what
        // each side ended up with. A loud tone rather than a quiet one because the
        // VAD needs to fire for any audio to reach the recognizer at all.
        const double loudAmplitude = 0.9; // about -3.9 dBFS RMS for a sine
        const double sourceLevelDb = -3.9;

        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, loudAmplitude),
            SignalGenerator.Silence(1.0, 48000));

        var recognizer = new FakeSpeechRecognizer();

        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { Profile = new PerformanceProfile { ChunkSeconds = 1.0 } },
            factory,
            new TranscriptStore());

        var path = Path.Combine(_root, "quiet.wav");
        pipeline.Start(path, CreateSettings(), recognizer, null, Path.Combine(_root, "spill"));

        Thread.Sleep(3000);
        pipeline.Stop(TimeSpan.FromSeconds(10));

        double[] received;
        lock (recognizer.ReceivedSampleCounts)
        {
            received = recognizer.ReceivedRmsDb.ToArray();
        }

        Assert.NotEmpty(received);
        var recognizerLevel = received.Max();

        using var reader = new WavFileReader(path);
        var fileLevel = AudioMath.LinearToDb(AudioMath.Rms(reader.ReadAllMono()));

        // The recognizer sees the audio as captured: had it come through the
        // chain, it could not still be sitting at the source level.
        Assert.True(
            Math.Abs(recognizerLevel - sourceLevelDb) < 1.5,
            $"the recognizer should see the captured level ({sourceLevelDb:F1} dBFS) "
            + $"but saw {recognizerLevel:F1} dBFS");

        // The file, meanwhile, has been pulled down towards the listening target.
        Assert.True(
            fileLevel < recognizerLevel - 8.0,
            $"the recorded file ({fileLevel:F1} dBFS) should be well below the captured level, "
            + $"otherwise the two paths are not actually separate");
    }

    [Fact]
    public void RecordingContinuesWithOnlyOneStreamWhenTheOtherDeviceIsUnavailable()
    {
        var factory = new FakeCaptureFactory(failSystem: true);
        var transcript = new TranscriptStore();

        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { TranscriptionEnabled = false },
            factory,
            transcript);

        var path = Path.Combine(_root, "mic-only.wav");
        pipeline.Start(path, CreateSettings(), null, null, Path.Combine(_root, "spill"));

        Thread.Sleep(1200);
        var result = pipeline.Stop(TimeSpan.FromSeconds(5));

        Assert.True(result.Duration.TotalSeconds > 0.8);
        Assert.Contains(result.Warnings, w => w.Contains("PC内部音声"));

        using var reader = new WavFileReader(path);
        Assert.True(AudioMath.Rms(reader.ReadAllMono()) > 0.005, "the surviving stream should still be recorded");
    }

    [Fact]
    public void RecordingContinuesWhenTheMicrophoneEnumeratesButThenRefusesToOpen()
    {
        // Found on a real Windows machine: with microphone access denied in the
        // privacy settings the endpoint still enumerates, and WASAPI only fails
        // when the audio client is initialised - after the pipeline has decided
        // both legs exist. Before this was handled, that single access denial
        // took the whole recording down and the PC audio was lost with it.
        var factory = new FakeCaptureFactory(
            microphoneStartFailure: new UnauthorizedAccessException("Access is denied. (0x80070005 (E_ACCESSDENIED))"));

        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { TranscriptionEnabled = false },
            factory,
            new TranscriptStore());

        var path = Path.Combine(_root, "denied-mic.wav");
        pipeline.Start(path, CreateSettings(), null, null, Path.Combine(_root, "spill"));

        Thread.Sleep(1200);
        var result = pipeline.Stop(TimeSpan.FromSeconds(5));

        Assert.True(result.Duration.TotalSeconds > 0.8, "PC audio must keep recording when the microphone is denied");
        Assert.Contains(result.Warnings, w => w.Contains("マイク"));

        // A denial has one specific remedy, and the warning has to name it.
        Assert.Contains(result.Warnings, w => w.Contains("プライバシー"));

        using var reader = new WavFileReader(path);
        Assert.True(AudioMath.Rms(reader.ReadAllMono()) > 0.005, "the surviving stream should still be recorded");
    }

    [Fact]
    public void RefusesToStartWhenBothStreamsEnumerateButNeitherWillOpen()
    {
        var factory = new FakeCaptureFactory(
            microphoneStartFailure: new UnauthorizedAccessException("Access is denied."),
            systemStartFailure: new InvalidOperationException("The endpoint is in use."));

        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { TranscriptionEnabled = false },
            factory,
            new TranscriptStore());

        var error = Assert.Throws<InvalidOperationException>(() =>
            pipeline.Start(Path.Combine(_root, "neither.wav"), CreateSettings(), null, null, Path.Combine(_root, "spill")));

        // Failing is correct here; failing without saying what to do is not.
        Assert.Contains("プライバシー", error.Message);
        Assert.Equal(RecordingState.Faulted, pipeline.State);
    }

    [Fact]
    public void RefusesToStartWhenNeitherStreamCanBeOpened()
    {
        var factory = new FakeCaptureFactory(failMicrophone: true, failSystem: true);
        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { TranscriptionEnabled = false },
            factory,
            new TranscriptStore());

        Assert.Throws<InvalidOperationException>(() =>
            pipeline.Start(Path.Combine(_root, "none.wav"), CreateSettings(), null, null, Path.Combine(_root, "spill")));
    }

    [Fact]
    public void SilentSystemAudioStillProducesAContinuousTimeline()
    {
        // A loopback endpoint that delivers nothing at all - what WASAPI actually
        // does while the PC is silent.
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.3),
            SignalGenerator.Silence(1.0, 48000));

        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { TranscriptionEnabled = false },
            factory,
            new TranscriptStore());

        var path = Path.Combine(_root, "timeline.wav");
        pipeline.Start(path, CreateSettings(), null, null, Path.Combine(_root, "spill"));
        Thread.Sleep(1500);
        var result = pipeline.Stop(TimeSpan.FromSeconds(5));

        // The recording length must track wall-clock time, not the amount of
        // audio the quiet endpoint happened to deliver.
        Assert.InRange(result.Duration.TotalSeconds, 1.2, 2.5);
    }

    [Fact]
    public void TranscriptionRunsWhileRecordingAndTagsTheSourceStream()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(300, 1.0, 48000, 0.35),
            SignalGenerator.Sine(700, 1.0, 48000, 0.35));

        var transcript = new TranscriptStore();
        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions
            {
                TranscriptionEnabled = true,
                Profile = new PerformanceProfile { ChunkSeconds = 1.0 },
            },
            factory,
            transcript);

        pipeline.Start(
            Path.Combine(_root, "stt.wav"),
            CreateSettings(),
            new FakeSpeechRecognizer(),
            null,
            Path.Combine(_root, "spill"));

        Thread.Sleep(3000);
        pipeline.Stop(TimeSpan.FromSeconds(20));

        var segments = transcript.Snapshot();
        Assert.NotEmpty(segments);

        // Both legs carry a continuous tone, so both must produce segments and
        // each segment must be attributed to the stream it physically came from.
        Assert.Contains(segments, s => s.Source == AudioSourceKind.Microphone);
        Assert.Contains(segments, s => s.Source == AudioSourceKind.SystemAudio);
    }

    [Fact]
    public void RecordingSurvivesARecognizerThatKeepsThrowing()
    {
        var factory = new FakeCaptureFactory();
        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions
            {
                TranscriptionEnabled = true,
                Profile = new PerformanceProfile { ChunkSeconds = 1.0 },
            },
            factory,
            new TranscriptStore());

        var path = Path.Combine(_root, "resilient.wav");
        pipeline.Start(path, CreateSettings(), new AlwaysFailingRecognizer(), null, Path.Combine(_root, "spill"));
        Thread.Sleep(2500);
        var result = pipeline.Stop(TimeSpan.FromSeconds(10));

        Assert.True(result.Duration.TotalSeconds > 1.5, "audio must keep being written when STT fails");

        using var reader = new WavFileReader(path);
        Assert.True(reader.DurationSeconds > 1.5);
    }

    [Fact]
    public void StatusReportsLevelsForBothStreamsBeforeAndDuringRecording()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.5),
            SignalGenerator.Silence(1.0, 48000));

        using var pipeline = new RecordingPipeline(
            new RecordingPipelineOptions { TranscriptionEnabled = false },
            factory,
            new TranscriptStore());

        pipeline.Start(Path.Combine(_root, "levels.wav"), CreateSettings(), null, null, Path.Combine(_root, "spill"));
        Thread.Sleep(800);
        var status = pipeline.GetStatus();
        pipeline.Stop(TimeSpan.FromSeconds(5));

        Assert.Equal(RecordingState.Recording, status.State);
        Assert.True(status.MicLevel.PeakDb > -20, $"microphone meter reads {status.MicLevel.PeakDb:F1} dBFS");
        Assert.True(status.SystemLevel.LooksSilent(), "a silent system endpoint must be detectable before recording");
    }

    [Fact]
    public void SessionManagerProducesACompleteMeetingFolderAndClearsTheJournal()
    {
        var factory = new FakeCaptureFactory();
        using var manager = new MeetingSessionManager(factory);

        var settings = CreateSettings();
        settings.SttEnabled = false;

        var folder = manager.Start(settings, null, null, "月次定例");
        Assert.True(File.Exists(folder.JournalPath), "the crash journal must exist while recording");

        Thread.Sleep(1200);
        var summary = manager.Stop(TimeSpan.FromSeconds(10));

        Assert.True(File.Exists(folder.WavPath));
        Assert.True(File.Exists(folder.TranscriptTextPath));
        Assert.True(File.Exists(folder.TranscriptMarkdownPath));
        Assert.True(File.Exists(folder.MetadataPath));
        Assert.False(File.Exists(folder.JournalPath), "a clean stop must clear the crash marker");
        Assert.False(Directory.Exists(Path.Combine(folder.Path, ".stt-spill")), "temporary files must be removed");

        Assert.Contains("月次定例", folder.Name);
        Assert.InRange(summary.Metadata.DurationSeconds, 0.8, 2.5);
        Assert.Equal(RecordingFormat.Wav, summary.Metadata.Format);
    }

    [Fact]
    public void AnInterruptedSessionIsFoundByTheRecoveryScan()
    {
        var factory = new FakeCaptureFactory();
        var manager = new MeetingSessionManager(factory);

        var settings = CreateSettings();
        settings.SttEnabled = false;

        var folder = manager.Start(settings, null, null, "中断テスト");
        Thread.Sleep(1000);

        // Drop the manager without calling Stop: the process "crashed".
        manager.Dispose();

        var recoverable = new CrashRecoveryService().Scan(_root);
        Assert.Contains(recoverable, r => r.Folder.Path == folder.Path);
    }

    [Fact]
    public void Mp3RequestFallsBackToWavWhenNoEncoderIsAvailable()
    {
        var factory = new FakeCaptureFactory();
        using var manager = new MeetingSessionManager(factory, new NoMp3Transcoder());

        var settings = CreateSettings();
        settings.SttEnabled = false;
        settings.Format = RecordingFormat.Mp3;

        manager.Start(settings, null, null, "MP3");
        Thread.Sleep(900);
        var summary = manager.Stop(TimeSpan.FromSeconds(10));

        Assert.Equal(RecordingFormat.Wav, summary.Metadata.Format);
        Assert.Contains(summary.Warnings, w => w.Contains("MP3"));
        Assert.True(File.Exists(summary.AudioPath), "the recording must survive a failed transcode");
    }

    [Fact]
    public void EditsMadeAfterStopAreWrittenBackToTheTranscriptFiles()
    {
        var factory = new FakeCaptureFactory();
        using var manager = new MeetingSessionManager(factory);

        var settings = CreateSettings();
        settings.SttEnabled = false;

        var folder = manager.Start(settings, null, null, null);
        Thread.Sleep(700);
        manager.Stop(TimeSpan.FromSeconds(10));

        manager.Transcript.InsertManual(1000, AudioSourceKind.Microphone, "手入力した発言");
        manager.SaveTranscript();

        Assert.Contains("手入力した発言", File.ReadAllText(folder.TranscriptTextPath));
    }

    [Fact]
    public void ASecondRecordingCanStillSuppressSleep()
    {
        // The sleep preventer is shared across meetings. If a pipeline disposed
        // the injected instance, every meeting after the first would silently
        // let the laptop sleep mid-recording.
        var sleepPreventer = new NullSleepPreventer();
        var factory = new FakeCaptureFactory();
        var settings = CreateSettings();
        settings.SttEnabled = false;

        for (var round = 0; round < 2; round++)
        {
            using var pipeline = new RecordingPipeline(
                new RecordingPipelineOptions { TranscriptionEnabled = false },
                factory,
                new TranscriptStore(),
                sleepPreventer);

            pipeline.Start(Path.Combine(_root, $"sleep-{round}.wav"), settings, null, null, Path.Combine(_root, "spill"));
            Assert.True(sleepPreventer.IsActive, $"round {round}: sleep suppression was not requested");

            pipeline.Stop(TimeSpan.FromSeconds(5));
            Assert.False(sleepPreventer.IsActive, $"round {round}: sleep suppression was not released");
        }

        // Still usable afterwards - it was never disposed by the pipeline.
        Assert.True(sleepPreventer.Prevent("after both recordings"));
        sleepPreventer.Restore();
    }

    private sealed class AlwaysFailingRecognizer : ISpeechRecognizer
    {
        public string ModelId => "failing";

        public bool IsReady => true;

        public IReadOnlyList<RecognizedSpan> Transcribe(ReadOnlySpan<float> samples, string language, CancellationToken cancellationToken)
            => throw new InvalidOperationException("simulated STT failure");

        public void Dispose()
        {
        }
    }

    private sealed class NoMp3Transcoder : IAudioTranscoder
    {
        public bool IsFormatSupported(RecordingFormat format) => format == RecordingFormat.Wav;

        public string Transcode(string sourceWavPath, string destinationPath, RecordingFormat format, int bitrateKbps)
            => throw new NotSupportedException("no encoder");
    }
}

public sealed class PreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-preflight-" + Guid.NewGuid().ToString("N"));

    public PreflightTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void EveryProblemComesWithAnActionableRemedyNotJustTheWordError()
    {
        var checker = new PreflightChecker(
            new FakeCaptureFactory(failMicrophone: true, failSystem: true),
            new ModelStore(Path.Combine(_root, "models")));

        var settings = new AppSettings { SaveRoot = null, SttEnabled = true };
        var results = checker.Run(settings);

        Assert.Contains(results, r => r.Check == PreflightCheck.Microphone && r.Severity == PreflightSeverity.Error);
        Assert.Contains(results, r => r.Check == PreflightCheck.SystemAudio && r.Severity == PreflightSeverity.Error);
        Assert.Contains(results, r => r.Check == PreflightCheck.SaveLocation && r.Severity == PreflightSeverity.Error);

        foreach (var result in results.Where(r => r.Severity != PreflightSeverity.Ok))
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Remedy), $"{result.Check} has no remedy text");
            Assert.False(string.IsNullOrWhiteSpace(result.Detail), $"{result.Check} has no detail text");
            Assert.True(result.Remedy.Length > 15, $"{result.Check} remedy is too terse to help");
        }
    }

    [Fact]
    public void AHealthySetupPassesEveryCheckExceptTheModelWhenItIsNotDownloaded()
    {
        var checker = new PreflightChecker(new FakeCaptureFactory(), new ModelStore(Path.Combine(_root, "models")));
        var settings = new AppSettings
        {
            SaveRoot = _root,
            SttEnabled = true,
            Profile = new PerformanceProfile { SttModelId = ModelCatalog.WhisperBase },
        };

        var results = checker.Run(settings);

        Assert.Equal(PreflightSeverity.Ok, results.Single(r => r.Check == PreflightCheck.Microphone).Severity);
        Assert.Equal(PreflightSeverity.Ok, results.Single(r => r.Check == PreflightCheck.SystemAudio).Severity);
        Assert.Equal(PreflightSeverity.Ok, results.Single(r => r.Check == PreflightCheck.SaveLocation).Severity);

        // A missing model is a warning, never a blocker: recording still works.
        var model = results.Single(r => r.Check == PreflightCheck.SpeechModel);
        Assert.Equal(PreflightSeverity.Warning, model.Severity);
        Assert.Contains("録音は", model.Remedy);
    }

    [Fact]
    public void TranscriptionDisabledIsNotTreatedAsAProblem()
    {
        var checker = new PreflightChecker(new FakeCaptureFactory(), new ModelStore(Path.Combine(_root, "models")));
        var results = checker.Run(new AppSettings { SaveRoot = _root, SttEnabled = false });

        Assert.Equal(PreflightSeverity.Ok, results.Single(r => r.Check == PreflightCheck.SpeechModel).Severity);
    }
}
