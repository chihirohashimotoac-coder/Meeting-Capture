using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Pipeline;

/// <summary>
/// Opens both capture streams purely to drive the level meters while the
/// application is idle.
/// </summary>
/// <remarks>
/// The requirement is specific: the user must be able to see - before pressing
/// record - that the microphone is picking something up and that PC audio is
/// reaching the loopback endpoint. Discovering after a one hour meeting that one
/// side was silent is the failure this prevents.
/// <para>
/// Nothing is written to disk here and no processing is applied: the meters show
/// the raw input, which is what tells the user whether the signal is actually
/// arriving. The monitor is stopped before recording starts so the two never
/// hold the same endpoint at once.
/// </para>
/// </remarks>
public sealed class LevelMonitor : IDisposable
{
    private readonly IAudioCaptureFactory _factory;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly LevelMeter _micMeter = new();
    private readonly LevelMeter _systemMeter = new();

    private IAudioCaptureSource? _mic;
    private IAudioCaptureSource? _system;
    private DateTime _lastMicData = DateTime.MinValue;
    private DateTime _lastSystemData = DateTime.MinValue;
    private bool _disposed;

    public LevelMonitor(IAudioCaptureFactory factory, ILogger? logger = null)
    {
        _factory = factory;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsRunning { get; private set; }

    /// <summary>Set when the microphone could not be opened; shown in the UI.</summary>
    public string? MicrophoneError { get; private set; }

    /// <summary>Set when the render endpoint could not be opened for loopback.</summary>
    public string? SystemAudioError { get; private set; }

    public LevelSnapshot MicrophoneLevel => _micMeter.Read();

    public LevelSnapshot SystemAudioLevel => _systemMeter.Read();

    /// <summary>
    /// True when the microphone has produced no data at all recently. Distinct
    /// from "quiet": it means the stream is not delivering.
    /// </summary>
    public bool MicrophoneStalled => IsRunning && _mic is not null && DateTime.UtcNow - _lastMicData > TimeSpan.FromSeconds(3);

    public void Start(AppSettings settings)
    {
        lock (_sync)
        {
            if (_disposed || IsRunning)
            {
                return;
            }

            MicrophoneError = null;
            SystemAudioError = null;

            _mic = TryOpen(() => _factory.CreateMicrophoneCapture(settings.MicrophoneDeviceId, 48000), AudioSourceKind.Microphone);
            _system = TryOpen(() => _factory.CreateSystemAudioCapture(settings.RenderDeviceId, 48000), AudioSourceKind.SystemAudio);

            IsRunning = _mic is not null || _system is not null;
        }
    }

    private IAudioCaptureSource? TryOpen(Func<IAudioCaptureSource> factory, AudioSourceKind kind)
    {
        try
        {
            var source = factory();
            if (kind == AudioSourceKind.Microphone)
            {
                source.DataAvailable += OnMicData;
            }
            else
            {
                source.DataAvailable += OnSystemData;
            }

            source.Start();
            return source;
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(LevelMonitor), $"Monitoring {kind} is unavailable: {ex.Message}");
            if (kind == AudioSourceKind.Microphone)
            {
                MicrophoneError = ex.Message;
            }
            else
            {
                SystemAudioError = ex.Message;
            }

            return null;
        }
    }

    private void OnMicData(ReadOnlySpan<float> samples)
    {
        _lastMicData = DateTime.UtcNow;
        _micMeter.Update(samples);
    }

    private void OnSystemData(ReadOnlySpan<float> samples)
    {
        _lastSystemData = DateTime.UtcNow;
        _systemMeter.Update(samples);
    }

    /// <summary>
    /// Lets the meters fall back towards silence when a stream stops delivering.
    /// WASAPI loopback sends nothing at all while the PC is quiet, so without
    /// this the meter would freeze at the last level it saw.
    /// </summary>
    public void Tick(TimeSpan elapsed)
    {
        if (!IsRunning)
        {
            return;
        }

        if (DateTime.UtcNow - _lastMicData > TimeSpan.FromMilliseconds(150))
        {
            _micMeter.Decay(elapsed);
        }

        if (DateTime.UtcNow - _lastSystemData > TimeSpan.FromMilliseconds(150))
        {
            _systemMeter.Decay(elapsed);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_mic is not null)
            {
                _mic.DataAvailable -= OnMicData;
                _mic.Stop();
                _mic.Dispose();
                _mic = null;
            }

            if (_system is not null)
            {
                _system.DataAvailable -= OnSystemData;
                _system.Stop();
                _system.Dispose();
                _system = null;
            }

            _micMeter.Reset();
            _systemMeter.Reset();
            IsRunning = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
