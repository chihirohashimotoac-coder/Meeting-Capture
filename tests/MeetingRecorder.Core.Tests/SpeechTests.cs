using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Stt;
using MeetingRecorder.Core.Tests.TestSupport;
using Xunit;

namespace MeetingRecorder.Core.Tests;

public class EnergyVadTests
{
    [Fact]
    public void DetectsSpeechAndReleasesAfterTheHangover()
    {
        var vad = new EnergyVad();
        var signal = SignalGenerator.Concat(
            SignalGenerator.Noise(1.0, 16000, 0.0005),
            SignalGenerator.Sine(250, 0.5, 16000, 0.2),
            SignalGenerator.Noise(2.0, 16000, 0.0005));

        var speechFrames = 0;
        var states = new List<bool>();

        for (var offset = 0; offset + vad.FrameSamples <= signal.Length; offset += vad.FrameSamples)
        {
            var isSpeech = vad.ProcessFrame(signal.AsSpan(offset, vad.FrameSamples));
            states.Add(isSpeech);
            if (isSpeech)
            {
                speechFrames++;
            }
        }

        Assert.True(speechFrames > 20, "the tone should be detected as speech");
        Assert.False(states[^1], "the detector must close again after the hangover");
    }

    [Fact]
    public void DoesNotFireOnSteadyBackgroundNoise()
    {
        var vad = new EnergyVad();
        var noise = SignalGenerator.Noise(5.0, 16000, 0.002);

        var speechFrames = 0;
        for (var offset = 0; offset + vad.FrameSamples <= noise.Length; offset += vad.FrameSamples)
        {
            if (vad.ProcessFrame(noise.AsSpan(offset, vad.FrameSamples)))
            {
                speechFrames++;
            }
        }

        var totalFrames = noise.Length / vad.FrameSamples;
        Assert.True(speechFrames < totalFrames * 0.1, $"{speechFrames}/{totalFrames} frames were misclassified as speech");
    }

    [Fact]
    public void CatchesAVeryShortUtterance()
    {
        var vad = new EnergyVad();

        // 180 ms - roughly a Japanese back-channel "はい".
        var signal = SignalGenerator.Concat(
            SignalGenerator.Noise(2.0, 16000, 0.0005),
            SignalGenerator.Sine(220, 0.18, 16000, 0.15),
            SignalGenerator.Noise(0.5, 16000, 0.0005));

        var detected = false;
        for (var offset = 0; offset + vad.FrameSamples <= signal.Length; offset += vad.FrameSamples)
        {
            if (vad.ProcessFrame(signal.AsSpan(offset, vad.FrameSamples)))
            {
                detected = true;
            }
        }

        Assert.True(detected, "short back-channel responses must not be discarded");
    }
}

