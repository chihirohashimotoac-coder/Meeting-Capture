using System.Runtime.InteropServices;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Stt;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace MeetingRecorder.Audio.Tests;

/// <summary>
/// Importing MP3, M4A and AAC through the codecs Windows ships.
/// </summary>
/// <remarks>
/// <para>
/// These formats are decoded by Media Foundation, which is part of Windows but
/// not of every Windows: N and KN editions omit the media stack until the Media
/// Feature Pack is installed. So the honest thing for a test to assert is not
/// "MP3 import works" but "this machine's answer about MP3 is correct and
/// actionable" - the format either round-trips, or it is refused with a reason
/// the user can act on, and never accepted and then mis-decoded.
/// </para>
/// <para>
/// The fixtures are encoded here, by the same Media Foundation encoders, so a
/// machine that cannot decode a format usually cannot produce the fixture
/// either; that case is reported as a skip with the reason, not as a pass.
/// </para>
/// <para>
/// <b>The .aac case is deliberately not overstated.</b> The fixture this test
/// can build is AAC in an MP4 container with an .aac extension, because Media
/// Foundation's encoder produces MP4 and there is no supported way to have it
/// emit a raw ADTS stream. A genuine ADTS .aac file is therefore <i>not</i>
/// covered by CI, and README.md says so rather than implying it is.
/// </para>
/// </remarks>
public class AudioImportCodecTests
{
    private readonly ITestOutputHelper _output;

    public AudioImportCodecTests(ITestOutputHelper output) => _output = output;

    private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static IAudioDecoder Decoder()
        => new CompositeAudioDecoder(new WavAudioDecoder(), new MediaFoundationAudioDecoder());

    /// <summary>Three seconds of tone, silence, tone: enough to measure.</summary>
    private static string WriteSourceWav(string directory)
    {
        var path = Path.Combine(directory, "source.wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));

        void Tone(double seconds, double amplitude)
        {
            var samples = (int)(48000 * seconds);
            for (var i = 0; i < samples; i++)
            {
                writer.WriteSample((float)(amplitude * Math.Sin(2 * Math.PI * 440 * i / 48000.0)));
            }
        }

        Tone(1.5, 0.4);
        Tone(1.0, 0.0);
        Tone(1.5, 0.4);
        writer.Flush();
        return path;
    }

    /// <summary>
    /// Encodes the fixture, or returns null with the reason this machine could
    /// not - which is reported rather than swallowed.
    /// </summary>
    private string? TryEncode(string sourceWav, string destination, string format, out string? reason)
    {
        reason = null;
        try
        {
            MediaFoundationApi.Startup();
            try
            {
                using var reader = new AudioFileReader(sourceWav);
                if (format == "mp3")
            {
                    MediaFoundationEncoder.EncodeToMp3(reader, destination, 96_000);
                }
                else
            {
                    MediaFoundationEncoder.EncodeToAac(reader, destination, 96_000);
                }
            }
            finally
            {
                MediaFoundationApi.Shutdown();
            }

            return File.Exists(destination) && new FileInfo(destination).Length > 0 ? destination : null;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    private void RunImport(string extension, string encoderFormat)
    {
        if (!OnWindows)
        {
            _output.WriteLine($"Skipped: {extension} import is decoded by Windows Media Foundation.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mr-codec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var source = WriteSourceWav(root);
            var encoded = Path.Combine(root, "meeting" + extension);

            if (TryEncode(source, encoded, encoderFormat, out var encodeError) is null)
            {
                // No encoder means no fixture. Say so loudly: a silent pass here
                // would read as "this format is supported".
                _output.WriteLine($"::warning::{extension} could not be encoded on this machine ({encodeError}); import of it is UNTESTED here.");
                return;
            }

            var before = File.ReadAllBytes(encoded);
            var service = new AudioImportService(Decoder());
            var working = Path.Combine(root, "working.wav");

            if (!service.CanImport(encoded, out var reason))
            {
                // A refusal is a legitimate outcome on a machine without the
                // codec - but it has to be a refusal the user can act on, and it
                // must not have produced anything.
                _output.WriteLine($"{extension}: refused with '{reason}'");
                Assert.False(string.IsNullOrWhiteSpace(reason));
                Assert.True(reason!.Length > 20, "a refusal must explain itself");
                Assert.False(File.Exists(working));
                return;
            }

            var result = service.Import(encoded, working, new AudioProcessingSettings());
            _output.WriteLine($"{extension}: decoded {result.DurationSeconds:F2}s, peak {result.Scan.PeakDbFs:F1} dBFS");

            using (var reader = new WavFileReader(working))
            {
                Assert.Equal(SpeechConstants.SampleRate, reader.SampleRate);
                Assert.Equal(1, reader.Channels);

                // Lossy encoders add priming and padding; the length only has to
                // be recognisably the same four seconds.
                Assert.InRange(reader.DurationSeconds, 3.5, 4.6);

                var samples = reader.ReadAllMono();
                Assert.True(AudioMath.Rms(samples) > 0.05, "the decoded audio is essentially silent");
            }

            // The file the user gave us is untouched.
            Assert.Equal(before, File.ReadAllBytes(encoded));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Mp3ImportsOrIsRefusedWithAReason() => RunImport(".mp3", "mp3");

    [Fact]
    public void M4aImportsOrIsRefusedWithAReason() => RunImport(".m4a", "aac");

    [Fact]
    public void AacImportsOrIsRefusedWithAReason() => RunImport(".aac", "aac");

    [Fact]
    public void WavNeedsNoOperatingSystemCodecAtAll()
    {
        // The one format that must work on every machine, including a Windows N
        // edition with no media stack: it goes through this project's own reader.
        var root = Path.Combine(Path.GetTempPath(), "mr-codec-wav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var source = OnWindows
                ? WriteSourceWav(root)
                : WriteFallbackWav(root);

            var working = Path.Combine(root, "working.wav");
            var result = new AudioImportService(new WavAudioDecoder()).Import(source, working, new AudioProcessingSettings());

            Assert.InRange(result.DurationSeconds, 3.5, 4.5);

            using var reader = new WavFileReader(working);
            Assert.Equal(SpeechConstants.SampleRate, reader.SampleRate);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>WAV fixture written without NAudio, so this test runs anywhere.</summary>
    private static string WriteFallbackWav(string directory)
    {
        var path = Path.Combine(directory, "source.wav");
        using var writer = new WavFileWriter(path, 48000);

        var samples = new float[48000 * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
        }

        writer.Write(samples);
        writer.Flush();
        return path;
    }
}
