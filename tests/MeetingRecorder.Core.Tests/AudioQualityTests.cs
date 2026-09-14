using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// Measures the frequency response of the sample-rate converter with a swept
/// sine, so the claim "the interpolation was costing high frequencies" is a
/// number rather than an assertion.
/// </summary>
/// <remarks>
/// The thresholds here are the measured Catmull-Rom figures with head-room, not
/// aspirations. If somebody puts linear interpolation back, these fail: linear
/// loses 0.95 dB at 8 kHz converting 44.1 kHz and 5.25 dB at 7 kHz converting
/// 16 kHz, both far outside what is allowed below.
/// </remarks>
public class ResamplerFrequencyResponseTests
{
    /// <summary>
    /// Amplitude of <paramref name="frequencyHz"/> in the converted signal,
    /// relative to the amplitude that went in, in dB.
    /// </summary>
    private static double ResponseDb(int sourceRate, int targetRate, double frequencyHz)
    {
        const double Amplitude = 0.5;

        var input = SignalGenerator.Sine(frequencyHz, 1.0, sourceRate, Amplitude);
        var output = new Resampler(sourceRate, targetRate).Process(input);

        // Correlate against the reference sinusoid rather than reading a peak:
        // a peak is one sample and moves with the resampling phase, while the
        // correlation is over the whole signal and is not fooled by either.
        var skip = Math.Min(2000, output.Length / 10);
        double re = 0, im = 0;
        var n = 0;

        for (var i = skip; i < output.Length - skip; i++)
        {
            var phase = 2.0 * Math.PI * frequencyHz * i / targetRate;
            re += output[i] * Math.Cos(phase);
            im += output[i] * Math.Sin(phase);
            n++;
        }

        var measured = 2.0 * Math.Sqrt((re * re) + (im * im)) / Math.Max(1, n);
        return AudioMath.LinearToDb(measured / Amplitude);
    }

    [Theory]
    // A 44.1 kHz endpoint - the common case for a USB headset - converted to
    // the 48 kHz engine rate. Linear interpolation measured -0.24, -0.53 and
    // -0.95 dB at these three frequencies.
    [InlineData(44100, 4000, 0.10)]
    [InlineData(44100, 6000, 0.20)]
    [InlineData(44100, 8000, 0.40)]
    public void UpConversionKeepsTheSpeechBandFlat(int sourceRate, double frequencyHz, double maximumLossDb)
    {
        var response = ResponseDb(sourceRate, 48000, frequencyHz);

        Assert.True(
            response > -maximumLossDb,
            $"{sourceRate} Hz -> 48 kHz lost {-response:F2} dB at {frequencyHz:F0} Hz, more than the {maximumLossDb:F2} dB allowed");

        // And it must not invent energy either: an interpolator with gain above
        // unity in the pass band would be eating head-room the mixer's
        // no-clipping guarantee depends on.
        Assert.True(response < 0.2, $"response at {frequencyHz:F0} Hz was +{response:F2} dB, which is a gain, not an interpolation");
    }

    [Theory]
    // A 16 kHz endpoint. It cannot carry anything above 8 kHz whatever this
    // code does, but what it does carry must survive the conversion: linear
    // interpolation measured -1.63 dB at 4 kHz and -3.77 dB at 6 kHz.
    [InlineData(2000, 0.30)]
    [InlineData(4000, 1.00)]
    [InlineData(6000, 3.00)]
    public void UpConversionFromANarrowbandEndpointKeepsWhatItHas(double frequencyHz, double maximumLossDb)
    {
        var response = ResponseDb(16000, 48000, frequencyHz);

        Assert.True(
            response > -maximumLossDb,
            $"16 kHz -> 48 kHz lost {-response:F2} dB at {frequencyHz:F0} Hz, more than the {maximumLossDb:F2} dB allowed");
    }

    [Fact]
    public void MatchingRatesAreABitExactCopy()
    {
        var resampler = new Resampler(48000, 48000);
        var input = SignalGenerator.Sine(1000, 0.25, 48000, 0.37);

        Assert.True(resampler.IsPassThrough);
        Assert.Equal(input, resampler.Process(input));
    }

    [Fact]
    public void TheSampleCountDoesNotDependOnTheInterpolator()
    {
        // The pump counts samples to keep the recording on the wall clock, so an
        // interpolator that swallowed a few samples at the head of the stream -
        // which a cubic naively would, while it waits for history - would show
        // up as drift.
        var resampler = new Resampler(44100, 48000);
        var input = SignalGenerator.Sine(440, 1.0, 44100);
        var output = resampler.Process(input);

        Assert.InRange(output.Length, 48000 - 4, 48000 + 4);
    }

