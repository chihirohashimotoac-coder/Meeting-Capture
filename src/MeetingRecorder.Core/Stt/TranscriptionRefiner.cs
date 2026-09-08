using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Stt;

/// <param name="Path">A 16 kHz mono WAV holding one capture stream as it was captured.</param>
/// <param name="Source">Which stream it is. Carried into every segment produced from it.</param>
public readonly record struct RefinementSource(string Path, AudioSourceKind Source);

/// <param name="ProcessedSeconds">Audio walked so far, across every source.</param>
/// <param name="TotalSeconds">Audio to walk in total. 0 when it could not be determined.</param>
/// <param name="Segments">Segments produced so far.</param>
public readonly record struct RefinementProgress(double ProcessedSeconds, double TotalSeconds, int Segments)
{
    public double Fraction => TotalSeconds <= 0 ? 0 : Math.Clamp(ProcessedSeconds / TotalSeconds, 0, 1);
}

/// <summary>
/// The second transcription pass: re-transcribes a finished recording with an
/// accurate recognizer and replaces the live transcript.
/// </summary>
/// <remarks>
/// <para>
/// The live pass is judged on latency - a caption that arrives after the topic
/// has moved on is useless - so it decodes greedily, in short chunks, with the
/// lightest model that keeps up. Those are exactly the choices that cost
/// accuracy on conversational Japanese, where a sentence's meaning often is not
/// settled until its final particle.
/// </para>
/// <para>
/// This pass makes the opposite choices, because nothing is waiting for it: a
/// larger model, a beam search, temperature fallback, and windows close to the
/// 30 seconds whisper was trained on, cut at silence so no window ends
/// mid-word.
/// </para>
/// <para>
/// It reads the per-stream recognition audio rather than the mixed recording.
/// That is what keeps <see cref="TranscriptSegment.Source"/> ground truth
/// instead of a guess: a segment comes from the microphone file or the loopback
/// file, and mixing the two would throw that information away permanently.
/// </para>
/// </remarks>
public sealed class TranscriptionRefiner
{
    /// <summary>
    /// Hard ceiling for one window. Whisper's receptive field is 30 seconds;
    /// anything longer is silently truncated by the engine, so the cut is made
    /// here where it can be placed at a silence.
    /// </summary>
    private const double MaxWindowSeconds = 27.0;

    /// <summary>
    /// A window is closed at the first silence after this much speech. Below
    /// roughly 15 seconds the model loses the sentence-level context that makes
    /// this pass worth running at all.
    /// </summary>
    private const double PreferredWindowSeconds = 18.0;

    private readonly ILogger _logger;

