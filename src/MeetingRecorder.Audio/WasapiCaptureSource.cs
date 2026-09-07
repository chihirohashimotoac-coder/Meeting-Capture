using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecorder.Audio;

/// <summary>
/// Shared implementation for the two WASAPI capture paths: a normal capture
/// endpoint (the microphone) and a render endpoint captured in loopback mode
/// (everything Windows is playing).
/// </summary>
/// <remarks>
/// <para><b>Why WASAPI shared-mode loopback.</b> It is the only way to record
/// "whatever the PC is playing" that needs no driver installation, no
/// administrator rights and no cooperation from Zoom, Teams or the browser.
/// Stereo Mix depends on the sound card vendor and is absent on most modern
/// laptops; VB-CABLE and similar virtual devices need an installer and admin
/// rights. Both are therefore ruled out by the requirements, and neither is used
/// or needed here.</para>
///
/// <para><b>Format handling.</b> Shared mode hands over whatever the endpoint's
/// mix format is - typically 32-bit float, 2 channels, 48 kHz, but 44.1 kHz and
/// 24-bit are common on USB headsets. The class converts to the engine's mono
/// float at 48 kHz inside the callback, which is cheap, and hands the pipeline a
/// single canonical format.</para>
///
/// <para><b>Callback discipline.</b> The WASAPI callback must return quickly or
/// the next packet is dropped by Windows. It therefore only converts and copies;
/// all DSP, disk I/O and recognition happen on other threads.</para>
/// </remarks>
public abstract class WasapiCaptureSource : IAudioCaptureSource
{
    private readonly ILogger _logger;
    private readonly int _targetSampleRate;
    private readonly object _sync = new();

    private IWaveIn? _capture;
    private MMDevice? _device;
    private Resampler? _resampler;
    private float[] _monoBuffer = Array.Empty<float>();
    private float[] _resampleBuffer = Array.Empty<float>();
    private int _sourceChannels;
    private bool _shouldBeRunning;
    private int _restartAttempts;
    private bool _disposed;

    protected WasapiCaptureSource(string? deviceId, int targetSampleRate, ILogger? logger)
    {
        DeviceId = deviceId;
        _targetSampleRate = targetSampleRate;
        _logger = logger ?? NullLogger.Instance;

        // Resolve eagerly so that a missing device is reported by the pre-flight
        // check rather than by a silent recording.
        (_device, DeviceName) = ResolveDevice(deviceId);
    }

    /// <summary>Maximum automatic reconnection attempts after a device drops out.</summary>
    protected const int MaxRestartAttempts = 5;

    public abstract AudioSourceKind Kind { get; }

    public int SampleRate => _targetSampleRate;

    public string DeviceName { get; private set; }

    public string? DeviceId { get; private set; }

    public bool IsCapturing { get; private set; }

    public event AudioDataHandler? DataAvailable;

    public event EventHandler<CaptureFault>? Fault;

    protected ILogger Logger => _logger;

    /// <summary>Resolves the endpoint to capture; implemented per capture kind.</summary>
    protected abstract (MMDevice Device, string Name) ResolveDevice(string? deviceId);

    /// <summary>Creates the NAudio capture object for the resolved endpoint.</summary>
    protected abstract IWaveIn CreateCapture(MMDevice device);

