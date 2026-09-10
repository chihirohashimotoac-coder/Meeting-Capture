using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Stt;

/// <summary>
/// Turns one continuous 16 kHz stream into chunks worth transcribing.
/// </summary>
/// <remarks>
/// <para>
/// One chunker runs per capture stream. That is what makes
/// <see cref="TranscriptSegment.Source"/> ground truth rather than a guess: a
/// chunk physically comes from either the microphone ring or the loopback ring,
/// and it is tagged at construction.
/// </para>
/// <para>
/// Because the VAD gates each stream independently, the work stays close to
/// "one stream's worth" in practice - in a meeting either the room or the remote
/// side is talking, rarely both - while still never attributing a sentence to
/// the wrong side.
/// </para>
/// <para>
/// It is used only by <see cref="OfflineTranscriptionService"/>, which asks it
/// for the longest windows whisper can use. Nothing calls it while a recording
/// is running.
/// </para>
/// <para>
/// A chunk is emitted when speech has stopped for <c>SilenceFlushMs</c>, or when
/// the maximum chunk length is reached mid-sentence. In the second case the
/// tail is replayed as the head of the next chunk (<c>OverlapMs</c>) so a word
/// split across the seam is still recognized.
/// </para>
/// </remarks>
public sealed class SpeechChunker
{
    private readonly EnergyVad _vad;
    private readonly AudioSourceKind _source;
    private readonly List<float> _pending = new();
    private readonly float[] _preRoll;
    private readonly int _preRollSamples;
    private readonly int _maxSamples;
    private readonly int _minSamples;
    private readonly int _silenceFlushFrames;
    private readonly int _overlapSamples;
    private readonly List<float> _frameBuffer = new();

    private int _preRollCount;
    private int _preRollWrite;
    private int _silenceFrames;
    private long _pendingStartMs = -1;
    private long _consumedSamples;
    private long _sequence;

    public SpeechChunker(
        AudioSourceKind source,
        double maxChunkSeconds = 8.0,
        double minChunkSeconds = 0.35,
        double preRollMs = 300,
        double silenceFlushMs = 700,
        double overlapMs = 400,
        EnergyVad? vad = null)
    {
        _source = source;
        _vad = vad ?? new EnergyVad();
        _preRollSamples = (int)(SpeechConstants.SampleRate * preRollMs / 1000.0);
        _preRoll = new float[Math.Max(1, _preRollSamples)];
        _maxSamples = (int)(SpeechConstants.SampleRate * maxChunkSeconds);
        _minSamples = (int)(SpeechConstants.SampleRate * minChunkSeconds);
        _silenceFlushFrames = Math.Max(1, (int)(silenceFlushMs / 1000.0 * SpeechConstants.SampleRate / _vad.FrameSamples));
        _overlapSamples = (int)(SpeechConstants.SampleRate * overlapMs / 1000.0);
    }

    public bool IsSpeechActive => _vad.IsSpeech;

    public double NoiseFloorDb => _vad.NoiseFloorDb;

