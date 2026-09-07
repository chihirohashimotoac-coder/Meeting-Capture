using System.Collections.Concurrent;
using System.Diagnostics;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Stt;

/// <summary>Snapshot of transcription throughput, shown in the UI and the diagnostics panel.</summary>
/// <param name="QueuedChunks">Chunks waiting for the recognizer.</param>
/// <param name="QueuedSeconds">Audio seconds waiting for the recognizer.</param>
/// <param name="LatencySeconds">How far the transcript trails the recording.</param>
/// <param name="RealTimeFactor">Inference seconds per audio second (EMA). &lt; 1 keeps up.</param>
/// <param name="SpilledChunks">Chunks parked on disk because the queue got large.</param>
/// <param name="ProcessedChunks">Chunks transcribed so far.</param>
/// <param name="FailedChunks">Chunks the engine could not process.</param>
public readonly record struct SttStatus(
    int QueuedChunks,
    double QueuedSeconds,
    double LatencySeconds,
    double RealTimeFactor,
    int SpilledChunks,
    long ProcessedChunks,
    long FailedChunks);

/// <summary>
/// Owns the recognition worker thread and the queue in front of it.
/// </summary>
/// <remarks>
/// <para><b>Audio is never dropped to catch up.</b> That is the hard rule the
/// requirements set, and it drives the whole design here:</para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Enqueue"/> never blocks. It is called from the audio pump thread,
/// which must return before the next WASAPI deadline.
/// </description></item>
/// <item><description>
/// When the queue grows past <see cref="MaxInMemorySeconds"/>, further chunks
/// are written to a spill file instead of being held in RAM. A 3 hour backlog
/// of 16 kHz mono float costs ~690 MB on disk but only the configured cap in
/// memory, so a slow machine degrades into "the transcript is late" rather than
/// "the app was killed by the memory manager".
/// </description></item>
/// <item><description>
/// The lateness is reported through <see cref="Status"/> so the UI can say
/// "文字起こし処理遅延: 18秒" instead of silently falling behind.
/// </description></item>
/// </list>
/// <para>
/// The worker runs at below-normal priority: transcription must never take CPU
/// away from capture or from writing the WAV.
/// </para>
/// </remarks>
public sealed class SttScheduler : IDisposable
{
    private readonly BlockingCollection<QueuedChunk> _queue = new(new ConcurrentQueue<QueuedChunk>());
    private readonly ILogger _logger;
    private readonly string _spillDirectory;
    private readonly object _metricsSync = new();

    private Thread? _worker;
    private CancellationTokenSource? _cts;
    private ISpeechRecognizer? _recognizer;
    private volatile bool _disposed;

    private int _queuedChunks;
    private long _queuedSamples;
    private int _inMemoryChunks;
    private long _inMemorySamples;
    private int _spilledChunks;
    private long _processedChunks;
    private long _failedChunks;
    private double _realTimeFactor;
    private long _lastCompletedEndMs;
    private long _recordingPositionMs;

