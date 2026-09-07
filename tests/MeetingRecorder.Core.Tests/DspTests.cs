using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

public class AudioMathTests
{
    [Fact]
    public void RmsOfSineIsAmplitudeOverRootTwo()
    {
        var sine = SignalGenerator.Sine(1000, 1.0, 48000, 0.5);
        var rms = AudioMath.Rms(sine);
        Assert.InRange(rms, 0.5 / Math.Sqrt(2) - 0.005, 0.5 / Math.Sqrt(2) + 0.005);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-6.0)]
    [InlineData(-20.0)]
    [InlineData(-60.0)]
    public void DbConversionRoundTrips(double db)
    {
        Assert.InRange(AudioMath.LinearToDb(AudioMath.DbToLinear(db)), db - 1e-9, db + 1e-9);
    }

    [Fact]
    public void SilenceReportsFloorInsteadOfNegativeInfinity()
    {
        Assert.Equal(AudioMath.MinDb, AudioMath.RmsDb(new float[128]));
    }

    [Fact]
    public void DownmixAveragesChannels()
    {
        var interleaved = new[] { 1f, -1f, 0.5f, 0.5f };
        var mono = new float[2];
        var frames = AudioMath.DownmixToMono(interleaved, 2, mono);

        Assert.Equal(2, frames);
        Assert.Equal(0f, mono[0], 5);
        Assert.Equal(0.5f, mono[1], 5);
    }
}

public class LimiterTests
{
    private static AudioProcessingSettings Settings() => new();

    [Fact]
    public void NeverExceedsTheCeilingEvenForGrosslyHotInput()
    {
        var settings = Settings();
        var limiter = new Limiter(settings, 48000);
        var ceiling = AudioMath.DbToLinear(settings.LimiterCeilingDb);

        // 4x full scale: nothing a real chain produces, which is exactly why the
        // guarantee has to hold for it.
        var hot = SignalGenerator.Sine(440, 2.0, 48000, 4.0);
        limiter.Process(hot);

        Assert.True(AudioMath.Peak(hot) <= ceiling + 1e-6, $"peak {AudioMath.Peak(hot)} exceeded ceiling {ceiling}");
    }

    [Fact]
    public void LeavesQuietSignalEssentiallyUntouched()
    {
        var limiter = new Limiter(Settings(), 48000);
        var quiet = SignalGenerator.Sine(440, 1.0, 48000, 0.1);
        var original = (float[])quiet.Clone();
        limiter.Process(quiet);

        // Compare after the look-ahead delay: the limiter delays the signal.
        var latency = limiter.LatencySamples;
        var errors = 0;
        for (var i = latency; i < quiet.Length; i++)
        {
            if (Math.Abs(quiet[i] - original[i - latency]) > 0.01)
            {
                errors++;
            }
        }

        Assert.True(errors < quiet.Length / 100, $"{errors} samples differed by more than 0.01");
    }

    [Fact]
    public void ClipsHardEvenWhenDisabled()
    {
        var settings = Settings();
        settings.LimiterEnabled = false;
        var limiter = new Limiter(settings, 48000);
        var hot = SignalGenerator.Sine(440, 0.2, 48000, 3.0);
        limiter.Process(hot);

        Assert.True(AudioMath.Peak(hot) <= AudioMath.DbToLinear(settings.LimiterCeilingDb) + 1e-6);
    }
}

public class LoudnessNormalizerTests
{
    [Fact]
    public void RaisesQuietSpeechTowardsTheTarget()
    {
        var settings = new AudioProcessingSettings();
        var normalizer = new LoudnessNormalizer(settings, 48000);

        // -40 dBFS input; the AGC may add at most +18 dB, so it should end up
        // around -22 dBFS, not at the -20 dBFS target.
        var quiet = SignalGenerator.Sine(300, 30.0, 48000, AudioMath.DbToLinear(-40) * Math.Sqrt(2));
        ProcessInBlocks(normalizer, quiet, 4800);

        var tail = quiet.AsSpan(quiet.Length - 48000);
        var level = AudioMath.RmsDb(tail);
        Assert.InRange(level, -24.0, -19.0);
        Assert.InRange(normalizer.CurrentGainDb, settings.MinGainDb, settings.MaxGainDb);
    }

