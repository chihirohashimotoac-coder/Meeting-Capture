using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Stt;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// Checks where the microphone listening EQ is allowed to reach, using the real
/// pipeline and synthetic capture sources.
/// </summary>
/// <remarks>
/// The equalizer exists for one destination - the microphone leg of the meeting
/// file somebody listens back to - and the value of it depends entirely on it
/// not reaching anywhere else. Two places in particular:
/// <list type="bullet">
/// <item><description>the 16 kHz working audio, because whisper was trained on
/// speech that has not been re-shaped and there is no reason to think a shelf
/// helps it;</description></item>
/// <item><description>the PC-audio leg, which is not the leg with the problem
/// and is already clear.</description></item>
/// </list>
/// </remarks>
public sealed class ListeningEqPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-listening-eq-" + Guid.NewGuid().ToString("N"));

    public ListeningEqPipelineTests() => Directory.CreateDirectory(_root);

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

    private AppSettings Settings(bool listeningEq) => new()
    {
        SaveRoot = _root,
        SttEnabled = true,
        KeepRecognitionAudio = true,
        Format = RecordingFormat.Wav,
        Processing = new AudioProcessingSettings { MicrophoneListeningEqEnabled = listeningEq },
    };

    /// <summary>
    /// Runs a recording where one leg carries a low tone and the other a high
    /// one, and returns the files.
    /// </summary>
    private (float[] Mix, float[] MicWorking, float[] SystemWorking) Record(bool listeningEq, string name)
    {
        var settings = Settings(listeningEq);
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(300, 1.0, 48000, 0.3),
            SignalGenerator.Sine(300, 1.0, 48000, 0.3));

        var directory = Path.Combine(_root, name);
        var mixPath = Path.Combine(_root, name + ".wav");

        using (var pipeline = new RecordingPipeline(
                   new RecordingPipelineOptions { Processing = settings.Processing }, factory))
        {
            pipeline.Start(mixPath, settings, null, directory);
            Thread.Sleep(2000);
            pipeline.Stop();
        }

        using var mix = new WavFileReader(mixPath);
        using var mic = new WavFileReader(Path.Combine(directory, RecognitionAudioNames.Microphone));
        using var system = new WavFileReader(Path.Combine(directory, RecognitionAudioNames.SystemAudio));

        return (mix.ReadAllMono(), mic.ReadAllMono(), system.ReadAllMono());
    }

    [Fact]
    public void TheWorkingAudioIsIdenticalWhetherOrNotTheEqualizerIsOn()
    {
        // The strongest form of "recognition audio is never equalized": the same
        // deterministic input produces the same 16 kHz file at the same level
        // both ways.
        var off = Record(listeningEq: false, "eq-off");
        var on = Record(listeningEq: true, "eq-on");

        var offRms = AudioMath.Rms(off.MicWorking.AsSpan(4000, off.MicWorking.Length - 8000));
        var onRms = AudioMath.Rms(on.MicWorking.AsSpan(4000, on.MicWorking.Length - 8000));

        Assert.InRange(onRms, offRms * 0.99, offRms * 1.01);
    }

    [Fact]
    public void TheEqualizerChangesTheMeetingFileAndNothingElse()
    {
        var off = Record(listeningEq: false, "mix-off");
        var on = Record(listeningEq: true, "mix-on");

        // A 300 Hz tone sits below the 2.5 kHz shelf corner, so the normalized
        // shelf attenuates it: the meeting file changes.
        var offMix = AudioMath.Rms(off.Mix.AsSpan(10000, off.Mix.Length - 20000));
        var onMix = AudioMath.Rms(on.Mix.AsSpan(10000, on.Mix.Length - 20000));
        Assert.True(onMix < offMix * 0.99, $"the meeting mix was unchanged ({onMix:F4} vs {offMix:F4})");

        // ...while the PC-audio working file is untouched, because the shelf is
        // only ever applied to the microphone.
        var offSystem = AudioMath.Rms(off.SystemWorking.AsSpan(4000, off.SystemWorking.Length - 8000));
        var onSystem = AudioMath.Rms(on.SystemWorking.AsSpan(4000, on.SystemWorking.Length - 8000));
        Assert.InRange(onSystem, offSystem * 0.99, offSystem * 1.01);
    }

    [Fact]
    public void NothingInTheRecordingPathEverReachesFullScale()
    {
        // The mixer's guarantee, re-checked with the equalizer in the chain and
        // both legs driven hard: 0.9 on each side sums to 0.9 after the constant
        // -6 dB, and no filter is allowed to push that over.
        var settings = Settings(listeningEq: true);
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(180, 1.0, 48000, 0.9),
            SignalGenerator.Sine(240, 1.0, 48000, 0.9));

        var path = Path.Combine(_root, "hot.wav");
        using (var pipeline = new RecordingPipeline(
                   new RecordingPipelineOptions { Processing = settings.Processing }, factory))
        {
            pipeline.Start(path, settings, null, Path.Combine(_root, "hot"));
            Thread.Sleep(2000);
            pipeline.Stop();
        }

        using var reader = new WavFileReader(path);
        var samples = reader.ReadAllMono();

        Assert.True(AudioMath.Peak(samples) < 0.999, $"peak reached {AudioMath.Peak(samples):F4}");

        // A clamp engaging would show up as a run of identical full-scale
        // samples. There must be none, because the gain structure makes the
        // clamp unreachable rather than because it is doing its job.
        var atCeiling = samples.Count(s => Math.Abs(s) >= 0.999f);
        Assert.Equal(0, atCeiling);
    }

    [Fact]
    public void TheWorkingAudioIsSixteenKilohertzAndTheMeetingFileIsFortyEight()
    {
        var settings = Settings(listeningEq: false);
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(440, 1.0, 48000, 0.3),
            SignalGenerator.Sine(660, 1.0, 48000, 0.3));

        var directory = Path.Combine(_root, "rates");
        var path = Path.Combine(_root, "rates.wav");

        using (var pipeline = new RecordingPipeline(
                   new RecordingPipelineOptions { Processing = settings.Processing }, factory))
        {
            pipeline.Start(path, settings, null, directory);
            Thread.Sleep(1500);
            pipeline.Stop();
        }

        using (var meeting = new WavFileReader(path))
        {
            Assert.Equal(48000, meeting.SampleRate);
        }

        foreach (var name in new[] { RecognitionAudioNames.Microphone, RecognitionAudioNames.SystemAudio })
        {
            using var working = new WavFileReader(Path.Combine(directory, name));

            // 16 kHz is not a defect to be fixed. It is the rate whisper.cpp
            // consumes, and a file at that rate has 8 kHz of bandwidth by
            // definition - which is why it sounds duller than the meeting file
            // and why that is not a bug.
            Assert.Equal(SpeechConstants.SampleRate, working.SampleRate);
        }
    }
}