    [Fact]
    public void ConversionIsSeamlessWhenTheStreamArrivesInBlocks()
    {
        var whole = new Resampler(44100, 48000);
        var chunked = new Resampler(44100, 48000);

        var input = SignalGenerator.Sine(3000, 0.5, 44100, 0.5);
        var expected = whole.Process(input);

        var actual = new List<float>();
        for (var offset = 0; offset < input.Length; offset += 441)
        {
            var length = Math.Min(441, input.Length - offset);
            actual.AddRange(chunked.Process(input.AsSpan(offset, length)));
        }

        Assert.InRange(actual.Count, expected.Length - 2, expected.Length + 2);
        for (var i = 0; i < Math.Min(actual.Count, expected.Length); i++)
        {
            Assert.InRange(actual[i] - expected[i], -1e-5f, 1e-5f);
        }
    }
}

public class MonoDownmixerTests
{
    private static float[] Interleave(params float[][] channels)
    {
        var frames = channels[0].Length;
        var result = new float[frames * channels.Length];
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < channels.Length; c++)
            {
                result[(f * channels.Length) + c] = channels[c][f];
            }
        }

        return result;
    }

    [Fact]
    public void AMonoStreamIsCopiedWithoutBeingTouched()
    {
        var input = SignalGenerator.Voice(120, 700, 1300, 1.0, 48000);
        var downmixer = new MonoDownmixer(1, 48000);
        var output = new float[input.Length];

        var frames = downmixer.Process(input, output);

        Assert.Equal(input.Length, frames);
        Assert.Equal(input, output);
        Assert.Equal(MonoDownmixMode.Average, downmixer.Mode);
    }

    [Fact]
    public void TwoChannelsCarryingTheSameVoiceKeepTheirFullAmplitude()
    {
        var voice = SignalGenerator.Voice(120, 700, 1300, 2.0, 48000);
        var downmixer = new MonoDownmixer(2, 48000);
        var output = new float[voice.Length];

        var frames = downmixer.Process(Interleave(voice, voice), output);

        Assert.Equal(voice.Length, frames);
        Assert.Equal(MonoDownmixMode.Average, downmixer.Mode);
        Assert.InRange(AudioMath.Rms(output), AudioMath.Rms(voice) * 0.999, AudioMath.Rms(voice) * 1.001);
    }

    [Fact]
    public void IndependentChannelsAreNotMistakenForCancellation()
    {
        // Two uncorrelated channels average to -3 dB. That is arithmetic, not a
        // fault, and switching to channel 0 because of it would throw away the
        // noise averaging a stereo microphone gives for free.
        var left = SignalGenerator.Noise(2.0, 48000, 0.2, seed: 1);
        var right = SignalGenerator.Noise(2.0, 48000, 0.2, seed: 2);

        var downmixer = new MonoDownmixer(2, 48000);
        var output = new float[left.Length];
        downmixer.Process(Interleave(left, right), output);

        Assert.Equal(MonoDownmixMode.Average, downmixer.Mode);
        Assert.InRange(downmixer.CorrelationRatioDb, -4.0, -2.0);
    }

    [Fact]
    public void AntiPhaseChannelsAreDetectedAndTheStreamSwitchesToChannelZero()
    {
        var voice = SignalGenerator.Voice(120, 700, 1300, 2.0, 48000);
        var inverted = voice.Select(s => -s).ToArray();

        string? message = null;
        var downmixer = new MonoDownmixer(2, 48000);
        downmixer.ModeChanged += m => message = m;

        var interleaved = Interleave(voice, inverted);
        var output = new float[voice.Length];

        // In blocks, as a capture callback delivers them: the verdict has to be
        // reached from the first second, not from the whole file.
        const int Block = 4800;
        for (var frame = 0; frame < voice.Length; frame += Block)
        {
            var count = Math.Min(Block, voice.Length - frame);
            downmixer.Process(interleaved.AsSpan(frame * 2, count * 2), output.AsSpan(frame, count));
        }

        Assert.Equal(MonoDownmixMode.FirstChannel, downmixer.Mode);
        Assert.NotNull(message);
        Assert.Contains("channel 0", message, StringComparison.Ordinal);

        // The tail of the stream - decided, and taking channel 0 - carries the
        // voice at full amplitude instead of the near-silence averaging gives.
        var tail = output.AsSpan(voice.Length / 2);
        Assert.InRange(AudioMath.Rms(tail), AudioMath.Rms(voice) * 0.9, AudioMath.Rms(voice) * 1.1);
    }

    [Fact]
    public void SilenceNeverDecidesAnything()
    {
        var downmixer = new MonoDownmixer(2, 48000);
        var silence = new float[48000 * 4];
        var output = new float[silence.Length / 2];

        downmixer.Process(silence, output);

        Assert.Equal(MonoDownmixMode.Average, downmixer.Mode);
        Assert.False(downmixer.AnalysisComplete);
    }
}

