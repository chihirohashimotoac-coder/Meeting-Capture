using System.Diagnostics;
using System.Text;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Stt;
using Xunit;
using Xunit.Abstractions;

namespace MeetingRecorder.Stt.Tests;

/// <summary>
/// Runs the real whisper.cpp engine over real speech.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this repository uses a test double for recognition. This
/// one does not: it loads an actual ggml model, feeds it audio of a human voice,
/// and asserts that recognisable words come back. That is what establishes that
/// the native binary loads, that the chunker feeds it correctly, and that the
/// whole path produces text rather than silence - none of which a fake can show.
/// </para>
/// <para>
/// It needs a model (~31 MB) and an audio file, so it is driven by environment
/// variables and skipped when they are absent. CI sets
/// <c>MEETINGRECORDER_REQUIRE_REAL_STT=1</c>, which turns a skip into a failure -
/// otherwise a broken CI step could quietly stop running this and nobody would
/// notice.
/// </para>
/// <para>
/// <b>What this does not establish:</b> the audio is machine-synthesised speech,
/// not a real meeting recorded through a real microphone, and CI synthesises
/// English because Windows runners have no Japanese voice installed. Japanese
/// accuracy on real meeting audio is T-23 in docs/WINDOWS_E2E_TEST.md and still
/// requires a physical machine.
/// </para>
/// </remarks>
public class RealModelRecognitionTests
{
    private readonly ITestOutputHelper _output;

    public RealModelRecognitionTests(ITestOutputHelper output) => _output = output;

    private static string? ModelPath => Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_MODEL");

    private static string? AudioPath => Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_AUDIO");

    private static string Language => Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_LANGUAGE") ?? "en";

    private static bool Required =>
        Environment.GetEnvironmentVariable("MEETINGRECORDER_REQUIRE_REAL_STT") == "1";

    /// <summary>Returns false when the fixtures are absent and that is allowed.</summary>
    private bool HaveFixtures()
    {
        var haveModel = !string.IsNullOrWhiteSpace(ModelPath) && File.Exists(ModelPath);
        var haveAudio = !string.IsNullOrWhiteSpace(AudioPath) && File.Exists(AudioPath);

        if (Required)
        {
            Assert.True(haveModel, $"MEETINGRECORDER_REQUIRE_REAL_STT=1 but the model is missing: '{ModelPath}'");
            Assert.True(haveAudio, $"MEETINGRECORDER_REQUIRE_REAL_STT=1 but the audio is missing: '{AudioPath}'");
        }

        if (!haveModel || !haveAudio)
        {
            _output.WriteLine("Skipped: set MEETINGRECORDER_TEST_MODEL and MEETINGRECORDER_TEST_AUDIO to run against a real model.");
            return false;
        }

        return true;
    }

    /// <summary>Loads the fixture as the 16 kHz mono the engine consumes.</summary>
    private float[] LoadAudioAt16k()
    {
        using var reader = new WavFileReader(AudioPath!);
        var samples = reader.ReadAllMono();
        _output.WriteLine($"Audio: {reader.SampleRate} Hz, {reader.Channels} ch, {reader.DurationSeconds:F2} s");

        if (reader.SampleRate == SpeechConstants.SampleRate)
        {
            return samples;
        }

        return new Resampler(reader.SampleRate, SpeechConstants.SampleRate).Process(samples);
    }

