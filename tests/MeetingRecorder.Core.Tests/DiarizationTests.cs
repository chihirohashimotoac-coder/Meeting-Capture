using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Tests.TestSupport;
using MeetingRecorder.Diarization;
using Xunit;

namespace MeetingRecorder.Core.Tests;

public class DiarizationTests
{
    // Two clearly different synthetic voices: a low fundamental with low
    // formants against a high fundamental with high formants. This is a sanity
    // check on the clustering mechanics, not a claim about real-world accuracy.
    private static float[] VoiceA(double seconds = 2.0) => SignalGenerator.Voice(110, 700, 1200, seconds, 16000, 0.4, seed: 1);

    private static float[] VoiceB(double seconds = 2.0) => SignalGenerator.Voice(210, 450, 2400, seconds, 16000, 0.4, seed: 2);

    [Fact]
    public void TheSameVoiceKeepsTheSameClusterId()
    {
        using var diarizer = new OnlineSpeakerDiarizer();

        var first = diarizer.Identify(AudioSourceKind.SystemAudio, VoiceA());
        var second = diarizer.Identify(AudioSourceKind.SystemAudio, VoiceA(1.5));

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void TwoDifferentVoicesGetDifferentClusterIds()
    {
        using var diarizer = new OnlineSpeakerDiarizer();

        var a = diarizer.Identify(AudioSourceKind.SystemAudio, VoiceA());
        var b = diarizer.Identify(AudioSourceKind.SystemAudio, VoiceB());

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ClusterSpacesAreSeparatePerCaptureStream()
    {
        using var diarizer = new OnlineSpeakerDiarizer();

        var mic = diarizer.Identify(AudioSourceKind.Microphone, VoiceA());
        var system = diarizer.Identify(AudioSourceKind.SystemAudio, VoiceB());

        // Both start their own numbering; the stream itself is the outer label
        // and is never inferred from the audio.
        Assert.Equal(0, mic);
        Assert.Equal(0, system);
    }

    [Fact]
    public void RefusesToAttributeAudioThatIsTooShort()
    {
        using var diarizer = new OnlineSpeakerDiarizer();
        Assert.Null(diarizer.Identify(AudioSourceKind.Microphone, VoiceA(0.2)));
    }

    [Fact]
    public void RefusesToAttributeNearSilence()
    {
        using var diarizer = new OnlineSpeakerDiarizer();
        Assert.Null(diarizer.Identify(AudioSourceKind.Microphone, SignalGenerator.Noise(2.0, 16000, 0.0002)));
    }

    [Fact]
    public void WhenDisabledItAttributesNothingAtAll()
    {
        using var diarizer = new OnlineSpeakerDiarizer { IsEnabled = false };
        Assert.Null(diarizer.Identify(AudioSourceKind.SystemAudio, VoiceA()));
    }

    [Fact]
    public void ClusterCountIsBoundedSoOneMeetingCannotProduceEndlessSpeakers()
    {
        using var diarizer = new OnlineSpeakerDiarizer(similarityThreshold: 0.999, maxClustersPerSource: 3);

        var ids = new HashSet<int>();
        for (var i = 0; i < 20; i++)
        {
            var id = diarizer.Identify(AudioSourceKind.SystemAudio, SignalGenerator.Voice(100 + (i * 13), 500 + (i * 40), 1500 + (i * 60), 2.0, 16000, 0.4, seed: i));
            if (id.HasValue)
            {
                ids.Add(id.Value);
            }
        }

        Assert.True(ids.Count <= 3, $"expected at most 3 clusters, got {ids.Count}");
    }

    [Fact]
    public void ResetForgetsEveryLearnedSpeaker()
    {
        using var diarizer = new OnlineSpeakerDiarizer();
        diarizer.Identify(AudioSourceKind.SystemAudio, VoiceA());
        diarizer.Identify(AudioSourceKind.SystemAudio, VoiceB());

        diarizer.Reset();

        Assert.Equal(0, diarizer.Identify(AudioSourceKind.SystemAudio, VoiceB()));
    }

    [Fact]
    public void MfccProducesOneVectorPerFrame()
    {
        var mfcc = new Mfcc();
        var frames = mfcc.Compute(VoiceA(1.0));

        // 1 s at a 10 ms hop with a 25 ms window.
        Assert.InRange(frames.Count, 90, 100);
        Assert.All(frames, frame => Assert.Equal(13, frame.Length));
    }

    [Fact]
    public void FftMatchesADirectDiscreteFourierTransform()
    {
        const int n = 64;
        var real = new double[n];
        var imaginary = new double[n];
        for (var i = 0; i < n; i++)
        {
            real[i] = Math.Sin(2 * Math.PI * 5 * i / n) + (0.5 * Math.Cos(2 * Math.PI * 11 * i / n));
        }

        var expected = new double[n];
        for (var k = 0; k < n; k++)
        {
            double re = 0, im = 0;
            for (var t = 0; t < n; t++)
            {
                var angle = -2.0 * Math.PI * k * t / n;
                re += real[t] * Math.Cos(angle);
                im += real[t] * Math.Sin(angle);
            }

            expected[k] = Math.Sqrt((re * re) + (im * im));
        }

        Mfcc.Fft(real, imaginary);

        for (var k = 0; k < n; k++)
        {
            var magnitude = Math.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k]));
            Assert.InRange(magnitude - expected[k], -1e-6, 1e-6);
        }
    }
}
