using System.Diagnostics;
using System.Globalization;
using System.Text;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Stt;
using Xunit;
using Xunit.Abstractions;

namespace MeetingRecorder.Stt.Tests;

/// <summary>
/// Runs the same audio through every speed profile with the real engine and
/// prints what each one cost.
/// </summary>
/// <remarks>
/// <para>
/// This is the answer to "is the fast profile actually faster, and by how
/// much?" - a question that cannot be answered by a test double, by reading the
/// beam size, or by reasoning about decoder complexity. It has to be run.
/// </para>
/// <para>
/// It is driven by the same environment variables as
/// <see cref="RealModelRecognitionTests"/> and skipped without them, so it costs
/// nothing on a runner that has no model. Point
/// <c>MEETINGRECORDER_TEST_AUDIO</c> at a real Japanese meeting recording and
/// <c>MEETINGRECORDER_TEST_MODEL</c> at the model actually in use, and the
/// numbers it prints are that machine's numbers - which is the only kind worth
/// having. Somebody running the packaged application rather than the SDK gets
/// the same breakdown from its own log, tabulated by
/// <c>tools/Show-Performance.ps1</c>.
/// </para>
/// <para><b>What it does not measure.</b> Accuracy. Comparing transcripts
/// requires a reference transcription of the same audio, which this repository
/// does not have and cannot synthesise; the character counts printed below say
/// how much text came back, not how much of it was right. Anyone reporting a
/// profile as "accurate enough" on the strength of this table would be reporting
/// something it did not measure.
/// </para>
/// </remarks>
public class TranscriptionProfileBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public TranscriptionProfileBenchmarkTests(ITestOutputHelper output) => _output = output;

    private static string? ModelPath => Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_MODEL");

    private static string? AudioPath => Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_AUDIO");

    private static string Language => Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_LANGUAGE") ?? "en";

    private static bool Required => Environment.GetEnvironmentVariable("MEETINGRECORDER_REQUIRE_REAL_STT") == "1";

    private static int Threads
        => int.TryParse(Environment.GetEnvironmentVariable("MEETINGRECORDER_TEST_THREADS"), out var value) && value > 0
            ? value
            : Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    [Fact]
    public void EveryProfileTranscribesTheSameAudioAndTheCostOfEachIsReported()
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
            _output.WriteLine(
                "Skipped: set MEETINGRECORDER_TEST_MODEL and MEETINGRECORDER_TEST_AUDIO to benchmark the profiles "
                + "against a real model.");
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "mr-profile-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var workingPath = Path.Combine(directory, RecognitionAudioNames.SystemAudio);
            double audioSeconds;

            using (var reader = new WavFileReader(AudioPath!))
            {
                var samples = reader.SampleRate == SpeechConstants.SampleRate
                    ? reader.ReadAllMono()
                    : new Resampler(reader.SampleRate, SpeechConstants.SampleRate).Process(reader.ReadAllMono());

                using var writer = new WavFileWriter(workingPath, SpeechConstants.SampleRate);
                writer.Write(samples);
                writer.Flush();
                audioSeconds = samples.Length / (double)SpeechConstants.SampleRate;
            }

            var sources = RecognitionAudioNames.SourcesIn(directory);
            var table = new StringBuilder();
            var c = CultureInfo.InvariantCulture;

            table.AppendLine(c, $"Audio: {audioSeconds:F1} s, model {Path.GetFileName(ModelPath)}, "
                              + $"{Threads} threads, language {Language}, {Environment.ProcessorCount} logical cores");
            table.AppendLine();
            table.AppendLine("| profile  | beam | temp inc | model load | inference |   total | RTF  | chunks | chars |");
            table.AppendLine("|----------|------|----------|------------|-----------|---------|------|--------|-------|");

            var results = new List<(TranscriptionProfile Profile, TimeSpan Total, int Characters)>();

            foreach (var profile in TranscriptionProfiles.All)
            {
                var options = SpeechRecognitionOptions.For(profile, Threads, Language);

                var load = Stopwatch.StartNew();
                using var recognizer = new WhisperSpeechRecognizer(ModelPath!, "benchmark", options);
                load.Stop();

                var metrics = new TranscriptionMetrics
                {
                    ModelLoad = load.Elapsed,
                    Profile = profile,
                    BeamSize = options.BeamSize,
                    TemperatureIncrement = options.TemperatureIncrement,
                    Threads = options.Threads,
                };

                var segments = new OfflineTranscriptionService().Transcribe(
                    sources, recognizer, Language, metrics: metrics);

                var characters = segments.Sum(s => s.Text?.Length ?? 0);
                results.Add((profile, metrics.TotalIncludingModelLoad, characters));

                table.AppendLine(c,
                    $"| {TranscriptionProfiles.DisplayName(profile),-8} | {options.BeamSize,4} | "
                    + $"{options.TemperatureIncrement,8:F2} | {metrics.ModelLoad.TotalSeconds,9:F2}s | "
                    + $"{metrics.WhisperInference.TotalSeconds,8:F2}s | {metrics.TotalIncludingModelLoad.TotalSeconds,6:F2}s | "
                    + $"{metrics.RealTimeFactor,4:F2} | {metrics.Chunks,6} | {characters,5} |");

                // A profile that returns nothing is not fast, it is broken.
                Assert.NotEmpty(segments);
                Assert.True(characters > 0, $"the {profile} profile produced no text at all");
            }

            _output.WriteLine(table.ToString());

            // The one relationship that is a property of the code rather than of
            // the machine: greedy decoding cannot be slower than a beam search of
            // five over the same audio on the same engine. A 25% allowance covers
            // ordinary scheduling noise on a shared runner.
            var fast = results.Single(r => r.Profile == TranscriptionProfile.Fast).Total;
            var accurate = results.Single(r => r.Profile == TranscriptionProfile.Accurate).Total;

            Assert.True(
                fast <= accurate * 1.25,
                $"the fast profile took {fast.TotalSeconds:F2}s against the accurate profile's {accurate.TotalSeconds:F2}s");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
