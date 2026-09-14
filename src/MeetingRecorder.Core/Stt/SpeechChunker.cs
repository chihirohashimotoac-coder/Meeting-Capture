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
    private readonly int _targetSamples;
    private readonly int _maxCarriedSilenceFrames;
    private readonly List<float> _frameBuffer = new();

    private int _preRollCount;
    private int _preRollWrite;
    private int _silenceFrames;
    private long _pendingStartMs = -1;
    private long _consumedSamples;
    private long _sequence;
    private long _speechFrames;
    private long _emittedSamples;

    /// <param name="source">Capture stream this chunker is fed from.</param>
    /// <param name="maxChunkSeconds">Hard ceiling on one chunk.</param>
    /// <param name="minChunkSeconds">Below this, a silence never ends a chunk.</param>
    /// <param name="preRollMs">Audio kept from before the onset.</param>
    /// <param name="silenceFlushMs">Silence that marks a place a chunk may end.</param>
    /// <param name="overlapMs">Tail replayed when a chunk had to be cut for length.</param>
    /// <param name="vad">Voice activity detector; a default one is built when null.</param>
    /// <param name="targetChunkSeconds">
    /// Fill this much before a silence is allowed to end a chunk. Zero keeps the
    /// old behaviour of ending at the first silence past
    /// <paramref name="minChunkSeconds"/>.
    /// </param>
    /// <param name="maxCarriedSilenceMs">
    /// How much silence a half-full chunk will sit through before giving up and
    /// emitting anyway. Zero means "no limit", which is only safe when
    /// <paramref name="targetChunkSeconds"/> is zero too.
    /// </param>
    public SpeechChunker(
        AudioSourceKind source,
        double maxChunkSeconds = 8.0,
        double minChunkSeconds = 0.35,
        double preRollMs = 300,
        double silenceFlushMs = 700,
        double overlapMs = 400,
        EnergyVad? vad = null,
        double targetChunkSeconds = 0.0,
        double maxCarriedSilenceMs = 0.0)
    {
        _source = source;
        _vad = vad ?? new EnergyVad();
        _preRollSamples = (int)(SpeechConstants.SampleRate * preRollMs / 1000.0);
        _preRoll = new float[Math.Max(1, _preRollSamples)];
        _maxSamples = (int)(SpeechConstants.SampleRate * maxChunkSeconds);
        _minSamples = (int)(SpeechConstants.SampleRate * minChunkSeconds);
        _silenceFlushFrames = Math.Max(1, (int)(silenceFlushMs / 1000.0 * SpeechConstants.SampleRate / _vad.FrameSamples));
        _overlapSamples = (int)(SpeechConstants.SampleRate * overlapMs / 1000.0);
        _targetSamples = Math.Min(_maxSamples, (int)(SpeechConstants.SampleRate * Math.Max(0.0, targetChunkSeconds)));
        _maxCarriedSilenceFrames = maxCarriedSilenceMs > 0
            ? Math.Max(_silenceFlushFrames, (int)(maxCarriedSilenceMs / 1000.0 * SpeechConstants.SampleRate / _vad.FrameSamples))
            : int.MaxValue;
    }

    /// <summary>Audio the detector classified as speech, in seconds.</summary>
    public double SpeechSeconds => _speechFrames * _vad.FrameSamples / (double)SpeechConstants.SampleRate;

    /// <summary>Audio actually handed out in chunks, in seconds. What a recognizer is asked to read.</summary>
    public double EmittedSeconds => _emittedSamples / (double)SpeechConstants.SampleRate;

    /// <summary>Chunks emitted so far.</summary>
    public long ChunkCount => _sequence;

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
                _speechFrames++;
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

            // A silence is a place a chunk *may* end, not a place it must. Whisper
            // pads whatever it is given out to its own 30 second window before the
            // encoder runs, so a two second chunk and a twenty-seven second chunk
            // cost the encoder the same; ending at every pause turns a ten minute
            // meeting into far more encoder passes than it has audio for. So the
            // chunk keeps filling across pauses until it holds _targetSamples, and
            // only then does the next silence close it - which is both the cheaper
            // and the more accurate choice, because whisper was trained on long
            // windows.
            var atSilence = _silenceFrames >= _silenceFlushFrames;
            var full = _pending.Count >= Math.Max(_minSamples, _targetSamples);
            var waitedLongEnough = _silenceFrames >= _maxCarriedSilenceFrames;

            // ...but a chunk must not sit there collecting silence forever. Once
            // the pause is clearly not a pause any more, emit what there is
            // rather than pad the recognizer's input with nothing.
            var flushForSilence = atSilence && _pending.Count >= _minSamples && (full || waitedLongEnough);
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

        TrimCarriedSilence();

        var samples = _pending.ToArray();
        _emittedSamples += samples.Length;
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

    /// <summary>
    /// Drops the silence a chunk sat through beyond what it needs to end
    /// cleanly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the tail, and only the excess: <c>silenceFlushMs</c> worth stays, on
    /// top of the detector's own hang-over, so a trailing particle or the decay
    /// of the last vowel is still inside the chunk. What goes is the extra
    /// seconds a chunk collected while waiting to see whether somebody was going
    /// to keep talking.
    /// </para>
    /// <para>
    /// Trimming the tail cannot move anything: every timestamp a recognizer
    /// returns is relative to the start of the chunk, and the start is where it
    /// was. Leaving the silence in would cost nothing in accuracy either - it
    /// would simply hand whisper seconds of nothing to think about, which is one
    /// of the conditions it is known to fill in with invented text.
    /// </para>
    /// </remarks>
    private void TrimCarriedSilence()
    {
        var excessFrames = _silenceFrames - _silenceFlushFrames;
        if (excessFrames <= 0)
        {
            return;
        }

        var excessSamples = Math.Min(excessFrames * _vad.FrameSamples, Math.Max(0, _pending.Count - _minSamples));
        if (excessSamples > 0)
        {
            _pending.RemoveRange(_pending.Count - excessSamples, excessSamples);
        }
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
        _speechFrames = 0;
        _emittedSamples = 0;
    }
}
