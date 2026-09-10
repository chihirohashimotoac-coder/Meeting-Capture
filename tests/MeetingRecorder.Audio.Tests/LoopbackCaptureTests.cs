using System.Diagnostics;
using System.Runtime.InteropServices;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;
using Xunit.Abstractions;

namespace MeetingRecorder.Audio.Tests;

/// <summary>
/// Plays a tone through the machine's render endpoint and asserts that the
/// product's own WASAPI loopback source captures it.
/// </summary>
/// <remarks>
/// <para>
/// Every other audio test in this repository avoids the hardware. This one does
/// not: it starts <see cref="WasapiSystemAudioCaptureSource"/> - the exact class
/// the recorder uses for "PC内部音声" - plays a 1 kHz tone through WASAPI, and
/// then checks that the captured samples really contain that tone. Nothing here
/// is a test double.
/// </para>
/// <para>
/// A GitHub-hosted Windows runner ships with no audio endpoint at all, so CI
/// installs a virtual render device first (see the workflow). That device is a
/// stand-in for a speaker, not for a user's sound card: what it establishes is
/// that the loopback code path opens a render endpoint, receives the mix
/// Windows is playing, and converts it correctly. Whether a particular laptop's
/// endpoint behaves the same is T-09 and still wants a real machine - though
/// this is a great deal more than "the enumeration call did not throw".
/// </para>
/// <para>
/// <c>MEETINGRECORDER_REQUIRE_LOOPBACK=1</c> turns "no render device, skip" into
/// a failure, so a broken CI step cannot quietly stop running this.
/// </para>
/// </remarks>
public class LoopbackCaptureTests
{
    private const int ToneHz = 1000;
    private const int ControlHz = 3300;
    private const int SampleRate = 48000;

    private readonly ITestOutputHelper _output;

    public LoopbackCaptureTests(ITestOutputHelper output) => _output = output;

    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static bool Required =>
        Environment.GetEnvironmentVariable("MEETINGRECORDER_REQUIRE_LOOPBACK") == "1";

    /// <summary>Returns the default render endpoint, or null when the machine has none.</summary>
    private static MMDevice? TryGetRenderEndpoint()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool HaveRenderEndpoint()
    {
        using var device = TryGetRenderEndpoint();
        if (device is not null)
        {
            _output.WriteLine($"Render endpoint: {device.FriendlyName}");
            return true;
        }

        Assert.False(
            Required,
            "MEETINGRECORDER_REQUIRE_LOOPBACK=1 was set, but this machine has no render endpoint. "
            + "Either the virtual audio device failed to install or the check is running on the wrong machine.");

        _output.WriteLine("No render endpoint on this machine; skipping.");
        return false;
    }

    [Fact]
    public void LoopbackCaptureReceivesTheAudioTheMachineIsPlaying()
    {
        if (!OnWindows || !HaveRenderEndpoint())
        {
            return;
        }

        var factory = new WasapiCaptureFactory();
        using var source = factory.CreateSystemAudioCapture(deviceId: null, targetSampleRate: SampleRate);

        Assert.Equal(AudioSourceKind.SystemAudio, source.Kind);
        _output.WriteLine($"Capturing loopback from: {source.DeviceName}");

        var captured = new List<float>(SampleRate * 6);
        source.DataAvailable += samples =>
        {
            lock (captured)
            {
                foreach (var sample in samples)
                {
                    captured.Add(sample);
                }
            }
        };

        source.Start();

        // The tone starts after capture so that nothing is missed, and the
        // amplitude stays at 0.5 so a hard-limiting endpoint cannot flatten it.
        PlayTone(ToneHz, gain: 0.5, seconds: 4);

        source.Stop();

        float[] samplesCaptured;
        lock (captured)
        {
            samplesCaptured = captured.ToArray();
        }

        var seconds = samplesCaptured.Length / (double)SampleRate;
        var peak = Peak(samplesCaptured);
        var rms = Rms(samplesCaptured);
        _output.WriteLine(
            $"Captured {samplesCaptured.Length} samples ({seconds:F2} s), peak {ToDb(peak):F1} dBFS, RMS {ToDb(rms):F1} dBFS.");

        Assert.True(
            seconds >= 1.0,
            $"Loopback delivered only {seconds:F2} s of audio while a tone was playing for 4 s.");

        Assert.True(
            ToDb(peak) > -30.0,
            $"Loopback delivered {seconds:F2} s of audio but its peak was {ToDb(peak):F1} dBFS - that is silence, not the tone.");

        // Amplitude alone would also be satisfied by noise or by a stuck buffer.
        // Measuring the tone's own frequency is what shows this is the audio the
        // machine was actually playing.
        var toneEnergy = Goertzel(samplesCaptured, ToneHz);
        var controlEnergy = Goertzel(samplesCaptured, ControlHz);
        _output.WriteLine(
            $"Energy at {ToneHz} Hz: {toneEnergy:E3}; at the unused control frequency {ControlHz} Hz: {controlEnergy:E3}.");

        Assert.True(
            toneEnergy > controlEnergy * 20.0,
            $"The captured audio is not dominated by the {ToneHz} Hz tone that was played "
            + $"({toneEnergy:E3} against {controlEnergy:E3} at {ControlHz} Hz).");
    }