    [Fact]
    public void NeverExceedsTheMaximumGain()
    {
        var settings = new AudioProcessingSettings();
        var normalizer = new LoudnessNormalizer(settings, 48000);
        var verySoft = SignalGenerator.Sine(300, 60.0, 48000, AudioMath.DbToLinear(-70));
        ProcessInBlocks(normalizer, verySoft, 4800);

        Assert.True(normalizer.CurrentGainDb <= settings.MaxGainDb + 1e-6);
    }

    [Fact]
    public void DoesNotWindGainUpOnSilence()
    {
        var settings = new AudioProcessingSettings();
        var normalizer = new LoudnessNormalizer(settings, 48000);
        var silence = SignalGenerator.Silence(30.0, 48000);
        ProcessInBlocks(normalizer, silence, 4800);

        Assert.Equal(0.0, normalizer.CurrentGainDb, 6);
    }

    [Fact]
    public void AdaptsSlowlyEnoughToAvoidPumping()
    {
        var settings = new AudioProcessingSettings();
        var normalizer = new LoudnessNormalizer(settings, 48000);
        var quiet = SignalGenerator.Sine(300, 1.0, 48000, AudioMath.DbToLinear(-40));
        ProcessInBlocks(normalizer, quiet, 4800);

        // One second at 1.5 dB/s must not move the gain more than ~1.5 dB.
        Assert.InRange(normalizer.CurrentGainDb, 0.0, 1.8);
    }

    private static void ProcessInBlocks(LoudnessNormalizer normalizer, float[] samples, int blockSize)
    {
        for (var offset = 0; offset < samples.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, samples.Length - offset);
            normalizer.Process(samples.AsSpan(offset, length));
        }
    }
}

public class NoiseGateTests
{
    [Fact]
    public void AttenuatesBackgroundButNeverMutesIt()
    {
        var settings = new AudioProcessingSettings();
        var gate = new NoiseGate(settings, 48000);

        var background = SignalGenerator.Noise(3.0, 48000, AudioMath.DbToLinear(-70));
        var before = AudioMath.RmsDb(background);
        gate.Process(background);
        var after = AudioMath.RmsDb(background);

        Assert.True(after < before, "the gate should reduce background");
        // Attenuation is bounded, so the background is still present.
        Assert.True(after > before - settings.GateMaxAttenuationDb - 1.0);
        Assert.True(AudioMath.Peak(background) > 0, "the gate must never produce digital silence");
    }

    [Fact]
    public void KeepsShortUtterancesIntactIncludingTheirTail()
    {
        var settings = new AudioProcessingSettings();
        var gate = new NoiseGate(settings, 48000);

        // 200 ms of speech - about the length of a Japanese back-channel "はい".
        var signal = SignalGenerator.Concat(
            SignalGenerator.Noise(1.0, 48000, AudioMath.DbToLinear(-70)),
            SignalGenerator.Sine(300, 0.2, 48000, 0.3),
            SignalGenerator.Noise(1.0, 48000, AudioMath.DbToLinear(-70)));

        var utteranceBefore = AudioMath.RmsDb(signal.AsSpan(48000, 9600));
        gate.Process(signal);
        var utteranceAfter = AudioMath.RmsDb(signal.AsSpan(48000, 9600));

        Assert.True(utteranceAfter > utteranceBefore - 1.5, $"short utterance lost {utteranceBefore - utteranceAfter:F1} dB");
    }
}

public class CompressorTests
{
    [Fact]
    public void ReducesLevelAboveTheThreshold()
    {
        var settings = new AudioProcessingSettings();
        var compressor = new Compressor(settings, 48000);

        var loud = SignalGenerator.Sine(300, 2.0, 48000, AudioMath.DbToLinear(-6));
        var before = AudioMath.RmsDb(loud);
        compressor.Process(loud);
        var after = AudioMath.RmsDb(loud.AsSpan(48000));

        Assert.True(after < before, "signal above the threshold should be reduced");
    }

    [Fact]
    public void LeavesQuietSignalAlone()
    {
        var settings = new AudioProcessingSettings();
        var compressor = new Compressor(settings, 48000);

        var quiet = SignalGenerator.Sine(300, 2.0, 48000, AudioMath.DbToLinear(-40));
        var before = AudioMath.RmsDb(quiet);
        compressor.Process(quiet);
        var after = AudioMath.RmsDb(quiet.AsSpan(48000));

        // Only the fixed auto make-up gain applies below the threshold.
        Assert.InRange(after - before, -0.5, 2.5);
    }
}