public class SpeechChunkerTests
{
    [Fact]
    public void EmitsAChunkAfterSpeechStopsAndTagsItsSource()
    {
        var chunker = new SpeechChunker(AudioSourceKind.SystemAudio);
        var signal = SignalGenerator.Concat(
            SignalGenerator.Noise(0.5, 16000, 0.0005),
            SignalGenerator.Sine(250, 1.0, 16000, 0.2),
            SignalGenerator.Noise(2.0, 16000, 0.0005));

        var chunks = new List<SpeechChunk>();
        for (var offset = 0; offset < signal.Length; offset += 1600)
        {
            var length = Math.Min(1600, signal.Length - offset);
            chunks.AddRange(chunker.Append(signal.AsSpan(offset, length)));
        }

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.Equal(AudioSourceKind.SystemAudio, chunk.Source));
        Assert.True(chunks[0].DurationSeconds > 0.9, "the emitted chunk should contain the whole utterance");
    }

    [Fact]
    public void IncludesPreRollSoTheOnsetIsNotClipped()
    {
        var chunker = new SpeechChunker(AudioSourceKind.Microphone);
        var signal = SignalGenerator.Concat(
            SignalGenerator.Noise(1.0, 16000, 0.0005),
            SignalGenerator.Sine(250, 0.8, 16000, 0.25),
            SignalGenerator.Noise(2.0, 16000, 0.0005));

        var chunks = new List<SpeechChunk>();
        for (var offset = 0; offset < signal.Length; offset += 1600)
        {
            var length = Math.Min(1600, signal.Length - offset);
            chunks.AddRange(chunker.Append(signal.AsSpan(offset, length)));
        }

        var chunk = Assert.Single(chunks);

        // Speech starts at 1.0 s; with pre-roll the chunk must begin before that.
        Assert.True(chunk.StartMs <= 1000, $"chunk started at {chunk.StartMs} ms, after the onset");
        Assert.True(chunk.StartMs >= 600, $"chunk started at {chunk.StartMs} ms, far too early");
    }

    [Fact]
    public void SplitsLongSpeechAndOverlapsTheSeam()
    {
        var chunker = new SpeechChunker(AudioSourceKind.Microphone, maxChunkSeconds: 2.0, overlapMs: 400);
        var signal = SignalGenerator.Sine(250, 9.0, 16000, 0.25);

        var chunks = new List<SpeechChunk>();
        for (var offset = 0; offset < signal.Length; offset += 1600)
        {
            var length = Math.Min(1600, signal.Length - offset);
            chunks.AddRange(chunker.Append(signal.AsSpan(offset, length)));
        }

        Assert.True(chunks.Count >= 4, $"expected several chunks, got {chunks.Count}");

        for (var i = 1; i < chunks.Count; i++)
        {
            // Each chunk must start before the previous one ended: that overlap is
            // what stops a word from being lost at the seam.
            Assert.True(chunks[i].StartMs < chunks[i - 1].EndMs, "chunks must overlap at the seam");
        }
    }

    [Fact]
    public void FlushEmitsTheTailWhenRecordingStops()
    {
        var chunker = new SpeechChunker(AudioSourceKind.Microphone);
        var signal = SignalGenerator.Concat(
            SignalGenerator.Noise(0.3, 16000, 0.0005),
            SignalGenerator.Sine(250, 0.8, 16000, 0.25));

        chunker.Append(signal);
        var tail = chunker.Flush();

        Assert.NotNull(tail);
        Assert.True(tail!.DurationSeconds > 0.5);
    }

    [Fact]
    public void EmitsNothingForPureSilence()
    {
        var chunker = new SpeechChunker(AudioSourceKind.Microphone);
        var chunks = chunker.Append(SignalGenerator.Silence(5.0, 16000));

        Assert.Empty(chunks);
        Assert.Null(chunker.Flush());
    }
}

