using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// Whether the recording that reaches the disk is the audio that reached the
/// microphone, or that audio with holes punched in it.
/// </summary>
public class CaptureContinuityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mr-continuity-" + Guid.NewGuid().ToString("N"));

    private readonly ITestOutputHelper _output;

    public CaptureContinuityTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        GC.SuppressFinalize(this);
    }

    private static AppSettings Settings() => new()
    {
        SaveRoot = Path.GetTempPath(),
        Format = RecordingFormat.Wav,
        PreventSleepWhileRecording = false,
    };

    /// <summary>
    /// The measurement this whole file exists for: a continuous tone goes in, and
    /// every stretch of digital silence that comes out is a piece of the meeting
    /// that no longer exists.
    /// </summary>
    private (int Dropouts, double LostMs, double LongestMs) MeasureDropouts(float[] audio, int sampleRate)
    {
        // A 220 Hz tone crosses zero every ~109 samples and quantises to exactly
        // zero for at most a sample or two on the way past. A run of 1 ms is
        // therefore not the signal - it is a hole.
        var minimumRun = sampleRate / 1000;
        var dropouts = 0;
        long lost = 0;
        var longest = 0;
        var run = 0;

        foreach (var sample in audio)
        {
            if (sample == 0f)
            {
                run++;
                continue;
            }

            if (run >= minimumRun)
            {
                dropouts++;
                lost += run;
                longest = Math.Max(longest, run);
            }

            run = 0;
        }

        if (run >= minimumRun)
        {
            dropouts++;
            lost += run;
            longest = Math.Max(longest, run);
        }

        return (dropouts, lost * 1000.0 / sampleRate, longest * 1000.0 / sampleRate);
    }

    private static IEnumerable<(double AtMs, double LengthMs)> HolePositions(float[] audio, int sampleRate)
    {
        var minimumRun = sampleRate / 1000;
        var run = 0;
        for (var i = 0; i < audio.Length; i++)
        {
            if (audio[i] == 0f)
            {
                run++;
                continue;
            }

            if (run >= minimumRun)
            {
                yield return ((i - run) * 1000.0 / sampleRate, run * 1000.0 / sampleRate);
            }

            run = 0;
        }
    }

    [Fact]
    public void ABurstyEndpointStillProducesContinuousAudio()
    {
        const int rate = 48000;
        const int seconds = 4;

        // Both legs carry sound the whole time, delivered the way WASAPI
        // delivers it: a whole buffer at a time, sometimes late.
        // Only the microphone carries sound. If both legs did, a hole in one
        // would be filled by the other in the mix and the measurement would
        // flatter the pipeline.
        var factory = new BurstyCaptureFactory(
            SignalGenerator.Sine(220, 1.0, rate, 0.30),
            SignalGenerator.Silence(1.0, rate),
            burstMilliseconds: 100);

        using var pipeline = new RecordingPipeline(new RecordingPipelineOptions(), factory);
        var path = Path.Combine(_root, "bursty.wav");

        pipeline.Start(path, Settings(), null);
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        var status = pipeline.GetStatus();
        pipeline.Stop();

        using var reader = new WavFileReader(path);
        var audio = reader.ReadAllMono();

        // Ignore the very end: Stop lets the pump run on for a moment after the
        // capture has stopped, so trailing silence there is expected.
        var body = audio.AsSpan(0, Math.Max(0, audio.Length - (rate / 2))).ToArray();
        var (dropouts, lostMs, longestMs) = MeasureDropouts(body, rate);
        var recordedMs = body.Length * 1000.0 / rate;

        _output.WriteLine($"recorded {recordedMs:F0} ms, RMS {AudioMath.Rms(body):F4}, peak {AudioMath.Peak(body):F4}");
        _output.WriteLine($"dropouts: {dropouts}, lost {lostMs:F1} ms ({lostMs / recordedMs * 100:F2}%), longest {longestMs:F1} ms");
        _output.WriteLine("holes at: " + string.Join(", ", HolePositions(body, rate).Take(20).Select(h => $"{h.AtMs:F0}ms/{h.LengthMs:F0}ms")));
        _output.WriteLine($"pipeline reported: silence inserted {status.SilenceInsertedMs:F1} ms, overruns {status.BufferOverruns}, drift corrected {status.DriftCorrectedMs:F1} ms");

        Assert.True(recordedMs > 3000, $"only {recordedMs:F0} ms was recorded");

        // The bar: under one part in a thousand of the meeting may be missing,
        // and no single hole may be long enough to swallow a syllable.
        Assert.True(
            lostMs / recordedMs < 0.001,
            $"{lostMs:F1} ms of {recordedMs:F0} ms was dropped ({lostMs / recordedMs * 100:F2}%), in {dropouts} holes, longest {longestMs:F1} ms");
        Assert.True(longestMs < 20, $"the longest hole was {longestMs:F1} ms");
    }

    /// <summary>
    /// The other half of the promise: when audio really is lost, the user is
    /// told. A recording that quietly stutters is worse than one that says it
    /// stuttered.
    /// </summary>
    [Fact]
    public void AStarvedMicrophoneIsReportedRatherThanHidden()
    {
        const int rate = 48000;

        // A microphone that delivers only two thirds of the audio it should.
        // Nothing can recover the missing third; the pipeline's job is to say so.
        var factory = new FakeCaptureFactory(
            SignalGenerator.Sine(220, 1.0, rate, 0.30),
            SignalGenerator.Sine(660, 1.0, rate, 0.30),
            micClockScale: 0.66);

        using var pipeline = new RecordingPipeline(new RecordingPipelineOptions(), factory);
        var path = Path.Combine(_root, "starved.wav");

        pipeline.Start(path, Settings(), null);
        Thread.Sleep(2500);
        var result = pipeline.Stop();

        _output.WriteLine(string.Join("\n", result.Warnings));

        Assert.Contains(result.Warnings, w => w.Contains("マイクの音声が途切れ", StringComparison.Ordinal));
    }

    /// <summary>A healthy recording says nothing, because there is nothing to say.</summary>
    [Fact]
    public void AHealthyRecordingRaisesNoGapWarning()
    {
        const int rate = 48000;
        var factory = new BurstyCaptureFactory(
            SignalGenerator.Sine(220, 1.0, rate, 0.30),
            SignalGenerator.Sine(660, 1.0, rate, 0.30),
            burstMilliseconds: 100);

        using var pipeline = new RecordingPipeline(new RecordingPipelineOptions(), factory);
        var path = Path.Combine(_root, "healthy.wav");

        pipeline.Start(path, Settings(), null);
        Thread.Sleep(2500);
        var result = pipeline.Stop();

        Assert.DoesNotContain(result.Warnings, w => w.Contains("マイクの音声が途切れ", StringComparison.Ordinal));
    }
}