public class ResamplerTests
{
    [Fact]
    public void ProducesTheExpectedNumberOfSamples()
    {
        var resampler = new Resampler(48000, 16000);
        var input = SignalGenerator.Sine(400, 1.0, 48000);
        var output = resampler.Process(input);

        Assert.InRange(output.Length, 16000 - 4, 16000 + 4);
    }

    [Fact]
    public void PreservesAnInBandTone()
    {
        var resampler = new Resampler(48000, 16000);
        var input = SignalGenerator.Sine(440, 1.0, 48000, 0.5);
        var output = resampler.Process(input);

        var rmsIn = AudioMath.Rms(input);
        var rmsOut = AudioMath.Rms(output.AsSpan(1000));
        Assert.InRange(rmsOut, rmsIn * 0.8, rmsIn * 1.2);
    }

    [Fact]
    public void SuppressesContentAboveTheNewNyquist()
    {
        var resampler = new Resampler(48000, 16000);
        // 12 kHz would alias to 4 kHz - straight into the speech band - without
        // the anti-alias filter.
        var input = SignalGenerator.Sine(12000, 1.0, 48000, 0.5);
        var output = resampler.Process(input);

        var rmsOut = AudioMath.Rms(output.AsSpan(2000));
        Assert.True(rmsOut < AudioMath.Rms(input) * 0.1, $"aliased energy too high: {AudioMath.LinearToDb(rmsOut):F1} dBFS");
    }

    [Fact]
    public void IsSeamlessAcrossBlockBoundaries()
    {
        var whole = new Resampler(48000, 16000);
        var chunked = new Resampler(48000, 16000);

        var input = SignalGenerator.Sine(440, 0.5, 48000, 0.5);
        var expected = whole.Process(input);

        var actual = new List<float>();
        for (var offset = 0; offset < input.Length; offset += 960)
        {
            var length = Math.Min(960, input.Length - offset);
            actual.AddRange(chunked.Process(input.AsSpan(offset, length)));
        }

        Assert.InRange(actual.Count, expected.Length - 2, expected.Length + 2);
        var compare = Math.Min(actual.Count, expected.Length);
        for (var i = 0; i < compare; i++)
        {
            Assert.InRange(actual[i] - expected[i], -0.02f, 0.02f);
        }
    }

    [Fact]
    public void PassesThroughWhenRatesMatch()
    {
        var resampler = new Resampler(48000, 48000);
        Assert.True(resampler.IsPassThrough);

        var input = SignalGenerator.Sine(440, 0.1, 48000);
        Assert.Equal(input, resampler.Process(input));
    }
}

public class StreamMixerTests
{
    [Fact]
    public void SumsBothLegsWithHeadroomAndNeverClips()
    {
        var settings = new AudioProcessingSettings();
        var mixer = new StreamMixer(settings, 48000);

        var mic = SignalGenerator.Sine(300, 1.0, 48000, 0.9);
        var system = SignalGenerator.Sine(900, 1.0, 48000, 0.9);
        var mix = new float[mic.Length];

        mixer.Mix(mic, system, mix);

        Assert.True(AudioMath.Peak(mix) <= AudioMath.DbToLinear(settings.LimiterCeilingDb) + 1e-6);
        // Both sources must still be audible in the result.
        Assert.True(AudioMath.Rms(mix) > 0.1);
    }

    [Fact]
    public void RejectsMismatchedLengths()
    {
        var mixer = new StreamMixer(new AudioProcessingSettings(), 48000);
        Assert.Throws<ArgumentException>(() => mixer.Mix(new float[10], new float[20], new float[10]));
    }
}

public class AudioRingBufferTests
{
    [Fact]
    public void WritesAndReadsInOrderAcrossTheWrapPoint()
    {
        var ring = new AudioRingBuffer(100);
        var written = 0;

        for (var round = 0; round < 5; round++)
        {
            var block = Enumerable.Range(written, 30).Select(i => (float)i).ToArray();
            ring.Write(block);
            written += 30;

            var read = new float[30];
            Assert.Equal(30, ring.Read(read));
            Assert.Equal(block, read);
        }
    }

    [Fact]
    public void DiscardsOldestAudioAndCountsTheOverrunWhenTheConsumerStalls()
    {
        var ring = new AudioRingBuffer(100);
        ring.Write(Enumerable.Range(0, 100).Select(i => (float)i).ToArray());
        ring.Write(Enumerable.Range(100, 20).Select(i => (float)i).ToArray());

        Assert.Equal(1, ring.OverrunCount);
        Assert.Equal(20, ring.OverrunSamples);

        var read = new float[100];
        Assert.Equal(100, ring.Read(read));
        // The newest audio survived; the oldest 20 samples were dropped.
        Assert.Equal(20f, read[0]);
        Assert.Equal(119f, read[99]);
    }

