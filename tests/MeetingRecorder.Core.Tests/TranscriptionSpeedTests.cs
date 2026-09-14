using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Stt;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace MeetingRecorder.Core.Tests;

public class TranscriptionProfileTests
{
    [Fact]
    public void TheAccurateProfileIsExactlyWhatTheApplicationDidBefore()
    {
        // The promise made when the setting was introduced: nobody's transcript
        // got worse. If these ever diverge, "high accuracy" has quietly become
        // something other than the behaviour it is named after.
        var accurate = SpeechRecognitionOptions.For(TranscriptionProfile.Accurate, 6, "ja");
        var historical = SpeechRecognitionOptions.Offline(6, "ja");

        Assert.Equal(historical, accurate);
        Assert.Equal(SpeechDecodingDefaults.BeamSize, accurate.BeamSize);
        Assert.Equal(SpeechDecodingDefaults.TemperatureIncrement, accurate.TemperatureIncrement);
    }

    [Fact]
    public void TheFastProfileIsGreedyWithNoTemperatureFallback()
    {
        var fast = SpeechRecognitionOptions.For(TranscriptionProfile.Fast, 6, "ja");

        Assert.Equal(1, fast.BeamSize);
        Assert.Equal(0f, fast.TemperatureIncrement);
    }

    [Fact]
    public void TheBalancedProfileSitsBetweenTheOtherTwo()
    {
        var fast = SpeechRecognitionOptions.For(TranscriptionProfile.Fast, 6, "ja");
        var balanced = SpeechRecognitionOptions.For(TranscriptionProfile.Balanced, 6, "ja");
        var accurate = SpeechRecognitionOptions.For(TranscriptionProfile.Accurate, 6, "ja");

        Assert.True(balanced.BeamSize > fast.BeamSize);
        Assert.True(balanced.BeamSize < accurate.BeamSize);
        Assert.True(balanced.TemperatureIncrement > 0f);
    }

    [Fact]
    public void AnUnrecognisedProfileFallsBackToTheMiddleRatherThanTheExtremes()
    {
        // A settings file somebody hand-edited, or one written by a later build.
        var unknown = SpeechRecognitionOptions.For((TranscriptionProfile)99, 6, "ja");

        Assert.Equal(SpeechRecognitionOptions.For(TranscriptionProfile.Balanced, 6, "ja"), unknown);
    }

    [Theory]
    [InlineData(TranscriptionProfile.Fast)]
    [InlineData(TranscriptionProfile.Balanced)]
    [InlineData(TranscriptionProfile.Accurate)]
    public void EveryProfileKeepsTheJapanesePunctuationPrompt(TranscriptionProfile profile)
    {
        // Without it whisper returns Japanese as one unbroken run of kana. It is
        // style guidance, not content, and no speed setting is a reason to drop
        // it - a fast transcript that cannot be read is not faster.
        var options = SpeechRecognitionOptions.For(profile, 4, "ja");

        Assert.Equal(SpeechRecognitionOptions.JapaneseMeetingPrompt, options.InitialPrompt);
    }

    [Fact]
    public void ANonJapaneseLanguageGetsNoJapanesePrompt()
    {
        Assert.Null(SpeechRecognitionOptions.For(TranscriptionProfile.Balanced, 4, "en").InitialPrompt);
    }

    [Fact]
    public void EveryProfileHasANameAndAnExplanation()
    {
        foreach (var profile in TranscriptionProfiles.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(TranscriptionProfiles.DisplayName(profile)));
            Assert.False(string.IsNullOrWhiteSpace(TranscriptionProfiles.Description(profile)));
        }
    }
}

