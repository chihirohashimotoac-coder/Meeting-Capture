using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Stt;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// Covers the second transcription pass: the part that re-reads a finished
/// meeting and replaces the latency-tuned live transcript with an accurate one.
/// </summary>
/// <remarks>
/// The recognizer is a test double here, because what has to be right in this
/// class is the walking, the windowing, the timing arithmetic and the source
/// attribution - not whisper's output. Whether the accurate settings actually
/// transcribe better is a property of the model and is measured against real
/// speech in CI, not asserted here.
/// </remarks>
public sealed class TranscriptionRefinerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-refine-" + Guid.NewGuid().ToString("N"));

    public TranscriptionRefinerTests() => Directory.CreateDirectory(_root);

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

    /// <summary>Writes speech-then-silence so the chunker has somewhere to cut.</summary>
    private string WriteAudio(string name, int utterances, double speechSeconds = 2.0, double gapSeconds = 1.2)
    {
        var path = Path.Combine(_root, name);
        using var writer = new WavFileWriter(path, SpeechConstants.SampleRate);

        for (var i = 0; i < utterances; i++)
        {
            writer.Write(SignalGenerator.Sine(300 + (i * 40), speechSeconds, SpeechConstants.SampleRate, 0.35));
            writer.Write(SignalGenerator.Silence(gapSeconds, SpeechConstants.SampleRate));
        }

        writer.Flush();
        return path;
    }

    [Fact]
    public void RefinesBothStreamsAndKeepsTheSourceOfEachSegment()
    {
        var mic = WriteAudio(RecognitionAudioNames.Microphone, utterances: 3);
        var system = WriteAudio(RecognitionAudioNames.SystemAudio, utterances: 2);

        var recognizer = new FakeSpeechRecognizer();
        var refined = new TranscriptionRefiner().Refine(
            RecognitionAudioNames.SourcesIn(_root),
            recognizer,
            "ja");

        Assert.NotEmpty(refined);

        // Source is the file a segment came from. Mixing the streams would make
        // it a guess, which is exactly what this design refuses to do.
        Assert.Contains(refined, s => s.Source == AudioSourceKind.Microphone);
        Assert.Contains(refined, s => s.Source == AudioSourceKind.SystemAudio);

        // Times are absolute within the meeting, and ordered.
        Assert.True(refined[0].StartMs >= 0);
        for (var i = 1; i < refined.Count; i++)
        {
            Assert.True(refined[i].StartMs >= refined[i - 1].StartMs, "segments must come back in time order");
        }
    }

    [Fact]
    public void UsesWindowsFarLongerThanTheLivePassDoes()
    {
        // The point of the second pass is context: whisper was trained on 30
        // second windows and a four second one throws most of that away. If this
        // ever regresses to live-pass chunk sizes the pass stops being worth its
        // runtime, and nothing else would notice.
        var mic = WriteAudio(RecognitionAudioNames.Microphone, utterances: 6, speechSeconds: 3.0, gapSeconds: 0.9);

        var recognizer = new FakeSpeechRecognizer();
        new TranscriptionRefiner().Refine(
            new[] { new RefinementSource(mic, AudioSourceKind.Microphone) },
            recognizer,
            "ja");

        int[] windows;
        lock (recognizer.ReceivedSampleCounts)
        {
            windows = recognizer.ReceivedSampleCounts.ToArray();
        }

        Assert.NotEmpty(windows);
        var longest = windows.Max() / (double)SpeechConstants.SampleRate;
        Assert.True(longest > 10.0, $"the longest window was only {longest:F1}s; the second pass should batch far more than that");

        // ...and never longer than whisper's receptive field, which would be
        // truncated by the engine without telling anyone.
        var overLimit = windows.Max() / (double)SpeechConstants.SampleRate;
        Assert.True(overLimit <= 30.0, $"a window of {overLimit:F1}s exceeds whisper's 30 second field");
    }

    [Fact]
    public void KeepsSpeakerNamesAPersonAssigned()
    {
        var mic = WriteAudio(RecognitionAudioNames.Microphone, utterances: 2);

        var previous = new List<TranscriptSegment>
        {
            new() { StartMs = 0, EndMs = 60_000, Source = AudioSourceKind.Microphone, SpeakerName = "田中", SpeakerId = 1, Text = "旧" },
        };

        var refined = new TranscriptionRefiner().Refine(
            new[] { new RefinementSource(mic, AudioSourceKind.Microphone) },
            new FakeSpeechRecognizer(),
            "ja",
            previous);

        // Re-transcribing must not throw away work a human did by hand.
        Assert.All(refined, segment => Assert.Equal("田中", segment.SpeakerName));
    }

    [Fact]
    public void DoesNotApplyASpeakerNameFromTheOtherStream()
    {
        var system = WriteAudio(RecognitionAudioNames.SystemAudio, utterances: 2);

        var previous = new List<TranscriptSegment>
        {
            new() { StartMs = 0, EndMs = 60_000, Source = AudioSourceKind.Microphone, SpeakerName = "田中", Text = "旧" },
        };

        var refined = new TranscriptionRefiner().Refine(
            new[] { new RefinementSource(system, AudioSourceKind.SystemAudio) },
            new FakeSpeechRecognizer(),
            "ja",
            previous);

        // A name attached to the room microphone says nothing about who was
        // speaking on the far end of the call.
        Assert.All(refined, segment => Assert.Null(segment.SpeakerName));
    }

    [Fact]
    public void ReportsProgressThatEndsAtOne()
    {
        var mic = WriteAudio(RecognitionAudioNames.Microphone, utterances: 4);
        var reports = new List<RefinementProgress>();

        new TranscriptionRefiner().Refine(
            new[] { new RefinementSource(mic, AudioSourceKind.Microphone) },
            new FakeSpeechRecognizer(),
            "ja",
            previous: null,
            progress: new Progress<RefinementProgress>(p =>
            {
                lock (reports)
                {
                    reports.Add(p);
                }
            }));

        // Progress is delivered through a Progress<T>, which posts asynchronously;
        // what matters is that the final report says the work is complete.
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                lock (reports)
                {
                    return reports.Count > 0 && reports[^1].Fraction >= 0.999;
                }
            },
            TimeSpan.FromSeconds(5)),
            "the refiner should finish by reporting complete progress");
    }

    [Fact]
    public void CancellationStopsTheWalkPromptly()
    {
        // Long enough that the walk spans several windows, so the cancellation
        // has somewhere to land other than the very end.
        var mic = WriteAudio(RecognitionAudioNames.Microphone, utterances: 24);
        using var cts = new CancellationTokenSource();

        // Cancelling from inside the first window rather than on a timer: a
        // wall-clock race would pass or fail depending on how loaded the machine
        // running the suite happens to be.
        var recognizer = new CancellingRecognizer(cts);

        Assert.Throws<OperationCanceledException>(() => new TranscriptionRefiner().Refine(
            new[] { new RefinementSource(mic, AudioSourceKind.Microphone) },
            recognizer,
            "ja",
            previous: null,
            progress: null,
            cancellationToken: cts.Token));

        Assert.True(recognizer.Calls < 3, $"the walk kept going for {recognizer.Calls} windows after being cancelled");

        // An already-cancelled token must not start the walk at all.
        using var precancelled = new CancellationTokenSource();
        precancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => new TranscriptionRefiner().Refine(
            new[] { new RefinementSource(mic, AudioSourceKind.Microphone) },
            new FakeSpeechRecognizer(),
            "ja",
            previous: null,
            progress: null,
            cancellationToken: precancelled.Token));
    }

    [Fact]
    public void SaysSoWhenThereIsNoAudioToRefine()
    {
        var error = Assert.Throws<FileNotFoundException>(() => new TranscriptionRefiner().Refine(
            RecognitionAudioNames.SourcesIn(_root),
            new FakeSpeechRecognizer(),
            "ja"));

        // The user needs to know this is a setting they can change, not a defect.
        Assert.Contains("録音時", error.Message);
    }

    [Fact]
    public void RefusesAudioThatIsNotAtTheEngineSampleRate()
    {
        // A 48 kHz file here means it is the mixed recording, not the recognition
        // audio. Resampling it quietly would lose the per-stream attribution and
        // nobody would know why the sources were suddenly wrong.
        var path = Path.Combine(_root, "wrong-rate.wav");
        using (var writer = new WavFileWriter(path, 48000))
        {
            writer.Write(SignalGenerator.Sine(440, 1.0, 48000, 0.3));
            writer.Flush();
        }

        Assert.Throws<InvalidDataException>(() => new TranscriptionRefiner().Refine(
            new[] { new RefinementSource(path, AudioSourceKind.Microphone) },
            new FakeSpeechRecognizer(),
            "ja"));
    }

    /// <summary>Cancels the walk from inside the first window it is asked to decode.</summary>
    private sealed class CancellingRecognizer : ISpeechRecognizer
    {
        private readonly CancellationTokenSource _cts;

        public CancellingRecognizer(CancellationTokenSource cts) => _cts = cts;

        public string ModelId => "cancelling-fake";

        public bool IsReady => true;

        public int Calls { get; private set; }

        public IReadOnlyList<RecognizedSpan> Transcribe(ReadOnlySpan<float> samples, string language, CancellationToken cancellationToken)
        {
            Calls++;
            _cts.Cancel();
            return Array.Empty<RecognizedSpan>();
        }

        public void Dispose()
        {
        }
    }
}