    /// <summary>Human readable label used in fault messages ("マイク" / "PC内部音声").</summary>
    protected abstract string DisplayLabel { get; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (IsCapturing)
            {
                return;
            }

            _shouldBeRunning = true;
            _restartAttempts = 0;
            StartCore();
        }
    }

    private void StartCore()
    {
        _device ??= ResolveDevice(DeviceId).Device;

        var capture = CreateCapture(_device);
        var format = capture.WaveFormat;
        _sourceChannels = Math.Max(1, format.Channels);
        _resampler = new Resampler(format.SampleRate, _targetSampleRate);

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        _capture = capture;
        capture.StartRecording();
        IsCapturing = true;

        _logger.Info(
            GetType().Name,
            $"Capture started on '{DeviceName}' ({format.SampleRate} Hz, {format.Channels} ch, {format.Encoding}) -> {_targetSampleRate} Hz mono.");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        try
        {
            var capture = _capture;
            if (capture is null)
            {
                return;
            }

            var format = capture.WaveFormat;
            var frames = ConvertToMono(e.Buffer, e.BytesRecorded, format);
            if (frames == 0)
            {
                return;
            }

            var resampler = _resampler;
            if (resampler is null || resampler.IsPassThrough)
            {
                DataAvailable?.Invoke(_monoBuffer.AsSpan(0, frames));
                return;
            }

            var needed = resampler.EstimateOutputLength(frames);
            if (_resampleBuffer.Length < needed)
            {
                _resampleBuffer = new float[needed];
            }

            var written = resampler.Process(_monoBuffer.AsSpan(0, frames), _resampleBuffer);
            DataAvailable?.Invoke(_resampleBuffer.AsSpan(0, written));
        }
        catch (Exception ex)
        {
            // Never let an exception escape into the WASAPI callback: it would
            // tear down the capture thread and take the recording with it.
            _logger.Error(GetType().Name, "Capture callback failed.", ex);
        }
    }

    /// <summary>Converts an interleaved WASAPI buffer into <see cref="_monoBuffer"/>.</summary>
    private int ConvertToMono(byte[] buffer, int byteCount, WaveFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample == 0)
        {
            return 0;
        }

        var totalSamples = byteCount / bytesPerSample;
        var frames = totalSamples / _sourceChannels;
        if (frames <= 0)
        {
            return 0;
        }

        if (_monoBuffer.Length < frames)
        {
            _monoBuffer = new float[frames];
        }

        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                      || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < _sourceChannels; channel++)
            {
                var offset = ((frame * _sourceChannels) + channel) * bytesPerSample;
                sum += ReadSample(buffer, offset, format.BitsPerSample, isFloat);
            }

            _monoBuffer[frame] = (float)(sum / _sourceChannels);
        }

        return frames;
    }

    private static double ReadSample(byte[] buffer, int offset, int bitsPerSample, bool isFloat) => bitsPerSample switch
    {
        16 => BitConverter.ToInt16(buffer, offset) / 32768.0,
        24 => (buffer[offset] | (buffer[offset + 1] << 8) | ((sbyte)buffer[offset + 2] << 16)) / 8388608.0,
        32 => isFloat ? BitConverter.ToSingle(buffer, offset) : BitConverter.ToInt32(buffer, offset) / 2147483648.0,
        8 => (buffer[offset] - 128) / 128.0,
        _ => 0.0,
    };

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsCapturing = false;

        if (!_shouldBeRunning)
        {
            return;
        }

        var reason = e.Exception?.Message ?? "デバイスからのデータ供給が停止しました。";
        _logger.Warn(GetType().Name, $"Capture stopped unexpectedly: {reason}");

        // Reconnect on a worker thread: this callback runs on NAudio's capture
        // thread and must not block while the device settles.
        ThreadPool.QueueUserWorkItem(_ => TryRestart(reason));
    }

    private void TryRestart(string reason)
    {
        lock (_sync)
        {
            if (!_shouldBeRunning || _disposed)
            {
                return;
            }

            DisposeCapture();

            while (_restartAttempts < MaxRestartAttempts)
            {
                _restartAttempts++;

                // Linear back-off: a USB or Bluetooth endpoint usually reappears
                // within a couple of seconds, and a fixed short retry storm would
                // just spin the CPU while Windows re-enumerates.
                Thread.Sleep(TimeSpan.FromMilliseconds(500 * _restartAttempts));

                try
                {
                    _device = null;
                    var resolved = ResolveDevice(DeviceId);
                    _device = resolved.Device;
                    DeviceName = resolved.Name;

                    StartCore();

                    var message = $"{DisplayLabel}のデバイスが切断されましたが、「{DeviceName}」で自動的に再接続しました。";
                    _logger.Info(GetType().Name, message);
                    Fault?.Invoke(this, new CaptureFault(CaptureFaultKind.DeviceRemoved, message, Recovered: true));
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Warn(GetType().Name, $"Reconnect attempt {_restartAttempts} failed: {ex.Message}");
                }
            }

            var failure =
                $"{DisplayLabel}のデバイスが切断され、自動復帰できませんでした（{reason}）。" +
                $"録音は継続していますが、{DisplayLabel}は無音として記録されます。" +
                "デバイスを接続し直したうえで、録音を停止して再開してください。";

            _logger.Error(GetType().Name, failure);
            Fault?.Invoke(this, new CaptureFault(CaptureFaultKind.DeviceRemoved, failure, Recovered: false));
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _shouldBeRunning = false;
            if (_capture is not null)
            {
                try
                {
                    _capture.StopRecording();
                }
                catch (Exception ex)
                {
                    _logger.Warn(GetType().Name, $"StopRecording reported: {ex.Message}");
                }
            }

            DisposeCapture();
            IsCapturing = false;
        }
    }

    private void DisposeCapture()
    {
        if (_capture is null)
        {
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;

        try
        {
            _capture.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn(GetType().Name, $"Disposing the capture reported: {ex.Message}");
        }

        _capture = null;
    }

    protected void RaiseFault(CaptureFault fault) => Fault?.Invoke(this, fault);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _device?.Dispose();
        _device = null;
        GC.SuppressFinalize(this);
    }
}