public class CaptureFormatReportTests
{
    private static CaptureFormatReport Report(int sampleRate, int channels = 1) => new(
        AudioSourceKind.Microphone,
        "Headset Microphone",
        sampleRate,
        channels,
        16,
        "Pcm",
        $"{sampleRate} Hz / {channels} ch / 16-bit Pcm",
        48000);

    [Theory]
    [InlineData(8000)]
    [InlineData(16000)]
    public void ANarrowbandEndpointIsCalledOutRatherThanQuietlyUpSampled(int sampleRate)
    {
        var report = Report(sampleRate);

        Assert.True(report.LimitsTheSpeechBand);
        Assert.NotNull(report.UserWarning);
        Assert.Contains("入力帯域が限定されています", report.UserWarning, StringComparison.Ordinal);
        Assert.Contains("復元できません", report.UserWarning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void AFullBandEndpointProducesNoWarning(int sampleRate)
    {
        var report = Report(sampleRate);

        Assert.False(report.LimitsTheSpeechBand);
        Assert.Null(report.UserWarning);
    }

    [Fact]
    public void TheLogBlockNamesEveryThingSomebodyWouldAskFor()
    {
        var block = Report(16000, channels: 2).ToLogBlock();

        Assert.Contains("Device           = Headset Microphone", block, StringComparison.Ordinal);
        Assert.Contains("16000 Hz / 2 ch / 16-bit Pcm", block, StringComparison.Ordinal);
        Assert.Contains("WASAPI MixFormat", block, StringComparison.Ordinal);
        Assert.Contains("InternalFormat   = 48000 Hz / mono", block, StringComparison.Ordinal);
        Assert.Contains("Source Nyquist   = 8000 Hz", block, StringComparison.Ordinal);
    }

    [Fact]
    public void AMatchingEndpointIsReportedAsPassThrough()
    {
        Assert.Contains("pass-through", Report(48000).ToLogBlock(), StringComparison.Ordinal);
        Assert.Contains("resampled 16000 -> 48000 Hz", Report(16000).ToLogBlock(), StringComparison.Ordinal);
    }
}

public class ListeningEqTests
{
    private static double ResponseDb(ListeningEq eq, double frequencyHz, int sampleRate)
    {
        const double Amplitude = 0.5;
        var buffer = SignalGenerator.Sine(frequencyHz, 1.0, sampleRate, Amplitude);
        eq.Process(buffer);

        var skip = sampleRate / 4;
        double re = 0, im = 0;
        var n = 0;
        for (var i = skip; i < buffer.Length; i++)
        {
            var phase = 2.0 * Math.PI * frequencyHz * i / sampleRate;
            re += buffer[i] * Math.Cos(phase);
            im += buffer[i] * Math.Sin(phase);
            n++;
        }

        return AudioMath.LinearToDb(2.0 * Math.Sqrt((re * re) + (im * im)) / n / Amplitude);
    }

    [Fact]
    public void TheShelfTiltsTheSpectrumByTheAmountItSays()
    {
        var settings = new AudioProcessingSettings();
        var lowDb = ResponseDb(new ListeningEq(48000, settings.MicrophoneListeningEqCornerHz, settings.MicrophoneListeningEqGainDb), 250, 48000);
        var highDb = ResponseDb(new ListeningEq(48000, settings.MicrophoneListeningEqCornerHz, settings.MicrophoneListeningEqGainDb), 8000, 48000);

        var tilt = highDb - lowDb;
        Assert.InRange(tilt, settings.MicrophoneListeningEqGainDb - 0.5, settings.MicrophoneListeningEqGainDb + 0.5);
    }

    [Fact]
    public void NoFrequencyIsAmplified()
    {
        // The whole filter is normalized by the L1 norm of its impulse response,
        // so the mixer's "two full scale legs sum to exactly full scale"
        // guarantee - and therefore the promise that nothing ever clips -
        // survives having an equalizer in front of one of them.
        var eq = new ListeningEq(48000, 2500, 4.0);
        Assert.True(eq.Scale <= 1.0);

        foreach (var frequency in new double[] { 100, 500, 1000, 2500, 5000, 10000, 18000 })
        {
            var response = ResponseDb(new ListeningEq(48000, 2500, 4.0), frequency, 48000);
            Assert.True(response <= 0.01, $"{frequency:F0} Hz was amplified by {response:F2} dB");
        }
    }

    [Fact]
    public void AFullScaleInputCannotBeMadeToExceedFullScale()
    {
        // Not a sine: a step and an impulse train are what actually find an
        // overshoot, because they excite the filter's whole impulse response at
        // once.
        var eq = new ListeningEq(48000, 2500, 6.0);
        var hostile = new float[4800];
        for (var i = 0; i < hostile.Length; i++)
        {
            hostile[i] = (i / 8 % 2 == 0) ? 1f : -1f;
        }

        eq.Process(hostile);

        Assert.True(AudioMath.Peak(hostile) <= 1.0, $"peak reached {AudioMath.Peak(hostile):F4}");
    }

    [Fact]
    public void ItIsLinearAndTimeInvariant()
    {
        // The property every banned stage breaks: doubling the input doubles the
        // output, everywhere, with no dependence on how loud anything was.
        var quiet = SignalGenerator.Voice(120, 700, 1300, 0.5, 48000, amplitude: 0.02);
        var loud = quiet.Select(s => s * 20f).ToArray();

        new ListeningEq(48000, 2500, 4.0).Process(quiet);
        new ListeningEq(48000, 2500, 4.0).Process(loud);

        for (var i = 0; i < quiet.Length; i++)
        {
            Assert.InRange(loud[i] - (quiet[i] * 20f), -1e-3f, 1e-3f);
        }
    }
}

public class SpectralBalanceTests
{
    private static string WriteWav(string directory, string name, float[] samples, int rate = 16000)
    {
        var path = Path.Combine(directory, name);
        using var writer = new WavFileWriter(path, rate);
        writer.Write(samples);
        writer.Flush();
        return path;
    }

    [Fact]
    public void ABrightStreamAndADullStreamAreToldApart()
    {
        var directory = Directory.CreateTempSubdirectory("spectral").FullName;
        try
        {
            // "Bright": energy in the low-mid band and in the sibilance band.
            // "Dull": the same low-mid energy with the top band low-passed away,
            // which is exactly what a narrowband microphone produces.
            var lowMid = SignalGenerator.Sine(600, 3.0, 16000, 0.3);
            var sibilance = SignalGenerator.Sine(5500, 3.0, 16000, 0.3);

            var bright = new float[lowMid.Length];
            var dull = new float[lowMid.Length];
            for (var i = 0; i < bright.Length; i++)
            {
                bright[i] = lowMid[i] + sibilance[i];
                dull[i] = lowMid[i] + (sibilance[i] * 0.02f);
            }

            var brightReport = SpectralBalance.Measure(WriteWav(directory, "bright.wav", bright));
            var dullReport = SpectralBalance.Measure(WriteWav(directory, "dull.wav", dull));

            Assert.True(brightReport.HasSignal);
            Assert.True(dullReport.HasSignal);
            Assert.True(
                brightReport.HighBandTiltDb - dullReport.HighBandTiltDb > 20,
                $"expected a large tilt difference, saw {brightReport.HighBandTiltDb - dullReport.HighBandTiltDb:F1} dB");

            var message = SpectralBalance.DescribeDifference(dullReport, brightReport, out var differenceDb);
            Assert.NotNull(message);
            Assert.True(differenceDb > 6);
            Assert.Contains("マイク音声の高域", message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TwoComparableStreamsProduceNoClaim()
    {
        var directory = Directory.CreateTempSubdirectory("spectral").FullName;
        try
        {
            var signal = SignalGenerator.Noise(3.0, 16000, 0.2);
            var a = SpectralBalance.Measure(WriteWav(directory, "a.wav", signal));
            var b = SpectralBalance.Measure(WriteWav(directory, "b.wav", signal));

            Assert.Null(SpectralBalance.DescribeDifference(a, b, out var differenceDb));
            Assert.InRange(differenceDb, -0.01, 0.01);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SilenceIsReportedAsUnmeasurableRatherThanAsDull()
    {
        var directory = Directory.CreateTempSubdirectory("spectral").FullName;
        try
        {
            var report = SpectralBalance.Measure(WriteWav(directory, "quiet.wav", SignalGenerator.Silence(3.0, 16000)));

            Assert.False(report.HasSignal);
            Assert.Contains("not enough signal", report.ToLogBlock("Microphone"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
