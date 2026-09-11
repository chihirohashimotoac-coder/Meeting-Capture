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
        KeepRecognitionAudio = true,
        AutoSaveIntervalSeconds = 5,
    };

    private static RecordingPipelineOptions Options() => new();

    [Fact]
    public void RecordsBothStreamsIntoOneMixedFileOfTheRightLength()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.3),
            SignalGenerator.Sine(880, 1.0, 48000, 0.3));

        using var pipeline = new RecordingPipeline(Options(), factory);

        var path = Path.Combine(_root, "meeting.wav");
        pipeline.Start(path, CreateSettings(), null);

        Thread.Sleep(2000);
        var result = pipeline.Stop();

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
    public void StartingARecordingNeverTouchesASpeechRecognizer()
    {
        // The requirement in its most direct form. RecordingPipeline.Start takes
        // no recognizer and no diarizer because there is nowhere in the class
        // that could use one; this asserts that the signature stays that way, so
        // a future change has to be deliberate rather than accidental.
        var parameters = typeof(RecordingPipeline)
            .GetMethod(nameof(RecordingPipeline.Start))!
            .GetParameters();

        Assert.DoesNotContain(parameters, p => typeof(ISpeechRecognizer).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(parameters, p => typeof(ISpeakerDiarizer).IsAssignableFrom(p.ParameterType));

        // And no field of the pipeline holds one either, which is what would let
        // it acquire a recognizer by some other route.
        var fields = typeof(RecordingPipeline)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.FieldType)
            .ToList();

        Assert.DoesNotContain(fields, f => typeof(ISpeechRecognizer).IsAssignableFrom(f));
        Assert.DoesNotContain(fields, f => typeof(ISpeakerDiarizer).IsAssignableFrom(f));
    }

    [Fact]
    public void NothingInTheRecordingApiAcceptsARecognizerOrADiarizer()
    {
        // The other half of the guarantee: not only does RecordingPipeline hold
        // no recognizer, there is no way to hand one to the layer above it
        // either. A recording is started with settings and a title.
        foreach (var method in new[] { nameof(MeetingSessionManager.Start), nameof(MeetingSessionManager.Stop) })
        {
            var parameters = typeof(MeetingSessionManager).GetMethod(method)!.GetParameters();
            Assert.DoesNotContain(parameters, p => typeof(ISpeechRecognizer).IsAssignableFrom(p.ParameterType));
            Assert.DoesNotContain(parameters, p => typeof(ISpeakerDiarizer).IsAssignableFrom(p.ParameterType));
        }
    }

    [Fact]
    public void MutingTheMicrophoneRecordsPcAudioOnly()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.35),
            SignalGenerator.Sine(660, 1.0, 48000, 0.35));

        using var pipeline = new RecordingPipeline(Options(), factory);

        var settings = CreateSettings();
        settings.MicrophoneMuted = true;

        var recognitionDirectory = Path.Combine(_root, "pc-only-recognition");
        var path = Path.Combine(_root, "pc-only.wav");
        pipeline.Start(path, settings, null, recognitionDirectory);

        Assert.True(pipeline.MicrophoneMuted);
        Thread.Sleep(2500);
        var result = pipeline.Stop();

        // The recording still runs for the full wall-clock time - muting one leg
        // must not shorten the meeting.
        Assert.InRange(result.Duration.TotalSeconds, 2.0, 3.5);

        using var reader = new WavFileReader(path);
        Assert.True(AudioMath.Rms(reader.ReadAllMono()) > 0.02, "PC audio should still be recorded");

        // Nothing from the muted leg reaches the working audio either, so it
        // cannot come back later as a transcript.
        using var micAudio = new WavFileReader(Path.Combine(recognitionDirectory, RecognitionAudioNames.Microphone));
        Assert.True(AudioMath.Peak(micAudio.ReadAllMono()) < 0.001, "a muted leg must not be kept for transcription");
    }

    [Fact]
    public void MutingBothStreamsStillProducesACorrectlyTimedSilentFile()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.4),
            SignalGenerator.Sine(660, 1.0, 48000, 0.4));

        using var pipeline = new RecordingPipeline(Options(), factory);

        var settings = CreateSettings();
        settings.MicrophoneMuted = true;
        settings.SystemAudioMuted = true;

        var path = Path.Combine(_root, "both-muted.wav");
        pipeline.Start(path, settings, null);

        Thread.Sleep(1500);
        var result = pipeline.Stop();

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

        using var pipeline = new RecordingPipeline(Options(), factory);

        var path = Path.Combine(_root, "toggled.wav");
        pipeline.Start(path, CreateSettings(), null);

        Thread.Sleep(1200);
        pipeline.MicrophoneMuted = true;
        Thread.Sleep(1200);

        var result = pipeline.Stop();
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
    public void TheWorkingAudioIsTheCapturedAudioRatherThanAProcessedVersionOfIt()
    {
        // What reaches the recognizer later has to be what the microphone
        // produced. The mixer's constant -6 dB applies to the file only; the
        // per-stream working audio is written before it, so a tone at a known
        // level comes back at that level.
        const double amplitude = 0.5;

        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, amplitude),
            SignalGenerator.Silence(1.0, 48000));

        using var pipeline = new RecordingPipeline(Options(), factory);

        var recognitionDirectory = Path.Combine(_root, "fidelity");
        pipeline.Start(Path.Combine(_root, "fidelity.wav"), CreateSettings(), null, recognitionDirectory);
        Thread.Sleep(2000);
        pipeline.Stop();

        using var micAudio = new WavFileReader(Path.Combine(recognitionDirectory, RecognitionAudioNames.Microphone));
        var samples = micAudio.ReadAllMono();

        // Skip the resampler's warm-up.
        var rms = AudioMath.Rms(samples.AsSpan(4000, samples.Length - 8000));
        var expected = amplitude / Math.Sqrt(2);

        Assert.InRange(rms, expected * 0.9, expected * 1.1);
    }

    [Fact]
    public void KeepsPerStreamWorkingAudioForBothLegs()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.35),
            SignalGenerator.Sine(660, 1.0, 48000, 0.35));

        using var pipeline = new RecordingPipeline(Options(), factory);

        var recognitionDirectory = Path.Combine(_root, "recognition");
        var path = Path.Combine(_root, "with-working-audio.wav");

        pipeline.Start(path, CreateSettings(), null, recognitionDirectory);

        Thread.Sleep(2000);
        var result = pipeline.Stop();

        Assert.Equal(recognitionDirectory, result.RecognitionAudioDirectory);
        Assert.True(RecognitionAudioNames.HasPerStreamAudio(recognitionDirectory));

        // Written at the engine's rate, one file per capture stream: this is what
        // keeps "microphone" and "PC audio" a recorded fact rather than a guess.
        foreach (var name in new[] { RecognitionAudioNames.Microphone, RecognitionAudioNames.SystemAudio })
        {
            using var audio = new WavFileReader(Path.Combine(recognitionDirectory, name));
            Assert.Equal(SpeechConstants.SampleRate, audio.SampleRate);
            Assert.InRange(audio.DurationSeconds, 1.5, 3.0);
            Assert.True(AudioMath.Rms(audio.ReadAllMono()) > 0.05, $"{name} should carry its own stream");
        }
    }

    [Fact]
    public void KeepsNoWorkingAudioWhenTranscriptionIsTurnedOff()
    {
        var factory = new FakeCaptureFactory();
        using var pipeline = new RecordingPipeline(Options(), factory);

        var path = Path.Combine(_root, "no-working-audio.wav");
        pipeline.Start(path, CreateSettings(), null);

        Thread.Sleep(1200);
        var result = pipeline.Stop();

        // 115 MB per hour per stream is not something to spend without being
        // asked: no directory, no copies.
        Assert.Null(result.RecognitionAudioDirectory);
        Assert.False(RecognitionAudioNames.ExistIn(_root));
    }

    [Fact]
    public void RecordingContinuesWithOnlyOneStreamWhenTheOtherDeviceIsUnavailable()
    {
        var factory = new FakeCaptureFactory(failSystem: true);

        using var pipeline = new RecordingPipeline(
            Options(),
            factory);

        var path = Path.Combine(_root, "mic-only.wav");
        pipeline.Start(path, CreateSettings(), null);

        Thread.Sleep(1200);
        var result = pipeline.Stop();

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
            Options(),
            factory);

        var path = Path.Combine(_root, "denied-mic.wav");
        pipeline.Start(path, CreateSettings(), null);

        Thread.Sleep(1200);
        var result = pipeline.Stop();

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
            Options(),
            factory);

        var error = Assert.Throws<InvalidOperationException>(() =>
            pipeline.Start(Path.Combine(_root, "neither.wav"), CreateSettings(), null));

        // Failing is correct here; failing without saying what to do is not.
        Assert.Contains("プライバシー", error.Message);
        Assert.Equal(RecordingState.Faulted, pipeline.State);
    }

    [Fact]
    public void RefusesToStartWhenNeitherStreamCanBeOpened()
    {
        var factory = new FakeCaptureFactory(failMicrophone: true, failSystem: true);
        using var pipeline = new RecordingPipeline(
            Options(),
            factory);

        Assert.Throws<InvalidOperationException>(() =>
            pipeline.Start(Path.Combine(_root, "none.wav"), CreateSettings(), null));
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
            Options(),
            factory);

        var path = Path.Combine(_root, "timeline.wav");
        pipeline.Start(path, CreateSettings(), null);
        Thread.Sleep(1500);
        var result = pipeline.Stop();

        // The recording length must track wall-clock time, not the amount of
        // audio the quiet endpoint happened to deliver.
        Assert.InRange(result.Duration.TotalSeconds, 1.2, 2.5);
    }

    [Fact]
    public void TheTranscriptStaysEmptyForTheWholeRecording()
    {
        // Two streams of continuous tone for three seconds: under the old design
        // this produced a steady flow of segments. It must now produce none,
        // because nothing is transcribing.
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(300, 1.0, 48000, 0.35),
            SignalGenerator.Sine(700, 1.0, 48000, 0.35));

        using var manager = new MeetingSessionManager(factory);
        var folder = manager.Start(CreateSettings(), "no-live-stt");

        for (var i = 0; i < 6; i++)
        {
            Thread.Sleep(500);
            Assert.Empty(manager.Transcript.Snapshot());
        }

        var summary = manager.Stop();
        Assert.Empty(manager.Transcript.Snapshot());

        // Meanwhile the audio it should have been producing is all there.
        Assert.True(File.Exists(summary.AudioPath));
        Assert.True(summary.CanTranscribe, "the working audio should be ready for a transcription the user asks for");
        Assert.True(RecognitionAudioNames.HasPerStreamAudio(folder.Path));
    }

    [Fact]
    public void StoppingDoesNotStartATranscription()
    {
        // Stop returns a finished recording and nothing else. The audio is
        // there, ready to be transcribed when the user asks; the transcript is
        // not, because nobody asked.
        var factory = new FakeCaptureFactory();
        using var manager = new MeetingSessionManager(factory);

        manager.Start(CreateSettings(), "stop-only");
        Thread.Sleep(1200);

        var before = DateTime.UtcNow;
        var summary = manager.Stop();
        var elapsed = DateTime.UtcNow - before;

        Assert.Empty(manager.Transcript.Snapshot());
        Assert.True(summary.CanTranscribe);

        // Stopping is a file operation, not an inference: it finishes promptly
        // whatever model happens to be installed.
        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"stopping took {elapsed.TotalSeconds:F1}s");

        // And the transcript files written at stop are the empty ones.
        Assert.Contains("認識された発言はありません", File.ReadAllText(summary.Folder.TranscriptTextPath));
    }

    [Fact]
    public void StatusReportsLevelsForBothStreamsBeforeAndDuringRecording()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.5),
            SignalGenerator.Silence(1.0, 48000));

        using var pipeline = new RecordingPipeline(
            Options(),
            factory);

        pipeline.Start(Path.Combine(_root, "levels.wav"), CreateSettings(), null);
        Thread.Sleep(800);
        var status = pipeline.GetStatus();
        pipeline.Stop();

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

        var folder = manager.Start(settings, "月次定例");
        Assert.True(File.Exists(folder.JournalPath), "the crash journal must exist while recording");

        Thread.Sleep(1200);
        var summary = manager.Stop();

        Assert.True(File.Exists(folder.WavPath));
        Assert.True(File.Exists(folder.TranscriptTextPath));
        Assert.True(File.Exists(folder.TranscriptMarkdownPath));
        Assert.True(File.Exists(folder.MetadataPath));
        Assert.False(File.Exists(folder.JournalPath), "a clean stop must clear the crash marker");

        Assert.Contains("月次定例", folder.Name);
        Assert.InRange(summary.Metadata.DurationSeconds, 0.8, 2.5);
        Assert.Equal(RecordingFormat.Wav, summary.Metadata.Format);
    }

    [Fact]
    public void AQuietRecordingIsRaisedByOneConstantGainWhenItStops()
    {
        // The mixer deliberately writes 6 dB down so two full-scale streams
        // cannot clip. Stopping is where that head-room is handed back - once,
        // to the whole file, now that its real peak is known.
        // -16.5 dBFS in the mixed file: quiet enough to need a real boost, loud
        // enough that the boost cap does not bind.
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.3),
            SignalGenerator.Silence(1.0, 48000));

        using var manager = new MeetingSessionManager(factory);
        var settings = CreateSettings();
        settings.SttEnabled = false;

        manager.Start(settings, "quiet");
        Thread.Sleep(1500);
        var summary = manager.Stop();

        Assert.NotNull(summary.Metadata.AppliedGainDb);
        Assert.InRange(summary.Metadata.AppliedGainDb!.Value, 14.0, 16.5);

        using var reader = new WavFileReader(summary.AudioPath);
        var peak = AudioMath.Peak(reader.ReadAllMono());

        Assert.InRange(AudioMath.LinearToDb(peak), -1.5, -0.5);
        Assert.True(peak < 1.0, "normalization must never produce a full-scale sample");
    }

    [Fact]
    public void AVeryQuietRecordingIsBoostedOnlyAsFarAsTheCapAllows()
    {
        // -38 dBFS once mixed. Raising that to -1 dBFS would mean +37 dB, which
        // is amplifying the room's noise floor rather than anybody's voice.
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, 48000, 0.025),
            SignalGenerator.Silence(1.0, 48000));

        using var manager = new MeetingSessionManager(factory);
        var settings = CreateSettings();
        settings.SttEnabled = false;

        manager.Start(settings, "very-quiet");
        Thread.Sleep(1500);
        var summary = manager.Stop();

        Assert.NotNull(summary.Metadata.AppliedGainDb);
        Assert.InRange(summary.Metadata.AppliedGainDb!.Value, 19.9, 20.1);

        using var reader = new WavFileReader(summary.AudioPath);
        Assert.True(AudioMath.Peak(reader.ReadAllMono()) < 1.0);
    }

    [Fact]
    public void ASilentRecordingIsLeftSilentRatherThanAmplified()
    {
        var factory = new FakeCaptureFactory(
            SignalGenerator.Silence(1.0, 48000),
            SignalGenerator.Silence(1.0, 48000));

        using var manager = new MeetingSessionManager(factory);
        var settings = CreateSettings();
        settings.SttEnabled = false;

        manager.Start(settings, "silent");
        Thread.Sleep(1200);
        var summary = manager.Stop();

        Assert.Null(summary.Metadata.AppliedGainDb);

        using var reader = new WavFileReader(summary.AudioPath);
        Assert.Equal(0.0, AudioMath.Peak(reader.ReadAllMono()));
    }

    [Fact]
    public void ARecordingThatWasAlreadyClippedAtTheInputIsReportedToTheUser()
    {
        // A square wave at full scale is what an over-driven input produces.
        // Halved by the mixer it no longer reads as clipped in the file, so the
        // check has to run on what was captured - and it must say so rather than
        // claim to have fixed it.
        var square = new float[48000];
        for (var i = 0; i < square.Length; i++)
        {
            square[i] = (i / 100) % 2 == 0 ? 1f : -1f;
        }

        var factory = new FakeCaptureFactory(square, square);

        using var manager = new MeetingSessionManager(factory);
        var settings = CreateSettings();
        settings.SttEnabled = false;

        manager.Start(settings, "clipped");
        Thread.Sleep(1500);
        var summary = manager.Stop();

        Assert.True(summary.Metadata.SourceClipped);
        Assert.Contains(summary.Warnings, w => w.Contains("既に歪んでいます"));
        Assert.Contains(summary.Warnings, w => w.Contains("復元できません"));
    }

    [Fact]
    public void AnInterruptedSessionIsFoundByTheRecoveryScan()
    {
        var factory = new FakeCaptureFactory();
        var manager = new MeetingSessionManager(factory);

        var settings = CreateSettings();
        settings.SttEnabled = false;

        var folder = manager.Start(settings, "中断テスト");
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

        manager.Start(settings, "MP3");
        Thread.Sleep(900);
        var summary = manager.Stop();

        Assert.Equal(RecordingFormat.Wav, summary.Metadata.Format);
        Assert.Contains(summary.Warnings, w => w.Contains("MP3"));
        Assert.True(File.Exists(summary.AudioPath), "the recording must survive a failed transcode");
    }

    [Fact]
    public void TheBitrateRecordedIsTheOneTheEncoderActuallyProduced()
    {
        // An encoder publishes a fixed set of output formats, so a request for
        // 192 kbps can come back as something else. Writing the requested figure
        // into metadata would make the file describe itself incorrectly, and the
        // user would have no way to know.
        var factory = new FakeCaptureFactory();
        using var manager = new MeetingSessionManager(factory, new FixedRateTranscoder(160));

        var settings = CreateSettings();
        settings.SttEnabled = false;
        settings.Format = RecordingFormat.Mp3;
        settings.Mp3BitrateKbps = 192;

        manager.Start(settings, "Bitrate");
        Thread.Sleep(900);
        var summary = manager.Stop();

        Assert.Equal(RecordingFormat.Mp3, summary.Metadata.Format);
        Assert.Equal(160, summary.Metadata.Mp3BitrateKbps);
        Assert.Contains(summary.Warnings, w => w.Contains("160 kbps", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEncoderThatHonoursTheRequestSaysNothing()
    {
        var factory = new FakeCaptureFactory();
        using var manager = new MeetingSessionManager(factory, new FixedRateTranscoder(192));

        var settings = CreateSettings();
        settings.SttEnabled = false;
        settings.Format = RecordingFormat.Mp3;
        settings.Mp3BitrateKbps = 192;

        manager.Start(settings, "Bitrate-ok");
        Thread.Sleep(900);
        var summary = manager.Stop();

        Assert.Equal(192, summary.Metadata.Mp3BitrateKbps);
        Assert.DoesNotContain(summary.Warnings, w => w.Contains("kbps", StringComparison.Ordinal));
    }

    [Fact]
    public void EditsMadeAfterStopAreWrittenBackToTheTranscriptFiles()
    {
        var factory = new FakeCaptureFactory();
        using var manager = new MeetingSessionManager(factory);

        var settings = CreateSettings();
        settings.SttEnabled = false;

        var folder = manager.Start(settings);
        Thread.Sleep(700);
        manager.Stop();

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
                Options(),
                factory,
                sleepPreventer);

            pipeline.Start(Path.Combine(_root, $"sleep-{round}.wav"), settings, null);
            Assert.True(sleepPreventer.IsActive, $"round {round}: sleep suppression was not requested");

            pipeline.Stop();
            Assert.False(sleepPreventer.IsActive, $"round {round}: sleep suppression was not released");
        }

        // Still usable afterwards - it was never disposed by the pipeline.
        Assert.True(sleepPreventer.Prevent("after both recordings"));
        sleepPreventer.Restore();
    }

    private sealed class NoMp3Transcoder : IAudioTranscoder
    {
        public bool IsFormatSupported(RecordingFormat format) => format == RecordingFormat.Wav;

        public TranscodeResult Transcode(string sourceWavPath, string destinationPath, RecordingFormat format, int bitrateKbps)
            => throw new NotSupportedException("no encoder");
    }

    /// <summary>An encoder that always produces one particular bitrate.</summary>
    private sealed class FixedRateTranscoder : IAudioTranscoder
    {
        private readonly int _actualKbps;

        public FixedRateTranscoder(int actualKbps) => _actualKbps = actualKbps;

        public bool IsFormatSupported(RecordingFormat format) => true;

        public TranscodeResult Transcode(string sourceWavPath, string destinationPath, RecordingFormat format, int bitrateKbps)
        {
            // Stands in for the encoder without pulling Media Foundation into a
            // Linux test run: the bytes do not matter here, the reported rate does.
            File.Copy(sourceWavPath, destinationPath, overwrite: true);
            return new TranscodeResult(destinationPath, _actualKbps);
        }
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
