using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecorder.Audio;

/// <summary>Captures the microphone (or any other active capture endpoint).</summary>
public sealed class WasapiMicrophoneCaptureSource : WasapiCaptureSource
{
    public WasapiMicrophoneCaptureSource(string? deviceId, int targetSampleRate, ILogger? logger = null)
        : base(deviceId, targetSampleRate, logger)
    {
    }

    public override AudioSourceKind Kind => AudioSourceKind.Microphone;

    protected override string DisplayLabel => "マイク";

    protected override (MMDevice Device, string Name) ResolveDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active)
                {
                    return (device, device.FriendlyName);
                }

                device.Dispose();
            }
            catch (Exception)
            {
                // The saved device is gone; fall through to the system default so
                // a meeting can still be recorded.
            }
        }

        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
        {
            throw new InvalidOperationException(
                "利用可能なマイク（録音デバイス）が見つかりません。マイクを接続し、Windowsのサウンド設定で有効にしてください。");
        }

        var fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        return (fallback, fallback.FriendlyName);
    }

    protected override IWaveIn CreateCapture(MMDevice device)
    {
        // 100 ms, not the 20 ms this used to ask for. WASAPI does not wait for a
        // late client: when the capture buffer fills before the callback has
        // drained it, Windows overwrites the oldest audio and the words in it are
        // gone. A 20 ms buffer gives a managed callback 20 ms to be scheduled,
        // which on a laptop that is also running a conference client is not a
        // safe assumption.
        //
        // Nothing downstream wants the shorter buffer: the pipeline has a 30
        // second ring of its own, the level meters are updated from the same
        // callback either way, and the recording is not being monitored live.
        return new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 100);
    }
}

/// <summary>
/// Captures everything Windows plays through a render endpoint (WASAPI loopback).
/// </summary>
/// <remarks>
/// This is the requirement's "PC内部音声": not a per-process capture, but the
/// whole playback mix - Zoom, Teams, Meet, the browser, notification sounds.
/// It needs no driver and no elevation.
/// <para>
/// One real-world behaviour worth knowing: while the endpoint is completely
/// silent, Windows may deliver nothing at all rather than a stream of zeroes.
/// The pipeline treats a starved stream as silence on the master clock, so the
/// recording timeline stays correct either way.
/// </para>
/// </remarks>
public sealed class WasapiSystemAudioCaptureSource : WasapiCaptureSource
{
    public WasapiSystemAudioCaptureSource(string? deviceId, int targetSampleRate, ILogger? logger = null)
        : base(deviceId, targetSampleRate, logger)
    {
    }

    public override AudioSourceKind Kind => AudioSourceKind.SystemAudio;

    protected override string DisplayLabel => "PC内部音声";

    protected override (MMDevice Device, string Name) ResolveDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                var device = enumerator.GetDevice(deviceId);
                if (device.State == DeviceState.Active)
                {
                    return (device, device.FriendlyName);
                }

                device.Dispose();
            }
            catch (Exception)
            {
            }
        }

        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
        {
            throw new InvalidOperationException(
                "利用可能な再生デバイスが見つかりません。スピーカーまたはヘッドセットを接続してください。"
                + "PC内部音声は再生デバイスのループバックとして取得するため、再生デバイスが必須です。");
        }

        var fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        return (fallback, fallback.FriendlyName);
    }

    protected override IWaveIn CreateCapture(MMDevice device) => new WasapiLoopbackCapture(device);
}

/// <summary>Lists the endpoints the wizard and settings screen offer.</summary>
public sealed class WasapiDeviceProvider : IAudioDeviceProvider
{
    private readonly ILogger _logger;

    public WasapiDeviceProvider(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
        => Enumerate(DataFlow.Capture, Role.Communications, AudioSourceKind.Microphone);

    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
        => Enumerate(DataFlow.Render, Role.Console, AudioSourceKind.SystemAudio);

    private IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow, Role role, AudioSourceKind kind)
    {
        var result = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            if (enumerator.HasDefaultAudioEndpoint(flow, role))
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, role);
                defaultId = defaultDevice.ID;
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId, kind));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(WasapiDeviceProvider), $"Enumerating {flow} endpoints failed.", ex);
        }

        return result;
    }
}

/// <summary>Produces the real WASAPI capture sources for the pipeline.</summary>
public sealed class WasapiCaptureFactory : IAudioCaptureFactory
{
    private readonly ILogger _logger;

    public WasapiCaptureFactory(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public IAudioCaptureSource CreateMicrophoneCapture(string? deviceId, int targetSampleRate)
        => new WasapiMicrophoneCaptureSource(deviceId, targetSampleRate, _logger);

    public IAudioCaptureSource CreateSystemAudioCapture(string? deviceId, int targetSampleRate)
        => new WasapiSystemAudioCaptureSource(deviceId, targetSampleRate, _logger);
}
