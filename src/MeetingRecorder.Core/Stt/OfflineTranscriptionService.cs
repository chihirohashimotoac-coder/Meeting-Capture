using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Stt;

/// <param name="Path">A 16 kHz mono WAV holding one audio source as it was captured or imported.</param>
/// <param name="Source">
/// Which source it is. Stamped onto every segment produced from it. For a
/// recording made by this application that is ground truth - the file is either
/// the microphone capture or the loopback capture. For an imported file it is
/// <see cref="AudioSourceKind.Imported"/>, because a single mixed file carries
/// no evidence of who was speaking.
/// </param>
public readonly record struct TranscriptionSource(string Path, AudioSourceKind Source);

/// <param name="ProcessedSeconds">Audio walked so far, across every source.</param>
/// <param name="TotalSeconds">Audio to walk in total. 0 when it could not be determined.</param>
/// <param name="Segments">Segments produced so far.</param>
public readonly record struct TranscriptionProgress(double ProcessedSeconds, double TotalSeconds, int Segments)
{
    public double Fraction => TotalSeconds <= 0 ? 0 : Math.Clamp(ProcessedSeconds / TotalSeconds, 0, 1);
}

/// <summary>
/// The only speech recognition this application performs: a complete pass over
/// a finished audio file, started because the user asked for it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is waiting for this, and that single fact decides every choice in
/// here. The recorder no longer transcribes while it records - there is no
/// caption on screen to be late, and no second pass repairing a first one - so
/// the window handed to the engine is as long as whisper can use, it is cut at
/// a silence rather than at a stopwatch, the search is a beam search, and
/// whisper's temperature fallback is on. Those are the settings that cost
/// latency and buy accuracy, and latency is no longer a currency here.
/// </para>
/// <para>
/// It reads per-source audio rather than a mixed file. For a recording made by
/// this application that is what keeps <see cref="TranscriptSegment.Source"/> a
/// fact: a segment came out of the microphone file or the loopback file, and
/// mixing the two would throw that away permanently. For an imported file there
/// is only one source and it is labelled as such - no attempt is made to guess
/// who was speaking, because a single mixed recording does not contain that
/// information.
/// </para>
/// <para>
/// Speaker clustering, when a diarizer is supplied, runs here too - after the
/// recognizer, on the same window, on this thread. It is deliberately not part
/// of recording: nothing that can be done afterwards belongs on a thread whose
/// job is to not lose audio.
/// </para>
/// </remarks>
public sealed class OfflineTranscriptionService
{
    /// <summary>
    /// Hard ceiling for one window. Whisper's receptive field is 30 seconds;
    /// anything longer is silently truncated by the engine, so the cut is made
    /// here where it can be placed at a silence.
    /// </summary>
    public const double MaxWindowSeconds = 27.0;

    /// <summary>
    /// Silence shorter than this does not end a window, so a window keeps
    /// running across the pauses inside a sentence and only closes at a real
    /// break. Long windows are the whole reason this pass is more accurate than
    /// a few seconds at a time would be.
    /// </summary>
    public const double SilenceFlushMs = 800;

    /// <summary>
    /// Audio replayed from the end of a window that had to be cut for length
    /// rather than at a silence, so a word straddling the seam is still whole in
    /// one of the two windows.
    /// </summary>
    public const double OverlapMs = 200;

    /// <summary>Audio kept before a detected onset, so the first consonant is never clipped off.</summary>
    public const double PreRollMs = 300;

    private readonly ILogger _logger;

    public OfflineTranscriptionService(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Walks every source and returns the transcript, ordered by time.
    /// </summary>
    /// <param name="previous">
    /// A transcript already on screen, if any. Speaker names a human assigned
    /// are carried across to whichever new segment overlaps them most -
    /// transcribing again must not silently discard someone's manual work.
    /// </param>
    /// <param name="diarizer">
    /// Optional speaker clustering. Only meaningful for audio this application
    /// recorded; callers must not pass one for imported audio.
    /// </param>
    public IReadOnlyList<TranscriptSegment> Transcribe(
        IReadOnlyList<TranscriptionSource> sources,
        ISpeechRecognizer recognizer,
        string language,
        IReadOnlyList<TranscriptSegment>? previous = null,
        IProgress<TranscriptionProgress>? progress = null,
        ISpeakerDiarizer? diarizer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(recognizer);

        var usable = sources.Where(s => !string.IsNullOrWhiteSpace(s.Path) && File.Exists(s.Path)).ToList();
        if (usable.Count == 0)
        {
            throw new FileNotFoundException(
                "文字起こしに使う音声が見つかりません。"
                + "録音した会議では、設定の「文字起こし用の音声を保存する」が有効な録音でのみ実行できます。"
                + "インポートした音声では、会議フォルダー内の作業用音声が必要です。");
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
                _logger.Warn(nameof(OfflineTranscriptionService), $"Could not measure '{source.Path}': {ex.Message}");
            }
        }

        var segments = new List<TranscriptSegment>();
        var processedSeconds = 0.0;
        progress?.Report(new TranscriptionProgress(0, totalSeconds, 0));

        foreach (var source in usable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedSeconds = TranscribeOne(
                source, recognizer, language, segments, processedSeconds, totalSeconds, progress, diarizer, cancellationToken);
        }

        segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        CarryOverSpeakerNames(segments, previous);

        _logger.Info(
            nameof(OfflineTranscriptionService),
            $"Transcribed {totalSeconds:F0}s of audio from {usable.Count} source(s) into {segments.Count} segment(s).");

        progress?.Report(new TranscriptionProgress(totalSeconds, totalSeconds, segments.Count));
        return segments;
    }