    /// <summary>Feeds 16 kHz mono audio and returns any chunks that became ready.</summary>
    public IReadOnlyList<SpeechChunk> Append(ReadOnlySpan<float> samples)
    {
        List<SpeechChunk>? ready = null;

        for (var i = 0; i < samples.Length; i++)
        {
            _frameBuffer.Add(samples[i]);
            if (_frameBuffer.Count < _vad.FrameSamples)
            {
                continue;
            }

            var frame = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_frameBuffer);
            var wasSpeech = _vad.IsSpeech;
            var isSpeech = _vad.ProcessFrame(frame);
            var frameStartMs = (_consumedSamples * 1000) / SpeechConstants.SampleRate;

            if (isSpeech)
            {
                if (!wasSpeech && _pending.Count == 0)
                {
                    // Start a new utterance with the pre-roll so the onset is intact.
                    _pendingStartMs = Math.Max(0, frameStartMs - (_preRollCount * 1000L / SpeechConstants.SampleRate));
                    AppendPreRoll();
                }

                _pending.AddRange(frame);
                _silenceFrames = 0;
            }
            else if (_pending.Count > 0)
            {
                // Keep recording through the silence so trailing particles and the
                // gap between two sentences of the same utterance survive.
                _pending.AddRange(frame);
                _silenceFrames++;
            }
            else
            {
                PushPreRoll(frame);
            }

            _consumedSamples += frame.Length;
            _frameBuffer.Clear();

            var flushForSilence = _pending.Count >= _minSamples && _silenceFrames >= _silenceFlushFrames;
            var flushForLength = _pending.Count >= _maxSamples;

            if (flushForSilence || flushForLength)
            {
                var chunk = Emit(keepOverlap: flushForLength && !flushForSilence);
                if (chunk is not null)
                {
                    (ready ??= new List<SpeechChunk>()).Add(chunk);
                }
            }
        }

        return (IReadOnlyList<SpeechChunk>?)ready ?? Array.Empty<SpeechChunk>();
    }

    /// <summary>Emits whatever is buffered. Called when recording stops.</summary>
    public SpeechChunk? Flush()
    {
        if (_frameBuffer.Count > 0)
        {
            _consumedSamples += _frameBuffer.Count;

            // Only when an utterance is actually in progress. The frame buffer
            // holds less than one 20 ms VAD frame, so on its own it is a scrap of
            // audio nobody was speaking into: promoting it to a chunk spends a
            // recognition on silence and appends a stray segment to the
            // transcript - visible when a muted stream still produced one.
            if (_pending.Count > 0)
            {
                _pending.AddRange(_frameBuffer);
            }

            _frameBuffer.Clear();
        }

        return _pending.Count > 0 ? Emit(keepOverlap: false) : null;
    }

    private SpeechChunk? Emit(bool keepOverlap)
    {
        if (_pending.Count == 0)
        {
            return null;
        }

        var samples = _pending.ToArray();
        var startMs = _pendingStartMs < 0 ? 0 : _pendingStartMs;
        var chunk = new SpeechChunk(startMs, samples, _source, _sequence++);

        _pending.Clear();
        _silenceFrames = 0;

        if (keepOverlap && samples.Length > _overlapSamples && _overlapSamples > 0)
        {
            var tail = samples.AsSpan(samples.Length - _overlapSamples);
            _pending.AddRange(tail);
            _pendingStartMs = chunk.EndMs - (_overlapSamples * 1000L / SpeechConstants.SampleRate);
        }
        else
        {
            _pendingStartMs = -1;
            ResetPreRoll();
        }

        return chunk;
    }

    private void PushPreRoll(ReadOnlySpan<float> frame)
    {
        if (_preRollSamples <= 0)
        {
            return;
        }

        foreach (var sample in frame)
        {
            _preRoll[_preRollWrite] = sample;
            _preRollWrite = (_preRollWrite + 1) % _preRoll.Length;
            if (_preRollCount < _preRoll.Length)
            {
                _preRollCount++;
            }
        }
    }

    private void AppendPreRoll()
    {
        if (_preRollCount == 0)
        {
            return;
        }

        var start = (_preRollWrite - _preRollCount + _preRoll.Length) % _preRoll.Length;
        for (var i = 0; i < _preRollCount; i++)
        {
            _pending.Add(_preRoll[(start + i) % _preRoll.Length]);
        }

        ResetPreRoll();
    }

    private void ResetPreRoll()
    {
        _preRollCount = 0;
        _preRollWrite = 0;
    }

    public void Reset()
    {
        _pending.Clear();
        _frameBuffer.Clear();
        ResetPreRoll();
        _vad.Reset();
        _silenceFrames = 0;
        _pendingStartMs = -1;
        _consumedSamples = 0;
    }
}