    public SttScheduler(string spillDirectory, ILogger? logger = null)
    {
        _spillDirectory = spillDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Language code handed to the engine ("ja").</summary>
    public string Language { get; set; } = "ja";

    /// <summary>
    /// Optional speaker diarizer. It runs on the same chunk the recognizer just
    /// finished, on the transcription worker thread, so it can never interfere
    /// with capture. Set to null (or disable it) to fall back to the guaranteed
    /// microphone / system-audio split.
    /// </summary>
    public ISpeakerDiarizer? Diarizer { get; set; }

    /// <summary>
    /// Audio seconds kept in RAM before chunks start spilling to disk.
    /// 120 s of 16 kHz mono float is ~7.7 MB - generous, but bounded.
    /// </summary>
    public double MaxInMemorySeconds { get; set; } = 120.0;

    /// <summary>Raised on the worker thread for every recognized segment.</summary>
    public event Action<TranscriptSegment>? SegmentRecognized;

    /// <summary>Raised when the recognizer throws; recording continues regardless.</summary>
    public event Action<Exception>? RecognizerFailed;

    public bool IsRunning => _worker is { IsAlive: true };

    public void Start(ISpeechRecognizer recognizer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return;
        }

        Directory.CreateDirectory(_spillDirectory);
        _recognizer = recognizer;
        _cts = new CancellationTokenSource();
        _worker = new Thread(() => Run(_cts.Token))
        {
            IsBackground = true,
            Name = "MeetingRecorder.Stt",
            // Explicitly below the audio path. Requirement: STT falling behind
            // must never disturb capture or writing.
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
        _logger.Info(nameof(SttScheduler), $"Transcription worker started with model '{recognizer.ModelId}'.");
    }

    /// <summary>Reports how far the recording has progressed, so latency can be computed.</summary>
    public void ReportRecordingPosition(long positionMs) => Interlocked.Exchange(ref _recordingPositionMs, positionMs);

    /// <summary>Queues a chunk. Never blocks, never drops audio.</summary>
    public void Enqueue(SpeechChunk chunk)
    {
        if (_disposed || _queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            QueuedChunk queued;
            var maxSamples = (long)(MaxInMemorySeconds * SpeechConstants.SampleRate);

            if (Interlocked.Read(ref _inMemorySamples) > maxSamples)
            {
                queued = SpillToDisk(chunk);
                Interlocked.Increment(ref _spilledChunks);
            }
            else
            {
                queued = new QueuedChunk(chunk.StartMs, chunk.Source, chunk.SequenceNumber, chunk.Samples.Length, chunk.Samples, null);
                Interlocked.Add(ref _inMemorySamples, chunk.Samples.Length);
                Interlocked.Increment(ref _inMemoryChunks);
            }

            Interlocked.Increment(ref _queuedChunks);
            Interlocked.Add(ref _queuedSamples, queued.SampleCount);
            _queue.Add(queued);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(SttScheduler), "Failed to queue a chunk for transcription.", ex);
        }
    }

    public SttStatus Status
    {
        get
        {
            lock (_metricsSync)
            {
                var queuedSeconds = Interlocked.Read(ref _queuedSamples) / (double)SpeechConstants.SampleRate;
                var position = Interlocked.Read(ref _recordingPositionMs);
                var completed = Interlocked.Read(ref _lastCompletedEndMs);
                var latency = Math.Max(0.0, (position - completed) / 1000.0);

                // Before anything has been transcribed there is nothing to be
                // late about; report the queue depth instead of the whole
                // recording length.
                if (completed == 0)
                {
                    latency = queuedSeconds;
                }

                return new SttStatus(
                    Volatile.Read(ref _queuedChunks),
                    queuedSeconds,
                    latency,
                    _realTimeFactor,
                    Volatile.Read(ref _spilledChunks),
                    Interlocked.Read(ref _processedChunks),
                    Interlocked.Read(ref _failedChunks));
            }
        }
    }

    private void Run(CancellationToken token)
    {
        try
        {
            foreach (var queued in _queue.GetConsumingEnumerable(token))
            {
                ProcessOne(queued, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(SttScheduler), "Transcription worker stopped unexpectedly.", ex);
            RecognizerFailed?.Invoke(ex);
        }
    }

    private void ProcessOne(QueuedChunk queued, CancellationToken token)
    {
        float[] samples;
        try
        {
            samples = queued.Load();
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(SttScheduler), "Failed to read a spilled chunk; it is lost.", ex);
            Interlocked.Increment(ref _failedChunks);
            Release(queued);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var recognizer = _recognizer;
            if (recognizer is null || !recognizer.IsReady)
            {
                Interlocked.Increment(ref _failedChunks);
                return;
            }

            var spans = recognizer.Transcribe(samples, Language, token);
            stopwatch.Stop();

            var audioSeconds = samples.Length / (double)SpeechConstants.SampleRate;
            if (audioSeconds > 0)
            {
                var rtf = stopwatch.Elapsed.TotalSeconds / audioSeconds;
                lock (_metricsSync)
                {
                    // EMA over ~10 chunks: stable enough to drive the automatic
                    // degradation decisions without reacting to one slow chunk.
                    _realTimeFactor = _realTimeFactor <= 0 ? rtf : (_realTimeFactor * 0.8) + (rtf * 0.2);
                }
            }

            foreach (var span in spans)
            {
                var text = span.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                var segment = new TranscriptSegment
                {
                    StartMs = queued.StartMs + span.StartMs,
                    EndMs = queued.StartMs + span.EndMs,
                    Source = queued.Source,
                    Text = text,
                    Confidence = span.Confidence,
                    SpeakerId = IdentifySpeaker(queued.Source, samples, span),
                };

                SegmentRecognized?.Invoke(segment);
            }

            Interlocked.Increment(ref _processedChunks);
            var endMs = queued.StartMs + (long)(audioSeconds * 1000);
            InterlockedMax(ref _lastCompletedEndMs, endMs);

            _logger.Debug(
                nameof(SttScheduler),
                $"chunk seq={queued.Sequence} source={queued.Source} audio={audioSeconds:F2}s infer={stopwatch.Elapsed.TotalSeconds:F2}s spans={spans.Count}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedChunks);
            _logger.Error(nameof(SttScheduler), "Transcription of a chunk failed; recording is unaffected.", ex);
            RecognizerFailed?.Invoke(ex);
        }
        finally
        {
            Release(queued);
        }
    }

    /// <summary>
    /// Runs diarization over exactly the audio the recognized span covers.
    /// Any failure degrades to "no speaker", never to a wrong speaker and never
    /// to an interrupted transcript.
    /// </summary>
    private int? IdentifySpeaker(AudioSourceKind source, float[] chunk, RecognizedSpan span)
    {
        var diarizer = Diarizer;
        if (diarizer is null || !diarizer.IsEnabled)
        {
            return null;
        }

        try
        {
            var start = (int)Math.Clamp(span.StartMs * SpeechConstants.SampleRate / 1000, 0, chunk.Length);
            var end = (int)Math.Clamp(span.EndMs * SpeechConstants.SampleRate / 1000, start, chunk.Length);
            if (end - start < SpeechConstants.SampleRate / 4)
            {
                return null;
            }

            return diarizer.Identify(source, chunk.AsSpan(start, end - start));
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(SttScheduler), $"Diarization failed for one span; continuing without a speaker: {ex.Message}");
            return null;
        }
    }