    private double TranscribeOne(
        TranscriptionSource source,
        ISpeechRecognizer recognizer,
        string language,
        List<TranscriptSegment> segments,
        double processedSeconds,
        double totalSeconds,
        IProgress<TranscriptionProgress>? progress,
        ISpeakerDiarizer? diarizer,
        CancellationToken cancellationToken)
    {
        using var reader = new WavFileReader(source.Path);

        if (reader.SampleRate != SpeechConstants.SampleRate)
        {
            // Both the recorder and the importer write the engine's own rate.
            // Anything else means the file did not come from this application,
            // and resampling it silently would hide that.
            throw new InvalidDataException(
                $"'{Path.GetFileName(source.Path)}' は {reader.SampleRate} Hz です。"
                + $"文字起こしに使えるのは {SpeechConstants.SampleRate} Hz の音声だけです。");
        }

        // The chunker knows how to cut a stream at a silence and never in the
        // middle of a word. This pass simply asks it for the longest windows
        // whisper can actually use.
        var chunker = new SpeechChunker(
            source.Source,
            maxChunkSeconds: MaxWindowSeconds,
            minChunkSeconds: 1.0,
            preRollMs: PreRollMs,
            silenceFlushMs: SilenceFlushMs,
            overlapMs: OverlapMs,
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
                Recognize(chunk, recognizer, language, segments, diarizer, cancellationToken);
            }

            processedSeconds += read / (double)SpeechConstants.SampleRate;

            // A window can take seconds to decode; reporting every block would
            // just spam the UI thread.
            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(400))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new TranscriptionProgress(processedSeconds, totalSeconds, segments.Count));
            }
        }

        var tail = chunker.Flush();
        if (tail is not null)
        {
            Recognize(tail, recognizer, language, segments, diarizer, cancellationToken);
        }

        return processedSeconds;
    }

    private void Recognize(
        SpeechChunk chunk,
        ISpeechRecognizer recognizer,
        string language,
        List<TranscriptSegment> segments,
        ISpeakerDiarizer? diarizer,
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
            // One bad window must not cost the rest of the meeting.
            _logger.Error(nameof(OfflineTranscriptionService), $"Transcribing a window at {chunk.StartMs} ms failed.", ex);
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
                SpeakerId = IdentifySpeaker(diarizer, chunk, span),
            });
        }
    }

    /// <summary>
    /// Runs speaker clustering over exactly the audio the recognized span
    /// covers. Any failure degrades to "no speaker", never to a wrong speaker
    /// and never to an interrupted transcript.
    /// </summary>
    private int? IdentifySpeaker(ISpeakerDiarizer? diarizer, SpeechChunk chunk, RecognizedSpan span)
    {
        if (diarizer is null || !diarizer.IsEnabled)
        {
            return null;
        }

        try
        {
            var samples = chunk.Samples;
            var start = (int)Math.Clamp(span.StartMs * SpeechConstants.SampleRate / 1000, 0, samples.Length);
            var end = (int)Math.Clamp(span.EndMs * SpeechConstants.SampleRate / 1000, start, samples.Length);
            if (end - start < SpeechConstants.SampleRate / 4)
            {
                return null;
            }

            return diarizer.Identify(chunk.Source, samples.AsSpan(start, end - start));
        }
        catch (Exception ex)
        {
            _logger.Warn(
                nameof(OfflineTranscriptionService),
                $"Speaker clustering failed for one span; continuing without a speaker: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Copies human-assigned speaker names onto the new segments they overlap.
    /// Only names a person typed are carried; a cluster id from an earlier run
    /// means nothing to a new segmentation.
    /// </summary>
    private static void CarryOverSpeakerNames(
        List<TranscriptSegment> current,
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

        foreach (var segment in current)
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
