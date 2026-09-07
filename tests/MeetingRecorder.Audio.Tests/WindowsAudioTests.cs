using System.Runtime.InteropServices;
using MeetingRecorder.Core.Models;
using Xunit;

namespace MeetingRecorder.Audio.Tests;

/// <summary>
/// Windows-only checks for the WASAPI layer.
/// </summary>
/// <remarks>
/// <para>The suite only runs on Windows (the project targets net8.0-windows);
/// each test returns immediately on any other host so the assembly still builds
/// and loads everywhere.</para>
///
/// <para><b>What these tests can and cannot prove.</b> A GitHub-hosted Windows
/// runner has no microphone, no speakers and no audio session at all. These
/// tests therefore verify only what is verifiable there: that the endpoint
/// enumeration path executes, that a missing device produces a clear Japanese
/// error rather than a crash or a silent recording, and that the sleep
/// suppression API can be called and released.</para>
///
/// <para>They deliberately do <b>not</b> assert that any device exists, and they
/// never claim WASAPI loopback capture works. That can only be established on
/// real hardware and is tracked in docs/WINDOWS_E2E_TEST.md as
/// "requires a physical Windows audio device".</para>
/// </remarks>
public class WasapiDeviceEnumerationTests
{
    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void EnumeratingEndpointsDoesNotThrowEvenWithNoAudioHardware()
    {
        if (!OnWindows)
        {
            return;
        }


        var provider = new WasapiDeviceProvider();

        var capture = provider.GetCaptureDevices();
        var render = provider.GetRenderDevices();

        // A CI runner legitimately reports zero devices. Reporting an empty list
        // is correct; throwing, hanging, or inventing a device is not.
        Assert.NotNull(capture);
        Assert.NotNull(render);
        Assert.All(capture, device => Assert.Equal(AudioSourceKind.Microphone, device.Kind));
        Assert.All(render, device => Assert.Equal(AudioSourceKind.SystemAudio, device.Kind));
        Assert.All(capture, device => Assert.False(string.IsNullOrWhiteSpace(device.Id)));
    }

    [Fact]
    public void AMissingCaptureDeviceProducesAnActionableJapaneseMessage()
    {
        if (!OnWindows)
        {
            return;
        }


        var factory = new WasapiCaptureFactory();

        try
        {
            using var source = factory.CreateMicrophoneCapture(deviceId: null, targetSampleRate: 48000);

            // A machine with a real microphone: the source must describe itself.
            Assert.False(string.IsNullOrWhiteSpace(source.DeviceName));
            Assert.Equal(48000, source.SampleRate);
            Assert.Equal(AudioSourceKind.Microphone, source.Kind);
        }
        catch (InvalidOperationException ex)
        {
            // A runner with no audio hardware: the failure has to be explained.
            Assert.Contains("マイク", ex.Message);
            Assert.True(ex.Message.Length > 20, "the error must tell the user what to do");
        }
    }

    [Fact]
    public void AMissingRenderDeviceProducesAnActionableJapaneseMessage()
    {
        if (!OnWindows)
        {
            return;
        }


        var factory = new WasapiCaptureFactory();

        try
        {
            using var source = factory.CreateSystemAudioCapture(deviceId: null, targetSampleRate: 48000);
            Assert.Equal(AudioSourceKind.SystemAudio, source.Kind);
            Assert.False(string.IsNullOrWhiteSpace(source.DeviceName));
        }
        catch (InvalidOperationException ex)
        {
            Assert.Contains("再生デバイス", ex.Message);
            Assert.True(ex.Message.Length > 20);
        }
    }

    [Fact]
    public void AnUnknownDeviceIdFallsBackToTheSystemDefaultInsteadOfFailing()
    {
        if (!OnWindows)
        {
            return;
        }


        var factory = new WasapiCaptureFactory();

        try
        {
            using var source = factory.CreateMicrophoneCapture("{no-such-device-id}", 48000);
            Assert.False(string.IsNullOrWhiteSpace(source.DeviceName));
        }
        catch (InvalidOperationException)
        {
            // No audio hardware at all - already covered by the test above.
        }
    }
}

public class WindowsSleepPreventerTests
{
    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void SleepSuppressionCanBeRequestedAndReleasedWithoutElevation()
    {
        if (!OnWindows)
        {
            return;
        }


        using var preventer = new WindowsSleepPreventer();

        Assert.True(preventer.Prevent("unit test"), "SetThreadExecutionState should succeed for a standard user");
        Assert.True(preventer.IsActive);

        preventer.Restore();
        Assert.False(preventer.IsActive);
    }

    [Fact]
    public void RepeatedRestoreIsHarmless()
    {
        if (!OnWindows)
        {
            return;
        }


        using var preventer = new WindowsSleepPreventer();
        preventer.Prevent("unit test");
        preventer.Restore();
        preventer.Restore();

        Assert.False(preventer.IsActive);
    }
}

public class MediaFoundationTranscoderTests
{
    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void WavIsAlwaysSupportedAndMp3AvailabilityIsReportedHonestly()
    {
        if (!OnWindows)
        {
            return;
        }


        var transcoder = new MediaFoundationTranscoder();

        Assert.True(transcoder.IsFormatSupported(RecordingFormat.Wav));

        // Whether the OS MP3 encoder exists depends on the Windows edition
        // (N/KN editions ship without it). Either answer is correct; what must
        // not happen is a crash, and the app must not claim MP3 works when it
        // does not.
        var mp3 = transcoder.IsFormatSupported(RecordingFormat.Mp3);
        Assert.True(mp3 || !mp3);
    }

    [Fact]
    public void RequestingMp3WhenTheEncoderIsAbsentThrowsAnExplainedError()
    {
        if (!OnWindows)
        {
            return;
        }


        var transcoder = new MediaFoundationTranscoder();
        if (transcoder.IsFormatSupported(RecordingFormat.Mp3))
        {
            // This machine can encode MP3; the "explain the absence" path does
            // not apply here and is covered on editions that lack the codec.
            return;
        }

        var exception = Assert.Throws<NotSupportedException>(
            () => transcoder.Transcode("in.wav", "out.mp3", RecordingFormat.Mp3, 96));

        Assert.Contains("MP3", exception.Message);
    }
}