    private void Release(QueuedChunk queued)
    {
        Interlocked.Decrement(ref _queuedChunks);
        Interlocked.Add(ref _queuedSamples, -queued.SampleCount);

        if (queued.SpillPath is null)
        {
            Interlocked.Add(ref _inMemorySamples, -queued.SampleCount);
            Interlocked.Decrement(ref _inMemoryChunks);
        }
        else
        {
            try
            {
                File.Delete(queued.SpillPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private QueuedChunk SpillToDisk(SpeechChunk chunk)
    {
        var path = Path.Combine(_spillDirectory, $"chunk-{chunk.Source}-{chunk.SequenceNumber:D8}.pcm");
        Directory.CreateDirectory(_spillDirectory);

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
        {
            var bytes = new byte[chunk.Samples.Length * sizeof(float)];
            Buffer.BlockCopy(chunk.Samples, 0, bytes, 0, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        return new QueuedChunk(chunk.StartMs, chunk.Source, chunk.SequenceNumber, chunk.Samples.Length, null, path);
    }

    /// <summary>
    /// Stops accepting work and waits for the queue to drain (or for the timeout).
    /// Called when recording stops, so the tail of the meeting is still transcribed.
    /// </summary>
    public bool DrainAndStop(TimeSpan timeout)
    {
        if (!IsRunning)
        {
            return true;
        }

        _queue.CompleteAdding();
        var finished = _worker!.Join(timeout);
        if (!finished)
        {
            _logger.Warn(nameof(SttScheduler), $"Transcription backlog did not drain within {timeout.TotalSeconds:F0}s; stopping anyway.");
            _cts?.Cancel();
            _worker.Join(TimeSpan.FromSeconds(5));
        }

        return finished;
    }

    public void CleanupSpillDirectory()
    {
        try
        {
            if (Directory.Exists(_spillDirectory))
            {
                Directory.Delete(_spillDirectory, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _logger.Warn(nameof(SttScheduler), $"Could not remove the spill directory: {ex.Message}");
        }
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }

        _cts?.Cancel();
        _worker?.Join(TimeSpan.FromSeconds(5));
        _cts?.Dispose();
        _queue.Dispose();
        _recognizer?.Dispose();
    }

    private sealed record QueuedChunk(
        long StartMs,
        AudioSourceKind Source,
        long Sequence,
        int SampleCount,
        float[]? Samples,
        string? SpillPath)
    {
        public float[] Load()
        {
            if (Samples is not null)
            {
                return Samples;
            }

            var bytes = File.ReadAllBytes(SpillPath!);
            var result = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }
    }
}