    [Fact]
    public void ReadReturnsWhatIsAvailableWithoutBlocking()
    {
        var ring = new AudioRingBuffer(100);
        ring.Write(new float[7]);

        var destination = new float[50];
        Assert.Equal(7, ring.Read(destination));
        Assert.Equal(0, ring.Read(destination));
    }

    [Fact]
    public void DiscardRemovesTheOldestSamples()
    {
        var ring = new AudioRingBuffer(100);
        ring.Write(Enumerable.Range(0, 50).Select(i => (float)i).ToArray());

        Assert.Equal(10, ring.Discard(10));
        var read = new float[40];
        ring.Read(read);
        Assert.Equal(10f, read[0]);
    }
}

public class DriftCompensatorTests
{
    [Fact]
    public void DoesNothingWhileTheBacklogIsNormal()
    {
        var compensator = new DriftCompensator(48000);
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(0, compensator.Evaluate((int)(48000 * 0.08), 0.02));
        }

        Assert.Equal(0, compensator.CorrectedSamples);
    }

    [Fact]
    public void CorrectsOnlyAfterTheBacklogHasBeenHighForAWhile()
    {
        var compensator = new DriftCompensator(48000, sustainedSeconds: 5.0);
        var backlog = (int)(48000 * 0.4);

        var correctedEarly = 0;
        for (var i = 0; i < 100; i++)
        {
            correctedEarly += compensator.Evaluate(backlog, 0.02);
        }

        Assert.Equal(0, correctedEarly);

        var correctedLate = 0;
        for (var i = 0; i < 400; i++)
        {
            correctedLate += compensator.Evaluate(backlog, 0.02);
        }

        Assert.True(correctedLate > 0, "a sustained backlog must eventually be corrected");
    }

    [Fact]
    public void ABurstAfterSilenceIsNotMistakenForDrift()
    {
        var compensator = new DriftCompensator(48000);

        // WASAPI loopback delivers nothing while the endpoint is silent.
        for (var i = 0; i < 500; i++)
        {
            compensator.ReportSilenceInserted(960);
            compensator.Evaluate(0, 0.02);
        }

        // Then a burst arrives.
        var corrected = 0;
        for (var i = 0; i < 50; i++)
        {
            corrected += compensator.Evaluate((int)(48000 * 0.5), 0.02);
        }

        Assert.Equal(0, corrected);
    }

    [Fact]
    public void CorrectionIsBoundedPerTick()
    {
        var compensator = new DriftCompensator(48000, sustainedSeconds: 0.0);
        var backlog = 48000 * 5;

        var corrected = compensator.Evaluate(backlog, 0.02);
        Assert.InRange(corrected, 1, 48);
    }
}

public class ProcessingChainTests
{
    [Fact]
    public void ChainKeepsOutputWithinTheCeiling()
    {
        var settings = new AudioProcessingSettings();
        var chain = new AudioProcessingChain(settings, 48000);
        var signal = SignalGenerator.Sine(300, 5.0, 48000, 0.95);

        for (var offset = 0; offset < signal.Length; offset += 960)
        {
            chain.Process(signal.AsSpan(offset, Math.Min(960, signal.Length - offset)));
        }

        Assert.True(AudioMath.Peak(signal) <= AudioMath.DbToLinear(settings.LimiterCeilingDb) + 1e-6);
    }

    [Fact]
    public void ChainRemovesDcOffset()
    {
        var settings = new AudioProcessingSettings();
        var chain = new AudioProcessingChain(settings, 48000);

        var signal = SignalGenerator.Sine(300, 3.0, 48000, 0.2);
        for (var i = 0; i < signal.Length; i++)
        {
            signal[i] += 0.3f;
        }

        for (var offset = 0; offset < signal.Length; offset += 960)
        {
            chain.Process(signal.AsSpan(offset, Math.Min(960, signal.Length - offset)));
        }

        var tail = signal.AsSpan(signal.Length - 48000);
        double mean = 0;
        foreach (var sample in tail)
        {
            mean += sample;
        }

        mean /= tail.Length;
        Assert.InRange(mean, -0.02, 0.02);
    }
}
