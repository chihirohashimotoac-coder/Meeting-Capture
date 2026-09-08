using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Stt;

namespace MeetingRecorder.Core.Tests.TestSupport;

/// <summary>
/// A capture source driven by a background thread that produces a repeating
/// signal in real time, so the pipeline can be exercised end to end on a runner
/// with no audio hardware at all.
/// </summary>
public sealed class FakeCaptureSource : IAudioCaptureSource
{
    private readonly float[] _pattern;
    private readonly int _blockSamples;
    private readonly double _clockScale;
    private readonly Exception? _startFailure;
    private Thread? _thread;
    private volatile bool _running;
    private int _position;

    public FakeCaptureSource(
        AudioSourceKind kind,
        float[] pattern,
        int sampleRate = 48000,
        string deviceName = "Fake device",
        double clockScale = 1.0,
        int blockMilliseconds = 20,
        Exception? startFailure = null)
    {
        _startFailure = startFailure;
        Kind = kind;
        _pattern = pattern.Length == 0 ? new float[sampleRate] : pattern;
        SampleRate = sampleRate;
        DeviceName = deviceName;
        _clockScale = clockScale;
        _blockSamples = sampleRate * blockMilliseconds / 1000;
    }

    public AudioSourceKind Kind { get; }

    public int SampleRate { get; }

    public string DeviceName { get; }

    public string? DeviceId => "fake";

    public bool IsCapturing => _running;

    public event AudioDataHandler? DataAvailable;

    public event EventHandler<CaptureFault>? Fault;

    public void Start()
    {
        // A device that enumerates and then refuses to open. WASAPI does exactly
        // this when Windows privacy settings deny microphone access: the
        // endpoint is listed, and AudioClient.Initialize returns E_ACCESSDENIED.
        if (_startFailure is not null)
        {
            throw _startFailure;
        }

        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = $"Fake-{Kind}" };
        _thread.Start();
    }

    private void Loop()
    {
        var buffer = new float[_blockSamples];
        var start = DateTime.UtcNow;
        long produced = 0;

        while (_running)
        {
            // The clock scale lets a test simulate a device whose crystal runs
            // fast or slow, which is what the drift compensator exists for.
            var target = (long)((DateTime.UtcNow - start).TotalSeconds * SampleRate * _clockScale);
            if (produced >= target)
            {
                Thread.Sleep(5);
                continue;
            }

            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = _pattern[_position];
                _position = (_position + 1) % _pattern.Length;
            }

            DataAvailable?.Invoke(buffer);
            produced += buffer.Length;
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void RaiseFault(CaptureFault fault) => Fault?.Invoke(this, fault);

    public void Dispose() => Stop();
}

/// <summary>Capture factory that hands out <see cref="FakeCaptureSource"/> instances.</summary>
public sealed class FakeCaptureFactory : IAudioCaptureFactory
{
    private readonly float[] _micPattern;
    private readonly float[] _systemPattern;
    private readonly bool _failMicrophone;
    private readonly bool _failSystem;
    private readonly double _micClockScale;
    private readonly Exception? _microphoneStartFailure;
    private readonly Exception? _systemStartFailure;

    public FakeCaptureFactory(
        float[]? micPattern = null,
        float[]? systemPattern = null,
        bool failMicrophone = false,
        bool failSystem = false,
        double micClockScale = 1.0,
        Exception? microphoneStartFailure = null,
        Exception? systemStartFailure = null)
    {
        _microphoneStartFailure = microphoneStartFailure;
        _systemStartFailure = systemStartFailure;
        _micPattern = micPattern ?? SignalGenerator.Sine(220, 1.0, 48000, 0.2);
        _systemPattern = systemPattern ?? SignalGenerator.Sine(660, 1.0, 48000, 0.2);
        _failMicrophone = failMicrophone;
        _failSystem = failSystem;
        _micClockScale = micClockScale;
    }

    public List<FakeCaptureSource> Created { get; } = new();

    public IAudioCaptureSource CreateMicrophoneCapture(string? deviceId, int targetSampleRate)
    {
        if (_failMicrophone)
        {
            throw new InvalidOperationException("マイクを開けません（テスト）。");
        }

        var source = new FakeCaptureSource(
            AudioSourceKind.Microphone, _micPattern, targetSampleRate, "Fake microphone", _micClockScale,
            startFailure: _microphoneStartFailure);
        Created.Add(source);
        return source;
    }

    public IAudioCaptureSource CreateSystemAudioCapture(string? deviceId, int targetSampleRate)
    {
        if (_failSystem)
        {
            throw new InvalidOperationException("PC内部音声を開けません（テスト）。");
        }

        var source = new FakeCaptureSource(
            AudioSourceKind.SystemAudio, _systemPattern, targetSampleRate, "Fake speakers",
            startFailure: _systemStartFailure);
        Created.Add(source);
        return source;
    }
}

/// <summary>
/// A recognizer that reports what it was given instead of pretending to
/// transcribe it.
/// </summary>
/// <remarks>
/// This is a test double and lives only in the test assembly. The shipping
/// application never substitutes fabricated text for real recognition - if no
/// model is available it records without a transcript and says so.
/// </remarks>
public sealed class FakeSpeechRecognizer : ISpeechRecognizer
{
    private readonly TimeSpan _delay;
    private int _counter;

    public FakeSpeechRecognizer(TimeSpan? delay = null)
    {
        _delay = delay ?? TimeSpan.Zero;
    }

    public string ModelId => "fake-model";

    public bool IsReady { get; set; } = true;

    public List<int> ReceivedSampleCounts { get; } = new();

    public IReadOnlyList<RecognizedSpan> Transcribe(ReadOnlySpan<float> samples, string language, CancellationToken cancellationToken)
    {
        lock (ReceivedSampleCounts)
        {
            ReceivedSampleCounts.Add(samples.Length);
        }

        if (_delay > TimeSpan.Zero)
        {
            Thread.Sleep(_delay);
        }

        var index = Interlocked.Increment(ref _counter);
        var durationMs = (long)(samples.Length * 1000L / SpeechConstants.SampleRate);
        return new[] { new RecognizedSpan(0, durationMs, $"chunk-{index}", 0.9f) };
    }

    public void Dispose()
    {
    }
}
