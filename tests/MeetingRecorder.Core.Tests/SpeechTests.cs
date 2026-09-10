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