    [Fact]
    public void TranscribesRealSpeechIntoText()
    {
        if (!HaveFixtures())
        {
            return;
        }

        var audio = LoadAudioAt16k();
        var audioSeconds = audio.Length / (double)SpeechConstants.SampleRate;

        using var recognizer = new WhisperSpeechRecognizer(ModelPath!, "test-model", threads: 2);
        Assert.True(recognizer.IsReady);

        var stopwatch = Stopwatch.StartNew();
        var spans = recognizer.Transcribe(audio, Language, CancellationToken.None);
        stopwatch.Stop();

        var text = string.Join(" ", spans.Select(s => s.Text)).Trim();
        var realTimeFactor = stopwatch.Elapsed.TotalSeconds / Math.Max(0.001, audioSeconds);

        _output.WriteLine($"Recognized {spans.Count} span(s) in {stopwatch.Elapsed.TotalSeconds:F2} s (RTF {realTimeFactor:F2}):");
        _output.WriteLine(text);

        Assert.NotEmpty(spans);
        Assert.False(string.IsNullOrWhiteSpace(text), "the engine returned no text for real speech");

        // Timestamps must be inside the audio, otherwise transcript entries would
        // not line up with the recording.
        foreach (var span in spans)
        {
            Assert.InRange(span.StartMs, 0, (long)(audioSeconds * 1000) + 2000);
            Assert.True(span.EndMs >= span.StartMs, "a span ended before it started");
        }

        var expected = Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_EXPECT");
        if (!string.IsNullOrWhiteSpace(expected))
        {
            var wanted = expected!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var normalized = Normalize(text);
            var found = wanted.Where(w => normalized.Contains(Normalize(w), StringComparison.Ordinal)).ToList();

            _output.WriteLine($"Expected words found: {found.Count}/{wanted.Length} ({string.Join(", ", found)})");

            // Not every word survives a small quantised model, but recognising
            // almost none of them means the pipeline is feeding it noise.
            Assert.True(
                found.Count * 2 >= wanted.Length,
                $"only {found.Count} of {wanted.Length} expected words were recognised; got: {text}");
        }
    }

    [Fact]
    public void TheFullChunkerAndSchedulerPathProducesSegmentsFromRealSpeech()
    {
        if (!HaveFixtures())
        {
            return;
        }

        var audio = LoadAudioAt16k();

        // The real VAD and chunker, exactly as the recorder uses them.
        var chunker = new SpeechChunker(AudioSourceKind.SystemAudio);
        var chunks = new List<SpeechChunk>();
        for (var offset = 0; offset < audio.Length; offset += 1600)
        {
            var length = Math.Min(1600, audio.Length - offset);
            chunks.AddRange(chunker.Append(audio.AsSpan(offset, length)));
        }

        var tail = chunker.Flush();
        if (tail is not null)
        {
            chunks.Add(tail);
        }

        _output.WriteLine($"VAD produced {chunks.Count} chunk(s) totalling {chunks.Sum(c => c.DurationSeconds):F2} s of speech.");
        Assert.NotEmpty(chunks);

        var spill = Path.Combine(Path.GetTempPath(), "mr-real-stt-" + Guid.NewGuid().ToString("N"));
        var segments = new List<TranscriptSegment>();

        try
        {
            using var scheduler = new SttScheduler(spill) { Language = Language };
            scheduler.SegmentRecognized += segment =>
            {
                lock (segments)
                {
                    segments.Add(segment);
                }
            };

            scheduler.Start(new WhisperSpeechRecognizer(ModelPath!, "test-model", threads: 2));
            foreach (var chunk in chunks)
            {
                scheduler.Enqueue(chunk);
            }

            Assert.True(scheduler.DrainAndStop(TimeSpan.FromMinutes(10)), "the transcription queue did not drain");

            var status = scheduler.Status;
            _output.WriteLine($"Processed {status.ProcessedChunks} chunk(s), failed {status.FailedChunks}, measured RTF {status.RealTimeFactor:F2}");

            Assert.Equal(0, status.FailedChunks);
            Assert.Equal(chunks.Count, (int)status.ProcessedChunks);
            Assert.True(status.RealTimeFactor > 0, "no real-time factor was measured");

            scheduler.CleanupSpillDirectory();
        }
        finally
        {
            if (Directory.Exists(spill))
            {
                Directory.Delete(spill, true);
            }
        }

        var text = string.Join(" ", segments.OrderBy(s => s.StartMs).Select(s => s.Text));
        _output.WriteLine($"Transcript ({segments.Count} segment(s)): {text}");

        Assert.NotEmpty(segments);

        // The source stream is stamped by the chunker, never inferred.
        Assert.All(segments, s => Assert.Equal(AudioSourceKind.SystemAudio, s.Source));
    }

    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
