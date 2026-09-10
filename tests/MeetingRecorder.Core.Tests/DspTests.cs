using MeetingRecorder.Core.Audio;
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

/// <summary>
/// The stages that are gone, asserted by name.
/// </summary>
/// <remarks>
/// Every one of these changed gain as a function of time, and a speech
/// recognizer reads exactly that. Deleting them is the point of this change, so
/// the check is not "are they disabled" - a disabled stage can be re-enabled by
/// a stray setting - but "do they exist at all". Reflection over the shipped
/// assembly is the only form of this assertion that a future edit cannot quietly
/// satisfy.
/// </remarks>
public class RemovedProcessingStagesTests
{
    [Theory]
    [InlineData("NoiseGate")]
    [InlineData("Compressor")]
    [InlineData("Limiter")]
    [InlineData("LoudnessNormalizer")]
    [InlineData("Agc")]
    [InlineData("SoftGate")]
    public void TheTimeVaryingStagesAreNotInTheProduct(string typeName)
    {
        var assembly = typeof(AudioProcessingChain).Assembly;
        var found = assembly.GetTypes().Where(t => t.Name.Contains(typeName, StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(
            found.Count == 0,
            $"'{typeName}' still exists in {assembly.GetName().Name}: {string.Join(", ", found.Select(t => t.FullName))}");
    }

    [Fact]
    public void NoAudioSettingConfiguresAGateACompressorOrALimiter()
    {
        // A leftover property is how a deleted stage comes back: someone reads
        // it, wires it up, and the recordings quietly change.
        var forbidden = new[] { "Gate", "Compressor", "Limiter", "Agc", "TargetRms", "Adaptation" };

        var offending = typeof(AudioProcessingSettings)
            .GetProperties()
            .Where(p => forbidden.Any(f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(offending.Count == 0, $"AudioProcessingSettings still exposes: {string.Join(", ", offending)}");
    }
}

public class PeakNormalizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-gain-" + Guid.NewGuid().ToString("N"));

    public PeakNormalizerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteWav(string name, float[] samples, int sampleRate = 48000)
    {
        var path = Path.Combine(_root, name);
        using (var writer = new WavFileWriter(path, sampleRate))
        {
            writer.Write(samples);
            writer.Flush();
        }

        return path;
    }

    private static float[] ReadWav(string path)
    {
        using var reader = new WavFileReader(path);
        return reader.ReadAllMono();
    }

    /// <summary>
    /// Speech-shaped material: a quiet onset, a loud passage, digital silence
    /// and a very small transient. Between them they cover everything a gate, an
    /// AGC or a compressor would visibly damage.
    /// </summary>
    private static float[] DynamicSpeechLikeSignal() => SignalGenerator.Concat(
        SignalGenerator.Sine(180, 0.30, 48000, 0.02),   // a quiet start
        SignalGenerator.Sine(240, 0.50, 48000, 0.45),   // a loud passage
        SignalGenerator.Silence(0.40, 48000),           // a pause
        SignalGenerator.Sine(3000, 0.02, 48000, 0.004), // a faint consonant burst
        SignalGenerator.Silence(0.20, 48000),
        SignalGenerator.Sine(200, 0.30, 48000, 0.12));

    [Fact]
    public void EveryNonZeroSampleIsMultipliedByTheSameConstant()
    {
        // This is the whole requirement in one assertion: output[n] / input[n]
        // must be one number, for every n. A gate, an AGC, a compressor or a
        // limiter all fail it by construction.
        var input = DynamicSpeechLikeSignal();
        var path = WriteWav("constant-gain.wav", input);
        var settings = new AudioProcessingSettings();

        var result = PeakNormalizer.Normalize(path, settings);
        Assert.True(result.Applied, "a signal peaking at -7 dBFS should be raised");

        var output = ReadWav(path);
        Assert.Equal(input.Length, output.Length);

        double? reference = null;
        var compared = 0;

        for (var i = 0; i < input.Length; i++)
        {
            // 16-bit quantisation is +/- 1/65536 of full scale, so only samples
            // comfortably above it can carry a meaningful ratio.
            if (Math.Abs(input[i]) < 0.01)
            {
                continue;
            }

            var ratio = output[i] / (double)input[i];
            reference ??= ratio;
            compared++;

            // 0.5% covers re-quantisation at this amplitude and nothing else.
            Assert.InRange(ratio, reference.Value * 0.995, reference.Value * 1.005);
        }

        Assert.True(compared > 20000, $"only {compared} samples were large enough to compare");
        Assert.InRange(reference!.Value, result.Gain * 0.99, result.Gain * 1.01);
    }

    [Fact]
    public void TheRelativeAmplitudeOfLoudAndQuietPassagesIsUnchanged()
    {
        var input = DynamicSpeechLikeSignal();
        var path = WriteWav("relative.wav", input);

        var quietBefore = AudioMath.Rms(input.AsSpan(0, 14000));
        var loudBefore = AudioMath.Rms(input.AsSpan(20000, 20000));

        PeakNormalizer.Normalize(path, new AudioProcessingSettings());
        var output = ReadWav(path);

        var quietAfter = AudioMath.Rms(output.AsSpan(0, 14000));
        var loudAfter = AudioMath.Rms(output.AsSpan(20000, 20000));

        // The ratio between a quiet passage and a loud one is the thing a
        // compressor exists to change. It must not have changed.
        var before = loudBefore / quietBefore;
        var after = loudAfter / quietAfter;
        Assert.InRange(after, before * 0.99, before * 1.01);
    }

    [Fact]
    public void SilenceStaysSilent()
    {
        var input = DynamicSpeechLikeSignal();
        var path = WriteWav("silence.wav", input);

        PeakNormalizer.Normalize(path, new AudioProcessingSettings());
        var output = ReadWav(path);

        // Samples 38400..57600 are the digital silence in the middle. Multiplying
        // zero by anything is still zero - unlike a noise floor lifted by an AGC.
        for (var i = 38_400; i < 57_600; i++)
        {
            Assert.Equal(0f, output[i]);
        }
    }

    [Fact]
    public void AFaintTransientSurvivesInProportion()
    {
        // The 4 ms, -48 dBFS burst is exactly what a gate removes: too short to
        // open it, too quiet to matter to it. It has to come through scaled by
        // the same factor as the rest.
        var input = DynamicSpeechLikeSignal();
        var path = WriteWav("transient.wav", input);

        var burstStart = (int)(48000 * 1.20);
        var burstLength = (int)(48000 * 0.02);
        var before = AudioMath.Peak(input.AsSpan(burstStart, burstLength));
        Assert.True(before > 0, "the fixture must contain the burst");

        var result = PeakNormalizer.Normalize(path, new AudioProcessingSettings());
        var output = ReadWav(path);
        var after = AudioMath.Peak(output.AsSpan(burstStart, burstLength));

        Assert.InRange(after, before * result.Gain * 0.9, before * result.Gain * 1.1);
    }

    [Fact]
    public void NormalizationNeverProducesASampleAtFullScale()
    {
        var settings = new AudioProcessingSettings();
        var ceiling = AudioMath.DbToLinear(settings.NormalizationTargetPeakDbFs);

        foreach (var amplitude in new[] { 0.001, 0.05, 0.5, 0.95, 0.999 })
        {
            var path = WriteWav($"ceiling-{amplitude}.wav", SignalGenerator.Sine(300, 0.5, 48000, amplitude));
            PeakNormalizer.Normalize(path, settings);

            var peak = AudioMath.Peak(ReadWav(path));
            Assert.True(peak < 1.0, $"amplitude {amplitude} produced a full-scale sample ({peak})");
            Assert.True(
                peak <= ceiling + 0.001,
                $"amplitude {amplitude} exceeded the {settings.NormalizationTargetPeakDbFs} dBFS target ({AudioMath.LinearToDb(peak):F2} dBFS)");
        }
    }

    [Fact]
    public void AHotFileIsScaledDownRatherThanHavingItsPeaksFlattened()
    {
        // The forbidden alternative, written out: `if (s > t) s = t;`. It would
        // pass a peak check and fail this one, because every sample has to be
        // the input times one number - including the ones that were too loud.
        var input = SignalGenerator.Sine(300, 1.0, 48000, 0.99);
        var path = WriteWav("hot.wav", input);

        var result = PeakNormalizer.Normalize(path, new AudioProcessingSettings());
        Assert.True(result.Applied);
        Assert.True(result.Gain < 1.0, "a file peaking at -0.1 dBFS has to come down, not up");

        var output = ReadWav(path);

        // The fixture is quantised to 16 bits when it is written and again when
        // it is rewritten, and the writer scales by 32767 while the reader
        // divides by 32768. Three 16-bit steps covers all of that and nothing
        // that would count as a change of shape.
        const double tolerance = 3.0 / 32768.0;
        for (var i = 0; i < input.Length; i++)
        {
            var expected = input[i] * result.Gain;
            Assert.InRange(output[i] - expected, -tolerance, tolerance);
        }

        // The crest factor of a sine is fixed. A clipper raises it; a gain does not.
        var crestBefore = AudioMath.Peak(input) / AudioMath.Rms(input);
        var crestAfter = AudioMath.Peak(output) / AudioMath.Rms(output);
        Assert.InRange(crestAfter, crestBefore * 0.99, crestBefore * 1.01);
    }

    [Fact]
    public void BoostIsCappedSoANearlySilentRecordingIsNotAmplifiedToFullScale()
    {
        var settings = new AudioProcessingSettings();
        var maximum = AudioMath.DbToLinear(settings.MaxNormalizationGainDb);

        // -80 dBFS: essentially the noise floor of the capture, not speech.
        Assert.Equal(maximum, PeakNormalizer.ComputeGain(AudioMath.DbToLinear(-80), settings), 6);
    }

    [Fact]
    public void SilenceIsLeftExactlyAsItIs()
    {
        var path = WriteWav("all-silence.wav", SignalGenerator.Silence(0.5, 48000));
        var result = PeakNormalizer.Normalize(path, new AudioProcessingSettings());

        Assert.False(result.Applied);
        Assert.Equal(1.0, result.Gain);
        Assert.Equal(0.0, AudioMath.Peak(ReadWav(path)));
    }

    [Fact]
    public void AudioThatArrivedClippedIsReportedAndNotRepaired()
    {
        // A square wave is what an over-driven input produces: a long run of
        // samples pinned to full scale.
        var input = new float[48000];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (i / 100) % 2 == 0 ? 1f : -1f;
        }

        var path = WriteWav("clipped.wav", input);
        var settings = new AudioProcessingSettings();
        var result = PeakNormalizer.Normalize(path, settings);

        Assert.True(result.Scan.IsClipped, "a fully pinned waveform must be reported as clipped");
        Assert.True(result.Scan.ClippedFraction > 0.9);

        // The flattened peaks are still flat afterwards: nothing here claims to
        // have restored them.
        var output = ReadWav(path);
        var peak = AudioMath.Peak(output);
        Assert.True(output.Count(s => Math.Abs(s) >= peak - 1e-4) > 40000);
    }

    [Fact]
    public void CleanAudioIsNotReportedAsClipped()
    {
        var path = WriteWav("clean.wav", SignalGenerator.Sine(300, 1.0, 48000, 0.9));
        var scan = PeakNormalizer.Scan(path, new AudioProcessingSettings());

        Assert.False(scan.IsClipped);
    }

    [Fact]
    public void AFailedRewriteLeavesTheOriginalRecordingIntact()
    {
        var input = SignalGenerator.Sine(300, 0.5, 48000, 0.1);
        var path = WriteWav("protected.wav", input);
        var before = File.ReadAllBytes(path);

        var result = PeakNormalizer.Normalize(
            path,
            new AudioProcessingSettings(),
            writerFactory: (_, _) => throw new IOException("disk full (test)"));

        Assert.False(result.Applied);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains("録音した音量のまま", result.Reason);
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
    public void TwoFullScaleStreamsSumToExactlyFullScaleAndNeverBeyond()
    {
        // The mixer's only job beyond the sum: make clipping arithmetically
        // impossible without a limiter and without a clamp. At -6.02 dB per leg,
        // 1.0 + 1.0 lands on 1.0 - the worst case there is.
        var settings = new AudioProcessingSettings();
        var mixer = new StreamMixer(settings, 48000);

        var mic = SignalGenerator.Sine(300, 1.0, 48000, 1.0);
        var system = SignalGenerator.Sine(300, 1.0, 48000, 1.0);
        var mix = new float[mic.Length];

        mixer.Mix(mic, system, mix);

        Assert.True(AudioMath.Peak(mix) <= 1.0 + 1e-6, $"peak {AudioMath.Peak(mix)} exceeded full scale");
        Assert.InRange(AudioMath.Peak(mix), 0.99, 1.0 + 1e-6);
    }

    [Fact]
    public void TheMixIsALinearSumWithOneConstantGain()
    {
        var mixer = new StreamMixer(new AudioProcessingSettings(), 48000);

        var mic = SignalGenerator.Sine(300, 0.2, 48000, 0.4);
        var system = SignalGenerator.Sine(900, 0.2, 48000, 0.1);
        var mix = new float[mic.Length];

        mixer.Mix(mic, system, mix);

        for (var i = 0; i < mix.Length; i++)
        {
            Assert.Equal((mic[i] + system[i]) * mixer.Gain, mix[i], 5);
        }
    }

    [Fact]
    public void BothSourcesStayAudibleInTheResult()
    {
        var mixer = new StreamMixer(new AudioProcessingSettings(), 48000);

        var mic = SignalGenerator.Sine(300, 1.0, 48000, 0.5);
        var system = SignalGenerator.Sine(900, 1.0, 48000, 0.5);
        var mix = new float[mic.Length];

        mixer.Mix(mic, system, mix);
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
    public void TheChainAppliesTheSameGainToALoudPassageAndAQuietOne()
    {
        // What the recording chain does, in full. A gate, an AGC or a compressor
        // would each show up here as a different gain on the -34 dBFS passages
        // than on the -4 dBFS one.
        var settings = new AudioProcessingSettings();
        var chain = new AudioProcessingChain(settings, 48000);

        var signal = SignalGenerator.Concat(
            SignalGenerator.Sine(200, 0.5, 48000, 0.02),
            SignalGenerator.Sine(200, 0.5, 48000, 0.6),
            SignalGenerator.Silence(0.3, 48000),
            SignalGenerator.Sine(200, 0.5, 48000, 0.02));

        var original = (float[])signal.Clone();

        for (var offset = 0; offset < signal.Length; offset += 960)
        {
            chain.Process(signal.AsSpan(offset, Math.Min(960, signal.Length - offset)));
        }

        // Sample-by-sample comparison would measure the high-pass's phase shift
        // rather than its gain, so each passage is compared by level. Windows
        // start a little inside each passage to skip the filter's transient.
        var passages = new[] { (4800, 14400), (28800, 14400), (67200, 14400) };
        var gains = passages
            .Select(p => AudioMath.Rms(signal.AsSpan(p.Item1, p.Item2)) / AudioMath.Rms(original.AsSpan(p.Item1, p.Item2)))
            .ToArray();

        foreach (var gain in gains)
        {
            // A 20 Hz first-order high-pass costs a 200 Hz tone 0.5%.
            Assert.InRange(gain, 0.98, 1.02);
        }

        Assert.InRange(gains[1] / gains[0], 0.99, 1.01);
        Assert.InRange(gains[2] / gains[0], 0.99, 1.01);
    }

    [Fact]
    public void TheChainDoesNotLiftAQuietPassageTowardsALoudOne()
    {
        var chain = new AudioProcessingChain(new AudioProcessingSettings(), 48000);

        // 30 seconds is far longer than any AGC time constant: if one were still
        // in the chain, the quiet tail would have been pulled up by now.
        var signal = SignalGenerator.Concat(
            SignalGenerator.Sine(200, 15.0, 48000, 0.5),
            SignalGenerator.Sine(200, 15.0, 48000, 0.01));

        for (var offset = 0; offset < signal.Length; offset += 4800)
        {
            chain.Process(signal.AsSpan(offset, Math.Min(4800, signal.Length - offset)));
        }

        var loud = AudioMath.Rms(signal.AsSpan(48000, 48000));
        var quiet = AudioMath.Rms(signal.AsSpan(signal.Length - 48000));

        Assert.InRange(loud / quiet, 45.0, 55.0);
    }

    [Fact]
    public void TheChainRemovesDcOffset()
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

    [Fact]
    public void TheChainIntroducesNoLatency()
    {
        // Anything that delays the signal would have to be a look-ahead stage,
        // and the only stage that ever needed one was the limiter.
        Assert.Equal(0, new AudioProcessingChain(new AudioProcessingSettings(), 48000).LatencySamples);
    }
}
