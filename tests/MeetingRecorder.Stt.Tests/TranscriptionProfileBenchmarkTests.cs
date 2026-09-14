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
/// <para><b>And it asserts no ordering between the profiles.</b> It used to, and
/// the assertion was wrong. On CI's fixture - 6.7 seconds of audio, one window,
/// 85 characters out - the run came back 2.56 s greedy, 2.55 s at beam 2,
/// 1.89 s at beam 5, monotonically decreasing in the order the three ran rather
/// than in beam width. That is warm-up, not decoding: one window is one encoder
/// pass whatever the beam is, and twenty-five output tokens of decoding is far
/// too little work to show through the first run's JIT, page faults and clock
/// ramp. A warm-up pass now runs before the table is measured, which helps, but
/// the honest conclusion stands - a fixture this short cannot separate the
/// profiles, and a test that claimed it could would be measuring the runner's
/// mood. Point it at ten minutes of real meeting audio and the table means
/// something; what is asserted here is only what is true of the code.
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

            // One pass whose numbers are thrown away, so the first profile in
            // the table is not the one that pays for the JIT, the native load
            // and the CPU coming up to speed.
            using (var warmUp = new WhisperSpeechRecognizer(
                       ModelPath!, "warm-up", SpeechRecognitionOptions.For(TranscriptionProfile.Fast, Threads, Language)))
            {
                new OfflineTranscriptionService().Transcribe(sources, warmUp, Language);
            }

            table.AppendLine(c, $"Audio: {audioSeconds:F1} s, model {Path.GetFileName(ModelPath)}, "
                              + $"{Threads} threads, language {Language}, {Environment.ProcessorCount} logical cores");
            table.AppendLine();
            table.AppendLine("| profile  | beam | temp inc | model load | inference |   total | RTF  | chunks | chars |");
            table.AppendLine("|----------|------|----------|------------|-----------|---------|------|--------|-------|");

            var results = new List<ProfileResult>();

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
                results.Add(new ProfileResult(profile, options, metrics.TotalIncludingModelLoad, metrics.Chunks, characters));

                table.AppendLine(c,
                    $"| {TranscriptionProfiles.DisplayName(profile),-8} | {options.BeamSize,4} | "
                    + $"{options.TemperatureIncrement,8:F2} | {metrics.ModelLoad.TotalSeconds,9:F2}s | "
                    + $"{metrics.WhisperInference.TotalSeconds,8:F2}s | {metrics.TotalIncludingModelLoad.TotalSeconds,6:F2}s | "
                    + $"{metrics.RealTimeFactor,4:F2} | {metrics.Chunks,6} | {characters,5} |");

                // A profile that returns nothing is not fast, it is broken.
                Assert.NotEmpty(segments);
                Assert.True(characters > 0, $"the {profile} profile produced no text at all");
            }

            table.AppendLine();
            table.AppendLine(
                "The times above are this runner's, on this fixture. Nothing is asserted about their");
            table.AppendLine(
                "order: see the class remarks for why a fixture this short cannot separate the profiles.");

            _output.WriteLine(table.ToString());

            // What is asserted is what is true of the code rather than of the
            // machine it ran on.

            // Each profile reached the engine as the profile it claims to be.
            Assert.Equal(1, results.Single(r => r.Profile == TranscriptionProfile.Fast).Options.BeamSize);
            Assert.Equal(0f, results.Single(r => r.Profile == TranscriptionProfile.Fast).Options.TemperatureIncrement);
            Assert.Equal(
                SpeechDecodingDefaults.BeamSize,
                results.Single(r => r.Profile == TranscriptionProfile.Accurate).Options.BeamSize);

            // The windowing is decided before the decoder is, so the same audio
            // has to produce the same number of windows at every profile. If it
            // ever does not, the profile is changing something it has no business
            // changing.
            Assert.Single(results.Select(r => r.Chunks).Distinct());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    /// <param name="Profile">Which profile produced this row.</param>
    /// <param name="Options">The decoder settings it actually asked for.</param>
    /// <param name="Total">Model load plus the whole pass.</param>
    /// <param name="Chunks">Windows the recognizer was handed.</param>
    /// <param name="Characters">Transcript length. How much came back, not how much was right.</param>
    private sealed record ProfileResult(
        TranscriptionProfile Profile,
        SpeechRecognitionOptions Options,
        TimeSpan Total,
        int Chunks,
        int Characters);
}