/// <summary>
/// Measures how much audio the recognizer is actually asked to read, and how
/// many times it is asked.
/// </summary>
/// <remarks>
/// <para>
/// The number that decides how long a transcription takes is not the length of
/// the meeting - it is the number of windows, because whisper.cpp pads every
/// window out to its 30 second receptive field before the encoder runs. A
/// three second window and a twenty-six second window cost the same.
/// </para>
/// <para>
/// So these tests count windows on synthetic audio with a known structure. They
/// are a measurement of the windowing, not of whisper: nothing here claims a
/// wall-clock speed-up, only that the same audio now arrives in fewer, fuller
/// windows. What that is worth in seconds on a real machine is measured by
/// <c>TranscriptionProfileBenchmarkTests</c> against the real engine, and read
/// back from the application's own log by <c>tools/Show-Performance.ps1</c>.
/// </para>
/// </remarks>
public sealed class TranscriptionWindowingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-windowing-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public TranscriptionWindowingTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

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

    /// <summary>A conversation: utterances separated by pauses long enough to be pauses.</summary>
    private string WriteConversation(string name, int utterances, double speechSeconds, double gapSeconds)
    {
        var path = Path.Combine(_root, name);
        using var writer = new WavFileWriter(path, SpeechConstants.SampleRate);

        for (var i = 0; i < utterances; i++)
        {
            writer.Write(SignalGenerator.Sine(300 + (i * 17), speechSeconds, SpeechConstants.SampleRate, 0.35));
            writer.Write(SignalGenerator.Silence(gapSeconds, SpeechConstants.SampleRate));
        }

        writer.Flush();
        return path;
    }

    [Fact]
    public void WindowsAreFilledInsteadOfEndingAtEveryPause()
    {
        // Ten minutes of conversation: 4 second utterances with 2 second pauses.
        // Every pause is long enough to end a window under the old rule, so the
        // old rule produced one window per utterance.
        var path = WriteConversation("mic.wav", utterances: 100, speechSeconds: 4.0, gapSeconds: 2.0);

        var recognizer = new FakeSpeechRecognizer();
        var metrics = new TranscriptionMetrics();

        new OfflineTranscriptionService().Transcribe(
            new[] { new TranscriptionSource(path, AudioSourceKind.Microphone) },
            recognizer,
            "ja",
            metrics: metrics);

        int[] windows;
        lock (recognizer.ReceivedSampleCounts)
        {
            windows = recognizer.ReceivedSampleCounts.ToArray();
        }

        var meetingSeconds = 100 * 6.0;
        var encoderSeconds = windows.Length * TranscriptionMetrics.WhisperWindowSeconds;
        var before = CountWindowsTheOldWay(path);

        _output.WriteLine(
            $"{meetingSeconds:F0}s of audio -> {windows.Length} window(s), "
            + $"mean {windows.Average() / (double)SpeechConstants.SampleRate:F1}s, "
            + $"encoder-equivalent {encoderSeconds:F0}s "
            + $"({encoderSeconds / meetingSeconds:F2}x the meeting). "
            + $"Ending at the first pause would have produced {before} window(s), "
            + $"{before * TranscriptionMetrics.WhisperWindowSeconds / meetingSeconds:F2}x.");

        // The measurement that matters, taken both ways on the same audio rather
        // than asserted: filling windows has to cost the encoder substantially
        // fewer padded 30 second passes than ending one at every pause did.
        Assert.True(
            windows.Length * 2 < before,
            $"packing produced {windows.Length} windows against {before} the old way - not the improvement this is for");

        // In absolute terms: the floor is 30/27 = 1.11x the meeting length,
        // because a full window is 27 seconds of audio in a 30 second field.
        // Anything under 1.5x means the padding, not the pauses, is what is
        // left to pay for.
        Assert.True(
            encoderSeconds < meetingSeconds * 1.5,
            $"the encoder would run {encoderSeconds:F0}s of padded window for a {meetingSeconds:F0}s meeting");

        Assert.Equal(windows.Length, metrics.Chunks);
    }

    /// <summary>
    /// Counts the windows the chunker produced before it learned to fill them:
    /// end at the first silence past the minimum length.
    /// </summary>
    /// <remarks>
    /// The old settings are still reachable - they are the constructor's
    /// defaults - so the comparison is against the real previous behaviour and
    /// not against a reconstruction of it.
    /// </remarks>
    private static int CountWindowsTheOldWay(string path)
    {
        using var reader = new WavFileReader(path);
        var chunker = new SpeechChunker(
            AudioSourceKind.Microphone,
            maxChunkSeconds: OfflineTranscriptionService.MaxWindowSeconds,
            minChunkSeconds: 1.0,
            preRollMs: OfflineTranscriptionService.PreRollMs,
            silenceFlushMs: OfflineTranscriptionService.SilenceFlushMs,
            overlapMs: OfflineTranscriptionService.OverlapMs);

        var buffer = new float[SpeechConstants.SampleRate];
        int read;
        while ((read = reader.ReadMono(buffer)) > 0)
        {
            chunker.Append(buffer.AsSpan(0, read));
        }

        chunker.Flush();
        return (int)chunker.ChunkCount;
    }

    [Fact]
    public void NoWindowExceedsWhispersReceptiveField()
    {
        var path = WriteConversation("mic.wav", utterances: 40, speechSeconds: 4.0, gapSeconds: 1.0);

        var recognizer = new FakeSpeechRecognizer();
        new OfflineTranscriptionService().Transcribe(
            new[] { new TranscriptionSource(path, AudioSourceKind.Microphone) },
            recognizer,
            "ja");

        int[] windows;
        lock (recognizer.ReceivedSampleCounts)
        {
            windows = recognizer.ReceivedSampleCounts.ToArray();
        }

        var longest = windows.Max() / (double)SpeechConstants.SampleRate;
        Assert.True(longest <= 30.0, $"a window of {longest:F1}s would be truncated by the engine");
    }

    [Fact]
    public void ALongSilenceDoesNotEndUpInsideAWindow()
    {
        // One utterance, then two minutes of nothing, then one more. Without a
        // cap on how long a part-filled window waits, the first utterance would
        // collect the whole quiet stretch.
        var path = Path.Combine(_root, "sparse.wav");
        using (var writer = new WavFileWriter(path, SpeechConstants.SampleRate))
        {
            writer.Write(SignalGenerator.Sine(300, 3.0, SpeechConstants.SampleRate, 0.35));
            writer.Write(SignalGenerator.Silence(120.0, SpeechConstants.SampleRate));
            writer.Write(SignalGenerator.Sine(300, 3.0, SpeechConstants.SampleRate, 0.35));
            writer.Write(SignalGenerator.Silence(2.0, SpeechConstants.SampleRate));
            writer.Flush();
        }

        var recognizer = new FakeSpeechRecognizer();
        var metrics = new TranscriptionMetrics();
        new OfflineTranscriptionService().Transcribe(
            new[] { new TranscriptionSource(path, AudioSourceKind.Microphone) },
            recognizer,
            "ja",
            metrics: metrics);

        int[] windows;
        lock (recognizer.ReceivedSampleCounts)
        {
            windows = recognizer.ReceivedSampleCounts.ToArray();
        }

        Assert.Equal(2, windows.Length);
        foreach (var window in windows)
        {
            var seconds = window / (double)SpeechConstants.SampleRate;
            Assert.True(seconds < 10.0, $"a {seconds:F1}s window for a 3s utterance means the silence came with it");
        }

        // 128 seconds of file, six of which is speech, and only what is worth
        // decoding was submitted.
        Assert.True(metrics.SubmittedAudioSeconds < 15, $"submitted {metrics.SubmittedAudioSeconds:F1}s");
    }

    [Fact]
    public void EverySegmentStillLandsAtTheTimeItWasSpoken()
    {
        // Packing several utterances into one window is only safe because the
        // audio inside a window stays contiguous. If a window ever spliced the
        // pauses out, every timestamp after the first would drift.
        var path = Path.Combine(_root, "timed.wav");
        using (var writer = new WavFileWriter(path, SpeechConstants.SampleRate))
        {
            writer.Write(SignalGenerator.Silence(5.0, SpeechConstants.SampleRate));
            writer.Write(SignalGenerator.Sine(300, 2.0, SpeechConstants.SampleRate, 0.35));
            writer.Write(SignalGenerator.Silence(2.0, SpeechConstants.SampleRate));
            writer.Write(SignalGenerator.Sine(400, 2.0, SpeechConstants.SampleRate, 0.35));
            writer.Write(SignalGenerator.Silence(2.0, SpeechConstants.SampleRate));
            writer.Flush();
        }

        var segments = new OfflineTranscriptionService().Transcribe(
            new[] { new TranscriptionSource(path, AudioSourceKind.Microphone) },
            new SpanPerSecondRecognizer(),
            "ja");

        Assert.NotEmpty(segments);

        // Nothing may be dated before the first utterance started, allowing for
        // the pre-roll, and nothing after the file ends.
        foreach (var segment in segments)
        {
            Assert.InRange(segment.StartMs, 4000, 13000);
            Assert.InRange(segment.EndMs, segment.StartMs, 14000);
        }
    }

    [Fact]
    public void MetricsAccountForTheWholePass()
    {
        var mic = WriteConversation("mic.wav", utterances: 10, speechSeconds: 3.0, gapSeconds: 1.5);
        var system = WriteConversation("system.wav", utterances: 6, speechSeconds: 2.0, gapSeconds: 1.5);

        var metrics = new TranscriptionMetrics { Profile = TranscriptionProfile.Balanced, BeamSize = 2, Threads = 6 };
        var segments = new OfflineTranscriptionService().Transcribe(
            new[]
            {
                new TranscriptionSource(mic, AudioSourceKind.Microphone),
                new TranscriptionSource(system, AudioSourceKind.SystemAudio),
            },
            new FakeSpeechRecognizer(TimeSpan.FromMilliseconds(5)),
            "ja",
            metrics: metrics);

        Assert.Equal(segments.Count, metrics.Segments);
        Assert.Equal(2, metrics.SourceDurationSeconds.Count);

        // The meeting is as long as its longest stream, not the sum: a ten
        // minute meeting recorded on two streams is still ten minutes, and
        // reporting twenty would halve every ratio derived from it.
        Assert.Equal(
            metrics.SourceDurationSeconds.Values.Max(),
            metrics.MeetingDurationSeconds,
            precision: 3);

        Assert.True(metrics.Chunks > 0);
        Assert.True(metrics.SubmittedAudioSeconds > 0);
        Assert.True(metrics.SpeechSecondsDetected > 0);
        Assert.True(metrics.WhisperInference > TimeSpan.Zero);
        Assert.True(metrics.Total >= metrics.WhisperInference);
        Assert.Equal(TimeSpan.Zero, metrics.Diarization);

        var block = metrics.ToLogBlock();
        _output.WriteLine(block);

        foreach (var expected in new[]
                 {
                     "Meeting duration", "Speech audio submitted", "Chunks", "Model", "Beam",
                     "Threads", "Model load", "Whisper inference", "Diarization", "Total", "RTF",
                 })
        {
            Assert.Contains(expected, block, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SpeakerClusteringIsTimedSeparatelyFromTheRecognizer()
    {
        // B-5: the user can only make an informed decision about turning speaker
        // clustering off if its cost is reported apart from whisper's.
        var mic = WriteConversation("mic.wav", utterances: 6, speechSeconds: 3.0, gapSeconds: 1.5);

        var metrics = new TranscriptionMetrics();
        new OfflineTranscriptionService().Transcribe(
            new[] { new TranscriptionSource(mic, AudioSourceKind.Microphone) },
            new SpanPerSecondRecognizer(),
            "ja",
            diarizer: new SlowDiarizer(TimeSpan.FromMilliseconds(5)),
            metrics: metrics);

        Assert.True(metrics.Diarization > TimeSpan.Zero, "clustering must be measured, not folded into the recognizer's time");
        Assert.True(metrics.DiarizationEnabled);
    }

    [Fact]
    public void TranscriptionWorksWithClusteringOffAndKeepsTheStreamAttribution()
    {
        var mic = WriteConversation("mic.wav", utterances: 4, speechSeconds: 2.0, gapSeconds: 1.5);
        var system = WriteConversation("system.wav", utterances: 4, speechSeconds: 2.0, gapSeconds: 1.5);

        var metrics = new TranscriptionMetrics();
        var segments = new OfflineTranscriptionService().Transcribe(
            new[]
            {
                new TranscriptionSource(mic, AudioSourceKind.Microphone),
                new TranscriptionSource(system, AudioSourceKind.SystemAudio),
            },
            new FakeSpeechRecognizer(),
            "ja",
            diarizer: null,
            metrics: metrics);

        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.Null(s.SpeakerId));

        // Which stream a segment came from is not a model's opinion. It is which
        // file the audio was read out of, and turning clustering off cannot
        // touch it.
        Assert.Contains(segments, s => s.Source == AudioSourceKind.Microphone);
        Assert.Contains(segments, s => s.Source == AudioSourceKind.SystemAudio);
        Assert.False(metrics.DiarizationEnabled);
        Assert.Equal(TimeSpan.Zero, metrics.Diarization);
    }

    /// <summary>Returns one span per second of the window, so timestamps can be checked.</summary>
    private sealed class SpanPerSecondRecognizer : ISpeechRecognizer
    {
        public string ModelId => "span-per-second";

        public bool IsReady => true;

        public IReadOnlyList<RecognizedSpan> Transcribe(ReadOnlySpan<float> samples, string language, CancellationToken cancellationToken)
        {
            var seconds = samples.Length / SpeechConstants.SampleRate;
            var spans = new List<RecognizedSpan>();
            for (var i = 0; i < seconds; i++)
            {
                spans.Add(new RecognizedSpan(i * 1000L, (i + 1) * 1000L, $"s{i}", 0.9f));
            }

            return spans;
        }

        public void Dispose()
        {
        }
    }

    private sealed class SlowDiarizer : ISpeakerDiarizer
    {
        private readonly TimeSpan _delay;

        public SlowDiarizer(TimeSpan delay) => _delay = delay;

        public bool IsEnabled { get; set; } = true;

        public int? Identify(AudioSourceKind source, ReadOnlySpan<float> samples16k)
        {
            Thread.Sleep(_delay);
            return 0;
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }
}
