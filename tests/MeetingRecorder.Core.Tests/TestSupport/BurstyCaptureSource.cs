using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Tests.TestSupport;

/// <summary>
/// A capture source that delivers audio the way WASAPI actually does: in bursts
/// of a whole buffer at a time, at irregular intervals.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FakeCaptureSource"/> hands the pipeline a tidy 20 ms block every
/// 20 ms, which no real endpoint does. NAudio's capture thread wakes on a buffer
/// event (or, for loopback, on a timer set to one and a half buffer periods),
/// reads every packet that has queued up, and raises them together - so the
/// pipeline sees a burst of 100 ms and then nothing for 100 ms. Add ordinary
/// thread scheduling on a busy laptop and the gap between bursts is routinely
/// longer than the burst itself.
/// </para>
/// <para>
/// This source reproduces that: the same average rate, delivered in bursts, with
/// an occasional late burst standing in for a scheduling hiccup. Audio that
/// survives this arrives intact on real hardware; audio that does not, does not.
/// </para>
/// </remarks>
public sealed class BurstyCaptureSource : IAudioCaptureSource
{
    private readonly float[] _pattern;
    private readonly int _burstSamples;
    private readonly int _burstMs;
    private readonly int _lateBurstEvery;
    private readonly int _lateByMs;
    private Thread? _thread;
    private volatile bool _running;
    private int _position;

    public BurstyCaptureSource(
        AudioSourceKind kind,
        float[] pattern,
        int sampleRate = 48000,
        int burstMilliseconds = 100,
        int lateBurstEvery = 7,
        int lateByMilliseconds = 60)
    {
        Kind = kind;
        _pattern = pattern.Length == 0 ? new float[sampleRate] : pattern;
        SampleRate = sampleRate;
        _burstMs = burstMilliseconds;
        _burstSamples = sampleRate * burstMilliseconds / 1000;
        _lateBurstEvery = lateBurstEvery;
        _lateByMs = lateByMilliseconds;
    }

    public AudioSourceKind Kind { get; }

    public int SampleRate { get; }

    public string DeviceName => "Bursty device";

    public string? DeviceId => "bursty";

    public bool IsCapturing => _running;

    public event AudioDataHandler? DataAvailable;

    public event EventHandler<CaptureFault>? Fault;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = $"Bursty-{Kind}" };
        _thread.Start();
    }

    private void Loop()
    {
        var buffer = new float[_burstSamples];
        var start = DateTime.UtcNow;
        long produced = 0;
        var bursts = 0;

        while (_running)
        {
            var due = start.AddMilliseconds((double)bursts * _burstMs);
            if (_lateBurstEvery > 0 && bursts > 0 && bursts % _lateBurstEvery == 0)
            {
                due = due.AddMilliseconds(_lateByMs);
            }

            var wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
                continue;
            }

            // A real capture never loses audio because the callback was late; it
            // simply hands over everything that queued up in the meantime.
            var elapsed = (DateTime.UtcNow - start).TotalSeconds;
            var owed = (long)(elapsed * SampleRate) - produced;
            while (owed > 0 && _running)
            {
                var take = (int)Math.Min(owed, buffer.Length);
                for (var i = 0; i < take; i++)
                {
                    buffer[i] = _pattern[_position];
                    _position = (_position + 1) % _pattern.Length;
                }

                DataAvailable?.Invoke(buffer.AsSpan(0, take));
                produced += take;
                owed -= take;
            }

            bursts++;
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

/// <summary>Hands the pipeline <see cref="BurstyCaptureSource"/> instances.</summary>
public sealed class BurstyCaptureFactory : IAudioCaptureFactory
{
    private readonly float[] _micPattern;
    private readonly float[] _systemPattern;
    private readonly int _burstMilliseconds;

    public BurstyCaptureFactory(float[] micPattern, float[] systemPattern, int burstMilliseconds = 100)
    {
        _micPattern = micPattern;
        _systemPattern = systemPattern;
        _burstMilliseconds = burstMilliseconds;
    }

    public IAudioCaptureSource CreateMicrophoneCapture(string? deviceId, int targetSampleRate)
        => new BurstyCaptureSource(AudioSourceKind.Microphone, _micPattern, targetSampleRate, _burstMilliseconds);

    public IAudioCaptureSource CreateSystemAudioCapture(string? deviceId, int targetSampleRate)
        => new BurstyCaptureSource(AudioSourceKind.SystemAudio, _systemPattern, targetSampleRate, _burstMilliseconds);
}
