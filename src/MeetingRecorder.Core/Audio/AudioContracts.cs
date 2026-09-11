using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Audio;

/// <summary>Handler for a block of mono float samples at the engine sample rate.</summary>
public delegate void AudioDataHandler(ReadOnlySpan<float> samples);

/// <summary>Describes an audio endpoint the user can pick in the wizard.</summary>
/// <param name="Id">Stable Windows MMDevice id, or a synthetic id for fakes.</param>
/// <param name="Name">Friendly name shown in the UI.</param>
/// <param name="IsDefault">True when this is the current Windows default endpoint.</param>
/// <param name="Kind">Whether the endpoint is captured directly or via loopback.</param>
public readonly record struct AudioDeviceInfo(string Id, string Name, bool IsDefault, AudioSourceKind Kind);

/// <summary>Why a capture stream stopped or degraded.</summary>
public enum CaptureFaultKind
{
    /// <summary>The endpoint disappeared (USB unplugged, Bluetooth dropped).</summary>
    DeviceRemoved,

    /// <summary>Windows switched the default endpoint while recording.</summary>
    DefaultDeviceChanged,

    /// <summary>The endpoint is present but the stream stopped delivering data.</summary>
    StreamStalled,

    /// <summary>Anything else, carried in the message.</summary>
    Unknown,
}

/// <param name="Kind">Machine-readable fault category.</param>
/// <param name="Message">Message shown to the user, in Japanese, with a remedy.</param>
/// <param name="Recovered">True when the engine already reconnected automatically.</param>
public readonly record struct CaptureFault(CaptureFaultKind Kind, string Message, bool Recovered);

/// <summary>
/// A capture stream that produces mono float samples at
/// <see cref="SampleRate"/>. Implemented by the WASAPI classes on Windows and by
/// deterministic fakes in the test suite.
/// </summary>
public interface IAudioCaptureSource : IDisposable
{
    AudioSourceKind Kind { get; }

    /// <summary>Sample rate of the data handed to <see cref="DataAvailable"/>.</summary>
    int SampleRate { get; }

    string DeviceName { get; }

    string? DeviceId { get; }

    bool IsCapturing { get; }

    event AudioDataHandler? DataAvailable;

    event EventHandler<CaptureFault>? Fault;

    void Start();

    void Stop();
}

/// <summary>Lists the endpoints offered by the first-run wizard and settings screen.</summary>
public interface IAudioDeviceProvider
{
    IReadOnlyList<AudioDeviceInfo> GetCaptureDevices();

    IReadOnlyList<AudioDeviceInfo> GetRenderDevices();
}

/// <summary>
/// Creates capture sources. Keeping this behind an interface is what lets the
/// whole recording pipeline be exercised on a CI runner that has no audio
/// hardware at all.
/// </summary>
public interface IAudioCaptureFactory
{
    IAudioCaptureSource CreateMicrophoneCapture(string? deviceId, int targetSampleRate);

    IAudioCaptureSource CreateSystemAudioCapture(string? deviceId, int targetSampleRate);
}

/// <summary>Incrementally writes the mixed meeting track to disk.</summary>
public interface IAudioFileWriter : IDisposable
{
    string FilePath { get; }

    int SampleRate { get; }

    int Channels { get; }

    long SamplesWritten { get; }

    double DurationSeconds { get; }

    void Write(ReadOnlySpan<float> samples);

    /// <summary>
    /// Pushes buffered data to disk and leaves the file in a state that is
    /// playable even if the process is killed immediately afterwards.
    /// </summary>
    void Flush();
}

/// <summary>Transcodes the crash-safe WAV into the user's chosen delivery format.</summary>
/// <param name="Path">The file that was produced.</param>
/// <param name="BitrateKbps">
/// The bitrate actually used, which is not always the one that was asked for:
/// an encoder offers a fixed set of output formats and the nearest one is taken.
/// Reported so the application can say what it did rather than what it intended.
/// </param>
public readonly record struct TranscodeResult(string Path, int BitrateKbps);

public interface IAudioTranscoder
{
    bool IsFormatSupported(RecordingFormat format);

    /// <summary>
    /// Converts <paramref name="sourceWavPath"/> and returns the produced path
    /// together with the bitrate that was actually achieved.
    /// Implementations must not delete the source; the caller decides.
    /// </summary>
    TranscodeResult Transcode(string sourceWavPath, string destinationPath, RecordingFormat format, int bitrateKbps);
}
