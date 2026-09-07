using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

public sealed class AudioFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mr-tests-" + Guid.NewGuid().ToString("N"));

    public AudioFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RoundTripsAudioThroughWav()
    {
        var path = Path.Combine(_directory, "roundtrip.wav");
        var signal = SignalGenerator.Sine(440, 0.5, 48000, 0.5);

        using (var writer = new WavFileWriter(path, 48000))
        {
            writer.Write(signal);
        }

        using var reader = new WavFileReader(path);
        Assert.Equal(48000, reader.SampleRate);
        Assert.Equal(1, reader.Channels);
        Assert.Equal(16, reader.BitsPerSample);

        var read = reader.ReadAllMono();
        Assert.Equal(signal.Length, read.Length);

        for (var i = 0; i < signal.Length; i++)
        {
            // 16-bit quantisation is the only permitted difference.
            Assert.InRange(read[i] - signal[i], -1.0f / 32767, 1.0f / 32767);
        }
    }

    [Fact]
    public void HeaderIsCorrectAfterEveryFlushSoAKilledProcessLeavesAPlayableFile()
    {
        var path = Path.Combine(_directory, "crash.wav");
        var block = SignalGenerator.Sine(440, 0.25, 48000, 0.4);

        // Deliberately never disposed: this simulates the process being killed.
        var writer = new WavFileWriter(path, 48000);
        writer.Write(block);
        writer.Flush();
        writer.Write(block);
        writer.Flush();

        using (var reader = new WavFileReader(path))
        {
            Assert.InRange(reader.DurationSeconds, 0.49, 0.51);
        }

        writer.Dispose();
    }

    [Fact]
    public void TruncatedDataChunkIsReadAsFarAsItGoes()
    {
        var path = Path.Combine(_directory, "torn.wav");
        using (var writer = new WavFileWriter(path, 48000))
        {
            writer.Write(SignalGenerator.Sine(440, 1.0, 48000, 0.4));
        }

        // Chop the file in half without touching the header, exactly like a power
        // loss mid-write would.
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        using var reader = new WavFileReader(path);
        Assert.InRange(reader.DurationSeconds, 0.4, 0.6);
        Assert.NotEmpty(reader.ReadAllMono());
    }

    [Fact]
    public void ClampsSamplesOutsideTheValidRangeRatherThanWrappingThem()
    {
        var path = Path.Combine(_directory, "clamp.wav");
        using (var writer = new WavFileWriter(path, 48000))
        {
            writer.Write(new[] { 2.0f, -2.0f, 0f });
        }

        using var reader = new WavFileReader(path);
        var read = reader.ReadAllMono();

        Assert.InRange(read[0], 0.99f, 1.0f);
        Assert.InRange(read[1], -1.0f, -0.99f);
        Assert.Equal(0f, read[2], 4);
    }

    [Fact]
    public void ReportsDurationAsItWrites()
    {
        var path = Path.Combine(_directory, "duration.wav");
        using var writer = new WavFileWriter(path, 48000);

        writer.Write(new float[48000]);
        Assert.Equal(1.0, writer.DurationSeconds, 3);

        writer.Write(new float[24000]);
        Assert.Equal(1.5, writer.DurationSeconds, 3);
    }

    [Fact]
    public void MixedSignalSurvivesTheWholeChainWithoutClipping()
    {
        var path = Path.Combine(_directory, "chain.wav");
        var settings = new Core.Models.AudioProcessingSettings();
        var micChain = new AudioProcessingChain(settings, 48000);
        var systemChain = new AudioProcessingChain(settings, 48000);
        var mixer = new StreamMixer(settings, 48000);

        var mic = SignalGenerator.Sine(220, 3.0, 48000, 0.05);
        var system = SignalGenerator.Sine(880, 3.0, 48000, 0.8);
        var mix = new float[mic.Length];

        using (var writer = new WavFileWriter(path, 48000))
        {
            for (var offset = 0; offset < mic.Length; offset += 960)
            {
                var length = Math.Min(960, mic.Length - offset);
                var micBlock = mic.AsSpan(offset, length);
                var systemBlock = system.AsSpan(offset, length);
                micChain.Process(micBlock);
                systemChain.Process(systemBlock);
                mixer.Mix(micBlock, systemBlock, mix.AsSpan(offset, length));
                writer.Write(mix.AsSpan(offset, length));
            }
        }

        using var reader = new WavFileReader(path);
        var written = reader.ReadAllMono();
        Assert.True(AudioMath.Peak(written) < 1.0);
        Assert.InRange(reader.DurationSeconds, 2.99, 3.01);
    }
}
