using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Diagnostics;

/// <param name="Opened">Whether the endpoint could be opened at all.</param>
/// <param name="DeviceName">The endpoint that was actually used.</param>
/// <param name="Samples">How many samples arrived during the probe.</param>
/// <param name="PeakDb">Loudest sample seen, in dBFS.</param>
/// <param name="RmsDb">Average level, in dBFS.</param>
/// <param name="Error">Why the endpoint could not be opened, when it could not.</param>
public readonly record struct CaptureProbeResult(
    bool Opened,
    string DeviceName,
    long Samples,
    double PeakDb,
    double RmsDb,
    string? Error)
{
    /// <summary>True when data arrived and it was not digital silence.</summary>
    public bool HasSignal => Opened && Samples > 0 && PeakDb > -70.0;

    /// <summary>True when the endpoint delivered samples but they were all silent.</summary>
    public bool SilentButDelivering => Opened && Samples > 0 && PeakDb <= -70.0;
}

/// <summary>
/// Opens a capture endpoint for a few seconds and measures what actually
/// arrives.
/// </summary>
/// <remarks>
/// This is the measurement behind the "microphone / PC audio is or is not
/// reaching the recorder" question. It deliberately distinguishes three
/// outcomes, because they need different advice:
/// <list type="bullet">
/// <item><description>the endpoint would not open at all (device or permission problem);</description></item>
/// <item><description>it opened but delivered nothing (WASAPI loopback does exactly
/// this while the PC is silent - the user simply needs to play something);</description></item>
/// <item><description>it delivered samples that were all zeroes (a real signal path
/// problem, e.g. a muted endpoint).</description></item>
/// </list>
/// </remarks>
public sealed class CaptureProbe
{
    private readonly IAudioCaptureFactory _factory;
    private readonly ILogger _logger;

    public CaptureProbe(IAudioCaptureFactory factory, ILogger? logger = null)
    {
        _factory = factory;
        _logger = logger ?? NullLogger.Instance;
    }

    public CaptureProbeResult Run(AudioSourceKind kind, string? deviceId, TimeSpan duration, int sampleRate = 48000)
    {
        IAudioCaptureSource? source = null;

        try
        {
            source = kind == AudioSourceKind.Microphone
                ? _factory.CreateMicrophoneCapture(deviceId, sampleRate)
                : _factory.CreateSystemAudioCapture(deviceId, sampleRate);
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(CaptureProbe), $"{kind} could not be opened: {ex.Message}");
            return new CaptureProbeResult(false, "-", 0, AudioMath.MinDb, AudioMath.MinDb, ex.Message);
        }

        long samples = 0;
        double peak = 0;
        double sumOfSquares = 0;
        var sync = new object();

        void OnData(ReadOnlySpan<float> block)
        {
            var blockPeak = AudioMath.Peak(block);
            double blockSum = 0;
            for (var i = 0; i < block.Length; i++)
            {
                double value = block[i];
                blockSum += value * value;
            }

            lock (sync)
            {
                samples += block.Length;
                sumOfSquares += blockSum;
                if (blockPeak > peak)
                {
                    peak = blockPeak;
                }
            }
        }

        try
        {
            source.DataAvailable += OnData;
            source.Start();
            Thread.Sleep(duration);
            source.Stop();
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(CaptureProbe), $"{kind} probe failed while capturing: {ex.Message}");
            return new CaptureProbeResult(false, source.DeviceName, samples, AudioMath.MinDb, AudioMath.MinDb, ex.Message);
        }
        finally
        {
            source.DataAvailable -= OnData;
            source.Dispose();
        }

        long finalSamples;
        double finalPeak;
        double finalSum;
        lock (sync)
        {
            finalSamples = samples;
            finalPeak = peak;
            finalSum = sumOfSquares;
        }

        var rms = finalSamples > 0 ? Math.Sqrt(finalSum / finalSamples) : 0.0;

        return new CaptureProbeResult(
            true,
            source.DeviceName,
            finalSamples,
            AudioMath.LinearToDb(finalPeak),
            AudioMath.LinearToDb(rms),
            null);
    }
}