    [Fact]
    public void ASilentMachineIsReportedAsSilenceRatherThanAsAFailure()
    {
        if (!OnWindows || !HaveRenderEndpoint())
        {
            return;
        }

        var factory = new WasapiCaptureFactory();
        using var source = factory.CreateSystemAudioCapture(deviceId: null, targetSampleRate: SampleRate);

        var faults = new List<CaptureFault>();
        source.Fault += (_, fault) => faults.Add(fault);

        var received = 0;
        source.DataAvailable += samples => Interlocked.Add(ref received, samples.Length);

        source.Start();
        Thread.Sleep(1500);
        source.Stop();

        // Windows may deliver nothing at all while an endpoint is silent. That is
        // normal behaviour, not a fault, and the app must not report it as one -
        // otherwise every quiet moment in a meeting would raise an error.
        _output.WriteLine($"While silent, loopback delivered {received} samples and {faults.Count} fault(s).");
        Assert.Empty(faults);
    }

    /// <summary>Plays a sine tone through the default render endpoint, synchronously.</summary>
    private void PlayTone(int frequency, double gain, int seconds)
    {
        using var device = TryGetRenderEndpoint();
        Assert.NotNull(device);

        var generator = new SignalGenerator(SampleRate, 1)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = frequency,
            Gain = gain,
        };

        using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
        output.Init(generator.Take(TimeSpan.FromSeconds(seconds)));
        output.Play();

        var stopwatch = Stopwatch.StartNew();
        while (output.PlaybackState == PlaybackState.Playing && stopwatch.Elapsed.TotalSeconds < seconds + 3)
        {
            Thread.Sleep(50);
        }