public sealed class SttSchedulerTests : IDisposable
{
    private readonly string _spill = Path.Combine(Path.GetTempPath(), "mr-spill-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_spill))
            {
                Directory.Delete(_spill, true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TranscribesEveryQueuedChunkAndStampsTheSource()
    {
        using var scheduler = new SttScheduler(_spill);
        var segments = new List<TranscriptSegment>();
        scheduler.SegmentRecognized += segment =>
        {
            lock (segments)
            {
                segments.Add(segment);
            }
        };

        scheduler.Start(new FakeSpeechRecognizer());

        for (var i = 0; i < 20; i++)
        {
            var source = i % 2 == 0 ? AudioSourceKind.Microphone : AudioSourceKind.SystemAudio;
            scheduler.Enqueue(new SpeechChunk(i * 1000, new float[16000], source, i));
        }

        Assert.True(scheduler.DrainAndStop(TimeSpan.FromSeconds(30)));

        Assert.Equal(20, segments.Count);
        Assert.Equal(10, segments.Count(s => s.Source == AudioSourceKind.Microphone));
        Assert.Equal(10, segments.Count(s => s.Source == AudioSourceKind.SystemAudio));
    }

    [Fact]
    public void SegmentTimestampsAreOffsetByTheChunkStart()
    {
        using var scheduler = new SttScheduler(_spill);
        var segments = new List<TranscriptSegment>();
        scheduler.SegmentRecognized += segment =>
        {
            lock (segments)
            {
                segments.Add(segment);
            }
        };

        scheduler.Start(new FakeSpeechRecognizer());
        scheduler.Enqueue(new SpeechChunk(125_000, new float[16000], AudioSourceKind.Microphone, 0));
        scheduler.DrainAndStop(TimeSpan.FromSeconds(30));

        var segment = Assert.Single(segments);
        Assert.Equal(125_000, segment.StartMs);
        Assert.Equal(126_000, segment.EndMs);
    }

    [Fact]
    public void SpillsToDiskInsteadOfDroppingAudioWhenTheQueueGrows()
    {
        using var scheduler = new SttScheduler(_spill)
        {
            // Force spilling after a couple of chunks.
            MaxInMemorySeconds = 2.0,
        };

        var recognizer = new FakeSpeechRecognizer(TimeSpan.FromMilliseconds(30));
        var segments = 0;
        scheduler.SegmentRecognized += _ => Interlocked.Increment(ref segments);
        scheduler.Start(recognizer);

        const int total = 40;
        for (var i = 0; i < total; i++)
        {
            scheduler.Enqueue(new SpeechChunk(i * 1000, SignalGenerator.Sine(300, 1.0, 16000, 0.2), AudioSourceKind.Microphone, i));
        }

        Assert.True(scheduler.Status.SpilledChunks > 0, "the scheduler should have parked chunks on disk");
        Assert.True(scheduler.DrainAndStop(TimeSpan.FromSeconds(60)));

        // Nothing may be lost, whether it was held in RAM or on disk.
        Assert.Equal(total, segments);
        Assert.Equal(total, scheduler.Status.ProcessedChunks);
    }

    [Fact]
    public void ReportsHowFarBehindTheTranscriptIs()
    {
        using var scheduler = new SttScheduler(_spill);
        scheduler.Start(new FakeSpeechRecognizer(TimeSpan.FromMilliseconds(200)));

        for (var i = 0; i < 10; i++)
        {
            scheduler.Enqueue(new SpeechChunk(i * 1000, new float[16000], AudioSourceKind.Microphone, i));
        }

        scheduler.ReportRecordingPosition(10_000);
        var status = scheduler.Status;

        Assert.True(status.QueuedSeconds > 0);
        Assert.True(status.LatencySeconds > 0, "lateness must be visible to the UI");

        scheduler.DrainAndStop(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void SurvivesARecognizerThatThrows()
    {
        using var scheduler = new SttScheduler(_spill);
        scheduler.Start(new ThrowingRecognizer());

        for (var i = 0; i < 5; i++)
        {
            scheduler.Enqueue(new SpeechChunk(i * 1000, new float[16000], AudioSourceKind.Microphone, i));
        }

        Assert.True(scheduler.DrainAndStop(TimeSpan.FromSeconds(30)));
        Assert.Equal(5, scheduler.Status.FailedChunks);
    }

    [Fact]
    public void CleansUpItsSpillDirectory()
    {
        using var scheduler = new SttScheduler(_spill) { MaxInMemorySeconds = 0.5 };
        scheduler.Start(new FakeSpeechRecognizer(TimeSpan.FromMilliseconds(20)));

        for (var i = 0; i < 20; i++)
        {
            scheduler.Enqueue(new SpeechChunk(i * 1000, new float[16000], AudioSourceKind.Microphone, i));
        }

        scheduler.DrainAndStop(TimeSpan.FromSeconds(60));
        scheduler.CleanupSpillDirectory();

        Assert.False(Directory.Exists(_spill));
    }

    private sealed class ThrowingRecognizer : ISpeechRecognizer
    {
        public string ModelId => "throwing";

        public bool IsReady => true;

        public IReadOnlyList<RecognizedSpan> Transcribe(ReadOnlySpan<float> samples, string language, CancellationToken cancellationToken)
            => throw new InvalidOperationException("simulated engine failure");

        public void Dispose()
        {
        }
    }
}