    public TranscriptionRefiner(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Walks every source and returns the refined transcript, ordered by time.
    /// </summary>
    /// <param name="previous">
    /// The live transcript. Speaker names a human assigned are carried across to
    /// whichever refined segment overlaps them most - re-transcribing must not
    /// silently discard someone's manual work.
    /// </param>
    public IReadOnlyList<TranscriptSegment> Refine(
        IReadOnlyList<RefinementSource> sources,
        ISpeechRecognizer recognizer,
        string language,
        IReadOnlyList<TranscriptSegment>? previous = null,
        IProgress<RefinementProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(recognizer);

        var usable = sources.Where(s => !string.IsNullOrWhiteSpace(s.Path) && File.Exists(s.Path)).ToList();
        if (usable.Count == 0)
        {
            throw new FileNotFoundException(
                "再文字起こしに使う音声が見つかりません。録音時に「停止後に高精度で文字起こしをやり直す」が"
                + "有効になっていた録音でのみ実行できます。");
        }

        var totalSeconds = 0.0;
        foreach (var source in usable)
        {
            try
            {
                using var probe = new WavFileReader(source.Path);
                totalSeconds += probe.DurationSeconds;
            }
            catch (Exception ex)
            {
                _logger.Warn(nameof(TranscriptionRefiner), $"Could not measure '{source.Path}': {ex.Message}");
            }
        }

        var segments = new List<TranscriptSegment>();
        var processedSeconds = 0.0;
        progress?.Report(new RefinementProgress(0, totalSeconds, 0));

        foreach (var source in usable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedSeconds = RefineOne(
                source, recognizer, language, segments, processedSeconds, totalSeconds, progress, cancellationToken);
        }

        segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        CarryOverSpeakerNames(segments, previous);

        _logger.Info(
            nameof(TranscriptionRefiner),
            $"Refined {totalSeconds:F0}s of audio from {usable.Count} stream(s) into {segments.Count} segment(s).");

        progress?.Report(new RefinementProgress(totalSeconds, totalSeconds, segments.Count));
        return segments;
    }

    private double RefineOne(
        RefinementSource source,
        ISpeechRecognizer recognizer,
        string language,
        List<TranscriptSegment> segments,
        double processedSeconds,
        double totalSeconds,
        IProgress<RefinementProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var reader = new WavFileReader(source.Path);

        if (reader.SampleRate != SpeechConstants.SampleRate)
        {
            // The recognition audio is written at the engine's own rate. Anything
            // else means the file did not come from this recorder, and resampling
            // it silently would hide that.
            throw new InvalidDataException(
                $"'{Path.GetFileName(source.Path)}' は {reader.SampleRate} Hz です。"
                + $"再文字起こしに使えるのは {SpeechConstants.SampleRate} Hz の音声だけです。");
        }

        // The chunker already knows how to cut a stream at silence and never in
        // the middle of a word; this pass only asks it for much longer windows.
        var chunker = new SpeechChunker(
            source.Source,
            maxChunkSeconds: MaxWindowSeconds,
            minChunkSeconds: 1.0,
            preRollMs: 300,
            silenceFlushMs: 800,
            overlapMs: 200,
            vad: null);

        var buffer = new float[SpeechConstants.SampleRate];
        var lastReport = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = reader.ReadMono(buffer);
            if (read == 0)
            {
                break;
            }

            foreach (var chunk in chunker.Append(buffer.AsSpan(0, read)))
            {
                Recognize(chunk, recognizer, language, segments, cancellationToken);
            }

            processedSeconds += read / (double)SpeechConstants.SampleRate;

            // A window can take seconds to decode; reporting every block would
            // just spam the UI thread.
            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(400))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new RefinementProgress(processedSeconds, totalSeconds, segments.Count));
            }
        }

        var tail = chunker.Flush();
        if (tail is not null)
        {
            Recognize(tail, recognizer, language, segments, cancellationToken);
        }

        return processedSeconds;
    }

    private void Recognize(
        SpeechChunk chunk,
        ISpeechRecognizer recognizer,
        string language,
        List<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RecognizedSpan> spans;
        try
        {
            spans = recognizer.Transcribe(chunk.Samples, language, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad window must not cost the rest of the meeting. The live
            // transcript is still on screen until this pass finishes, so the user
            // is never left with less than they had.
            _logger.Error(nameof(TranscriptionRefiner), $"Refining a window at {chunk.StartMs} ms failed.", ex);
            return;
        }

        foreach (var span in spans)
        {
            var text = span.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            segments.Add(new TranscriptSegment
            {
                StartMs = chunk.StartMs + span.StartMs,
                EndMs = chunk.StartMs + span.EndMs,
                Source = chunk.Source,
                Text = text,
                Confidence = span.Confidence,
            });
        }
    }

    /// <summary>
    /// Copies human-assigned speaker names onto the refined segments they
    /// overlap. Only names a person typed are carried; a diarization cluster id
    /// from the old run means nothing to the new segmentation.
    /// </summary>
    private static void CarryOverSpeakerNames(
        List<TranscriptSegment> refined,
        IReadOnlyList<TranscriptSegment>? previous)
    {
        if (previous is null || previous.Count == 0)
        {
            return;
        }

        var named = previous
            .Where(p => !string.IsNullOrWhiteSpace(p.SpeakerName))
            .OrderBy(p => p.StartMs)
            .ToList();

        if (named.Count == 0)
        {
            return;
        }

        foreach (var segment in refined)
        {
            TranscriptSegment? best = null;
            long bestOverlap = 0;

            foreach (var candidate in named)
            {
                if (candidate.Source != segment.Source || candidate.StartMs > segment.EndMs)
                {
                    continue;
                }

                var overlap = Math.Min(candidate.EndMs, segment.EndMs) - Math.Max(candidate.StartMs, segment.StartMs);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = candidate;
                }
            }

            if (best is not null)
            {
                segment.SpeakerName = best.SpeakerName;
                segment.SpeakerId = best.SpeakerId;
            }
        }
    }
}