        output.Stop();
        _output.WriteLine($"Played {frequency} Hz for {stopwatch.Elapsed.TotalSeconds:F2} s.");
    }

    private static float Peak(IReadOnlyList<float> samples)
    {
        var peak = 0f;
        for (var i = 0; i < samples.Count; i++)
        {
            var magnitude = Math.Abs(samples[i]);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak;
    }

    private static double Rms(IReadOnlyList<float> samples)
    {
        if (samples.Count == 0)
        {
            return 0.0;
        }

        var sum = 0.0;
        for (var i = 0; i < samples.Count; i++)
        {
            sum += (double)samples[i] * samples[i];
        }

        return Math.Sqrt(sum / samples.Count);
    }

    private static double ToDb(double magnitude) =>
        magnitude <= 1e-9 ? -120.0 : 20.0 * Math.Log10(magnitude);

    /// <summary>
    /// Energy at one frequency, by the Goertzel algorithm. Cheaper than an FFT
    /// and enough to answer "is the tone that was played present in this audio".
    /// </summary>
    private static double Goertzel(IReadOnlyList<float> samples, double frequency)
    {
        if (samples.Count == 0)
        {
            return 0.0;
        }

        var coefficient = 2.0 * Math.Cos(2.0 * Math.PI * frequency / SampleRate);
        double s1 = 0.0, s2 = 0.0;

        for (var i = 0; i < samples.Count; i++)
        {
            var s0 = samples[i] + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
        return power / samples.Count;
    }
}

/// <summary>
/// Records a real meeting-shaped session through the real WASAPI stack and
/// checks that the audio that was playing ends up in the file.
/// </summary>
/// <remarks>
/// <para>
/// This is the closest thing to the product that can run without a person in
/// the room: the actual <see cref="MeetingRecorder.Core.Pipeline.RecordingPipeline"/>,
/// the actual <see cref="WasapiCaptureFactory"/>, both capture legs, the DSP
/// chains, the mixer and the WAV writer. Only the meeting itself is synthetic.
/// </para>
/// <para>
/// On CI the virtual cable feeds its own capture endpoint, so both legs receive
/// the tone and the two-stream path is genuinely exercised. On a machine with a
/// real microphone the microphone leg records whatever the room is doing, which
/// is fine - the assertions are about the render leg and about the file.
/// </para>
/// </remarks>
public class RealDeviceRecordingTests
{
    private const int ToneHz = 1000;
    private const int SampleRate = 48000;

    private readonly ITestOutputHelper _output;

    public RealDeviceRecordingTests(ITestOutputHelper output) => _output = output;

    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static bool Required =>
        Environment.GetEnvironmentVariable("MEETINGRECORDER_REQUIRE_LOOPBACK") == "1";

    [Fact]
    public void ARecordingMadeThroughTheRealDevicesContainsTheAudioThatWasPlaying()
    {
        if (!OnWindows)
        {
            return;
        }

        using (var probe = TryGetRenderEndpoint())
        {
            if (probe is null)
            {
                Assert.False(Required, "MEETINGRECORDER_REQUIRE_LOOPBACK=1 but this machine has no render endpoint.");
                return;
            }

            _output.WriteLine($"Recording with render endpoint: {probe.FriendlyName}");
        }

        var root = Path.Combine(Path.GetTempPath(), "mr-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var audioPath = Path.Combine(root, "meeting.wav");
            // What is under test is capture, mixing and writing. Nothing here
            // asks for recognition, and the pipeline has no way to perform any.
            var options = new RecordingPipelineOptions { SampleRate = SampleRate };

            var settings = new AppSettings
            {
                // Never leave the runner (or a developer's laptop) awake after a
                // test; sleep suppression has its own tests.
                PreventSleepWhileRecording = false,
            };

            using var pipeline = new RecordingPipeline(options, new WasapiCaptureFactory(), new TranscriptStore());

            pipeline.Start(audioPath, settings, journal: null);

            PlayTone(ToneHz, gain: 0.5, seconds: 5);

            var result = pipeline.Stop();

            foreach (var warning in result.Warnings)
            {
                _output.WriteLine($"Warning: {warning}");
            }

            _output.WriteLine($"Recorded {result.Duration.TotalSeconds:F2} s to {result.AudioFilePath}");
            Assert.True(File.Exists(result.AudioFilePath), "the recording produced no file");

            using var reader = new WavFileReader(result.AudioFilePath);
            var samples = reader.ReadAllMono();

            _output.WriteLine(
                $"File: {reader.SampleRate} Hz, {reader.Channels} ch, {reader.DurationSeconds:F2} s, {samples.Length} samples.");

            // The pipeline runs on a wall clock, so the file length reflects real
            // elapsed time rather than however much the device chose to deliver.
            Assert.Equal(SampleRate, reader.SampleRate);
            Assert.InRange(reader.DurationSeconds, 4.0, 9.0);

            var peak = 0f;
            foreach (var sample in samples)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            var toneEnergy = Goertzel(samples, ToneHz);
            var controlEnergy = Goertzel(samples, 3300);
            _output.WriteLine(
                $"Peak {20 * Math.Log10(Math.Max(peak, 1e-9)):F1} dBFS; energy at {ToneHz} Hz {toneEnergy:E3} against {controlEnergy:E3} at 3300 Hz.");

            Assert.True(
                peak > 0.02f,
                "the recording is silent: the capture legs produced nothing that reached the file");

            Assert.True(
                toneEnergy > controlEnergy * 20.0,
                "the recorded file is not dominated by the tone that was played while recording");

            // A clipped file would also pass the checks above, and clipping is the
            // failure the DSP chain exists to prevent.
            Assert.True(peak <= 1.0f, "the recording clipped");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A file the OS still holds open is not a test failure.
            }
        }
    }

    private static MMDevice? TryGetRenderEndpoint()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void PlayTone(int frequency, double gain, int seconds)
    {
        using var device = TryGetRenderEndpoint();
        Assert.NotNull(device);

        var generator = new SignalGenerator(SampleRate, 1)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = frequency,
            Gain = gain,
        };

        using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
        output.Init(generator.Take(TimeSpan.FromSeconds(seconds)));
        output.Play();

        var stopwatch = Stopwatch.StartNew();
        while (output.PlaybackState == PlaybackState.Playing && stopwatch.Elapsed.TotalSeconds < seconds + 3)
        {
            Thread.Sleep(50);
        }

        output.Stop();
        _output.WriteLine($"Played {frequency} Hz for {stopwatch.Elapsed.TotalSeconds:F2} s while recording.");
    }

    private static double Goertzel(IReadOnlyList<float> samples, double frequency)
    {
        if (samples.Count == 0)
        {
            return 0.0;
        }

        var coefficient = 2.0 * Math.Cos(2.0 * Math.PI * frequency / SampleRate);
        double s1 = 0.0, s2 = 0.0;

        for (var i = 0; i < samples.Count; i++)
        {
            var s0 = samples[i] + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
        return power / samples.Count;
    }
}
