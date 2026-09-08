using System.Diagnostics;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Stt;

namespace MeetingRecorder.Core.Pipeline;

/// <summary>Construction parameters for <see cref="RecordingPipeline"/>.</summary>
public sealed class RecordingPipelineOptions
{
    /// <summary>Internal processing rate. 48 kHz is what WASAPI shared mode runs at on Windows 11.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>How often the mixer pump runs. 20 ms keeps meters smooth without busy-waiting.</summary>
    public TimeSpan PumpInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Capacity of each capture ring buffer.</summary>
    public double RingBufferSeconds { get; init; } = 30.0;

    /// <summary>How often the WAV header and the journal are flushed to disk.</summary>
    public TimeSpan AutoSaveInterval { get; init; } = TimeSpan.FromSeconds(15);

    public AudioProcessingSettings Processing { get; init; } = new();

    public PerformanceProfile Profile { get; init; } = new();

    /// <summary>When false the STT legs are not started at all (recording only).</summary>
    public bool TranscriptionEnabled { get; init; } = true;
}

/// <summary>
/// The recording engine: two capture streams in, one normalized mixed file plus
/// a live transcript out.
/// </summary>
/// <remarks>
/// <para><b>Thread layout</b></para>
/// <list type="bullet">
/// <item><description>
/// <b>Capture callbacks</b> (owned by WASAPI) do the minimum possible work:
/// convert to mono float, update a meter, copy into a ring buffer. They never
/// allocate a large buffer, never touch the disk and never wait on a lock held
/// by anything slow.
/// </description></item>
/// <item><description>
/// <b>The pump thread</b> (here, above-normal priority) is the only writer of
/// the audio file. It consumes exactly as much audio per tick as the wall clock
/// says has elapsed, pads a starved stream with silence, applies the per-stream
/// DSP chains, mixes, writes, and hands 16 kHz audio to the chunkers.
/// </description></item>
/// <item><description>
/// <b>The transcription worker</b> (inside <see cref="SttScheduler"/>) runs at
/// below-normal priority and is completely decoupled by a queue. If it stalls,
/// stops, or throws, the pump keeps writing audio - which is the single most
/// important property of this whole application.
/// </description></item>
/// </list>
/// <para>
/// Failure policy: losing a stream degrades the recording, it never ends it. If
/// the microphone disappears mid-meeting the loopback side keeps recording (and
/// vice versa), with silence written for the missing leg and a warning raised to
/// the UI and to metadata.
/// </para>
/// </remarks>
public sealed class RecordingPipeline : IDisposable
{
    private readonly RecordingPipelineOptions _options;
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly ISleepPreventer _sleepPreventer;
    private readonly ILogger _logger;
    private readonly Func<string, int, IAudioFileWriter> _writerFactory;

    private readonly object _stateSync = new();
    private readonly LevelMeter _micMeter = new();
    private readonly LevelMeter _systemMeter = new();

    private AudioRingBuffer? _micRing;
    private AudioRingBuffer? _systemRing;
    private AudioProcessingChain? _micChain;
    private AudioProcessingChain? _systemChain;
    private DriftCompensator? _micDrift;
    private DriftCompensator? _systemDrift;
    private StreamMixer? _mixer;
    private Resampler? _micToStt;
    private Resampler? _systemToStt;
    private SpeechChunker? _micChunker;
    private SpeechChunker? _systemChunker;

    private IAudioCaptureSource? _micSource;
    private IAudioCaptureSource? _systemSource;
    private IAudioFileWriter? _writer;
    private SttScheduler? _scheduler;
    private RecoveryJournal? _journal;

    private Thread? _pump;
    private CancellationTokenSource? _cts;
    private Stopwatch? _clock;
    private long _producedSamples;
    private double _micSilenceSeconds;
    private double _systemSilenceSeconds;
    private DateTime _lastFlushUtc;
    private volatile RecordingState _state = RecordingState.Idle;
    private bool _disposed;

    public RecordingPipeline(
        RecordingPipelineOptions options,
        IAudioCaptureFactory captureFactory,
        TranscriptStore transcript,
        ISleepPreventer? sleepPreventer = null,
        ILogger? logger = null,
        Func<string, int, IAudioFileWriter>? writerFactory = null)
    {
        _options = options;
        _captureFactory = captureFactory;
        Transcript = transcript;
        _sleepPreventer = sleepPreventer ?? new NullSleepPreventer();
        _logger = logger ?? NullLogger.Instance;
        _writerFactory = writerFactory ?? ((path, rate) => new WavFileWriter(path, rate));
    }

    public TranscriptStore Transcript { get; }

    public RecordingState State => _state;

    public string? AudioFilePath => _writer?.FilePath;

    /// <summary>Non-fatal problems encountered during the recording, copied into metadata.json.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_stateSync)
            {
                return _warnings.ToList();
            }
        }
    }

    private readonly List<string> _warnings = new();

    /// <summary>Raised when a device disappears or a stream degrades. Always actionable text.</summary>
    public event EventHandler<CaptureFault>? Fault;

    /// <summary>Raised once per pump flush with the current status.</summary>
    public event Action<RecordingStatus>? StatusChanged;

    /// <summary>
    /// Starts capture. <paramref name="recognizer"/> may be null - the meeting is
    /// then recorded without a transcript rather than not recorded at all.
    /// </summary>
    public void Start(
        string audioFilePath,
        AppSettings settings,
        ISpeechRecognizer? recognizer,
        RecoveryJournal? journal,
        string spillDirectory,
        ISpeakerDiarizer? diarizer = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateSync)
        {
            if (_state is RecordingState.Recording or RecordingState.Preparing)
            {
                throw new InvalidOperationException("Recording is already in progress.");
            }

            _state = RecordingState.Preparing;
            _warnings.Clear();
        }

        var rate = _options.SampleRate;
        _journal = journal;
        Transcript.AttachJournal(journal);

        _micRing = AudioRingBuffer.ForSeconds(rate, _options.RingBufferSeconds);
        _systemRing = AudioRingBuffer.ForSeconds(rate, _options.RingBufferSeconds);
        _micChain = new AudioProcessingChain(_options.Processing, rate);
        _systemChain = new AudioProcessingChain(_options.Processing, rate);
        _micDrift = new DriftCompensator(rate);
        _systemDrift = new DriftCompensator(rate);
        _mixer = new StreamMixer(_options.Processing, rate);
        _micToStt = new Resampler(rate, SpeechConstants.SampleRate);
        _systemToStt = new Resampler(rate, SpeechConstants.SampleRate);
        _micChunker = new SpeechChunker(AudioSourceKind.Microphone, _options.Profile.ChunkSeconds, overlapMs: _options.Profile.ChunkOverlapMs);
        _systemChunker = new SpeechChunker(AudioSourceKind.SystemAudio, _options.Profile.ChunkSeconds, overlapMs: _options.Profile.ChunkOverlapMs);

        _micMeter.Reset();
        _systemMeter.Reset();
        _producedSamples = 0;
        _micSilenceSeconds = 0;
        _systemSilenceSeconds = 0;

        _writer = _writerFactory(audioFilePath, rate);

        // Capture is started before the writer's first sample so nothing is lost
        // while the devices spin up; anything that arrives early simply queues.
        _micSource = TryCreateSource(
            () => _captureFactory.CreateMicrophoneCapture(settings.MicrophoneDeviceId, rate),
            AudioSourceKind.Microphone);
        _systemSource = TryCreateSource(
            () => _captureFactory.CreateSystemAudioCapture(settings.RenderDeviceId, rate),
            AudioSourceKind.SystemAudio);

        if (_micSource is null && _systemSource is null)
        {
            _writer.Dispose();
            _writer = null;
            _state = RecordingState.Faulted;
            throw new InvalidOperationException("マイクとPC内部音声のどちらも取得できませんでした。録音を開始できません。");
        }

        if (_options.TranscriptionEnabled && recognizer is not null)
        {
            _scheduler = new SttScheduler(spillDirectory, _logger)
            {
                Language = settings.SttLanguage,
                // Diarization runs on the transcription worker, behind the same
                // queue, so it can never slow capture down. It is also the first
                // thing the degradation ladder switches off.
                Diarizer = _options.Profile.DiarizationEnabled ? diarizer : null,
            };
            _scheduler.SegmentRecognized += OnSegmentRecognized;
            _scheduler.RecognizerFailed += OnRecognizerFailed;
            _scheduler.Start(recognizer);
        }
        else if (_options.TranscriptionEnabled)
        {
            AddWarning("音声認識モデルが利用できないため、録音のみ実行しています。");
        }

        if (settings.PreventSleepWhileRecording && !_sleepPreventer.Prevent("会議を録音中"))
        {
            AddWarning("スリープ抑制を有効にできませんでした。録音中にPCがスリープする可能性があります。");
        }

        _cts = new CancellationTokenSource();
        _clock = Stopwatch.StartNew();
        _lastFlushUtc = DateTime.UtcNow;

        _pump = new Thread(() => PumpLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "MeetingRecorder.Pump",
            // Above normal, but not real-time: the pump owns the recording, and
            // must win against the UI and the recognizer.
            Priority = ThreadPriority.AboveNormal,
        };

        _state = RecordingState.Recording;
        _pump.Start();

        // Opening a stream can fail even though the endpoint resolved a moment
        // ago. The common case is Windows privacy settings: the microphone
        // enumerates normally and then denies access with E_ACCESSDENIED here,
        // when the audio client is initialised. One dead stream must never cost
        // the user the other, so a failure at this point drops that leg and the
        // recording carries on with whatever still works.
        if (!TryStartSource(_micSource, AudioSourceKind.Microphone))
        {
            _micSource = null;
        }

        if (!TryStartSource(_systemSource, AudioSourceKind.SystemAudio))
        {
            _systemSource = null;
        }

        if (_micSource is null && _systemSource is null)
        {
            AbortStart();
            throw new InvalidOperationException(
                "マイクとPC内部音声のどちらも録音を開始できませんでした。"
                + "Windowsの「設定 > プライバシーとセキュリティ > マイク」でデスクトップアプリのマイク使用が"
                + "許可されているか、再生デバイスが有効かを確認してください。");
        }

        _logger.Info(nameof(RecordingPipeline), $"Recording started -> {audioFilePath}");
    }

    /// <summary>
    /// Starts one capture leg, converting a failure into a warning rather than
    /// into a lost recording.
    /// </summary>
    private bool TryStartSource(IAudioCaptureSource? source, AudioSourceKind kind)
    {
        if (source is null)
        {
            return false;
        }

        try
        {
            source.Start();
            return true;
        }
        catch (Exception ex)
        {
            var label = kind == AudioSourceKind.Microphone ? "マイク" : "PC内部音声";
            _logger.Error(nameof(RecordingPipeline), $"{kind} capture could not be started.", ex);

            // An access denial has a specific remedy, and saying so is the
            // difference between a user fixing it and giving up.
            var remedy = ex is UnauthorizedAccessException && kind == AudioSourceKind.Microphone
                ? "「設定 > プライバシーとセキュリティ > マイク」でデスクトップアプリのマイク使用を許可してください。"
                : "もう一方の音声のみ録音します。";

            AddWarning($"{label}の録音を開始できませんでした（{ex.Message}）。{remedy}");
            Fault?.Invoke(this, new CaptureFault(
                CaptureFaultKind.Unknown, $"{label}の録音を開始できませんでした: {ex.Message}", false));

            try
            {
                source.Dispose();
            }
            catch (Exception disposeError)
            {
                _logger.Warn(nameof(RecordingPipeline), $"Disposing the failed {kind} source reported: {disposeError.Message}");
            }

            return false;
        }
    }

    /// <summary>Unwinds a start that could not produce a single working stream.</summary>
    private void AbortStart()
    {
        _cts?.Cancel();
        _pump?.Join(TimeSpan.FromSeconds(2));

        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(RecordingPipeline), $"Discarding the audio file after a failed start reported: {ex.Message}");
        }

        _writer = null;
        _scheduler?.DrainAndStop(TimeSpan.FromSeconds(1));
        _sleepPreventer.Restore();
        _state = RecordingState.Faulted;
    }

    private IAudioCaptureSource? TryCreateSource(Func<IAudioCaptureSource> factory, AudioSourceKind kind)
    {
        try
        {
            var source = factory();
            if (kind == AudioSourceKind.Microphone)
            {
                source.DataAvailable += OnMicData;
            }
            else
            {
                source.DataAvailable += OnSystemData;
            }

            source.Fault += OnCaptureFault;
            return source;
        }
        catch (Exception ex)
        {
            var label = kind == AudioSourceKind.Microphone ? "マイク" : "PC内部音声";
            _logger.Error(nameof(RecordingPipeline), $"{kind} capture could not be created.", ex);
            AddWarning($"{label}を取得できませんでした（{ex.Message}）。もう一方の音声のみ録音します。");
            Fault?.Invoke(this, new CaptureFault(CaptureFaultKind.Unknown, $"{label}を取得できませんでした: {ex.Message}", false));
            return null;
        }
    }

    private void OnCaptureFault(object? sender, CaptureFault fault)
    {
        AddWarning(fault.Message);
        _journal?.Note(fault.Message);
        _logger.Warn(nameof(RecordingPipeline), $"Capture fault: {fault.Kind} recovered={fault.Recovered} - {fault.Message}");
        Fault?.Invoke(this, fault);
    }

    private void OnMicData(ReadOnlySpan<float> samples)
    {
        _micMeter.Update(samples);
        _micRing?.Write(samples);
    }

    private void OnSystemData(ReadOnlySpan<float> samples)
    {
        _systemMeter.Update(samples);
        _systemRing?.Write(samples);
    }

    private void OnSegmentRecognized(TranscriptSegment segment) => Transcript.Add(segment);

    private void OnRecognizerFailed(Exception exception)
    {
        AddWarning("文字起こしでエラーが発生しました。録音は継続しています。");
        _logger.Error(nameof(RecordingPipeline), "Recognizer reported a failure; recording continues.", exception);
    }

    private void PumpLoop(CancellationToken token)
    {
        var rate = _options.SampleRate;
        var tick = _options.PumpInterval;
        var maxBlock = rate; // never process more than one second in a single pass

        var micBuffer = new float[maxBlock];
        var systemBuffer = new float[maxBlock];
        var mixBuffer = new float[maxBlock];
        var sttBuffer = new float[(maxBlock / (rate / SpeechConstants.SampleRate)) + 16];

        try
        {
            while (!token.IsCancellationRequested)
            {
                var elapsed = _clock!.Elapsed;
                var target = (long)(elapsed.TotalSeconds * rate);
                var need = (int)Math.Min(target - _producedSamples, maxBlock);

                if (need <= 0)
                {
                    Thread.Sleep(tick);
                    continue;
                }

                ProcessBlock(micBuffer.AsSpan(0, need), systemBuffer.AsSpan(0, need), mixBuffer.AsSpan(0, need), sttBuffer, tick.TotalSeconds);
                _producedSamples += need;

                if (DateTime.UtcNow - _lastFlushUtc >= _options.AutoSaveInterval)
                {
                    FlushToDisk();
                }

                if (target - _producedSamples < rate / 100)
                {
                    Thread.Sleep(tick);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _state = RecordingState.Faulted;
            _logger.Error(nameof(RecordingPipeline), "Recording pump failed.", ex);
            AddWarning($"録音処理でエラーが発生しました: {ex.Message}");
            Fault?.Invoke(this, new CaptureFault(CaptureFaultKind.Unknown, $"録音処理でエラーが発生しました: {ex.Message}", false));
        }
    }

    private void ProcessBlock(Span<float> mic, Span<float> system, Span<float> mix, float[] sttScratch, double tickSeconds)
    {
        var rate = _options.SampleRate;

        FillStream(_micRing, mic, _micDrift!, tickSeconds, ref _micSilenceSeconds, rate);
        FillStream(_systemRing, system, _systemDrift!, tickSeconds, ref _systemSilenceSeconds, rate);

        _micChain!.Process(mic);
        _systemChain!.Process(system);

        _mixer!.Mix(mic, system, mix);
        _writer!.Write(mix);

        if (_scheduler is not null)
        {
            FeedTranscription(_micChunker!, _micToStt!, mic, sttScratch);
            FeedTranscription(_systemChunker!, _systemToStt!, system, sttScratch);
            _scheduler.ReportRecordingPosition((long)(_writer.DurationSeconds * 1000));
        }
    }

    private void FillStream(
        AudioRingBuffer? ring,
        Span<float> destination,
        DriftCompensator drift,
        double tickSeconds,
        ref double silenceSeconds,
        int sampleRate)
    {
        destination.Clear();
        if (ring is null)
        {
            silenceSeconds += destination.Length / (double)sampleRate;
            return;
        }

        var read = ring.Read(destination);
        if (read < destination.Length)
        {
            // The stream is behind (or the endpoint is genuinely silent - WASAPI
            // loopback delivers nothing at all while nothing is playing). Silence
            // keeps the timeline honest either way.
            var inserted = destination.Length - read;
            drift.ReportSilenceInserted(inserted);
            silenceSeconds += inserted / (double)sampleRate;
        }
        else
        {
            silenceSeconds = 0;
        }

        var correction = drift.Evaluate(ring.Count, tickSeconds);
        if (correction > 0)
        {
            var dropped = ring.Discard(correction);
            if (dropped > 0)
            {
                _logger.Debug(
                    nameof(RecordingPipeline),
                    $"Clock drift correction: dropped {dropped} samples ({dropped * 1000.0 / sampleRate:F1} ms).");
            }
        }
    }

    private void FeedTranscription(SpeechChunker chunker, Resampler resampler, ReadOnlySpan<float> source, float[] scratch)
    {
        var needed = resampler.EstimateOutputLength(source.Length);
        if (scratch.Length < needed)
        {
            scratch = new float[needed];
        }

        var written = resampler.Process(source, scratch);
        var chunks = chunker.Append(scratch.AsSpan(0, written));
        foreach (var chunk in chunks)
        {
            _scheduler!.Enqueue(chunk);
        }
    }

    private void FlushToDisk()
    {
        try
        {
            _writer?.Flush();
            _journal?.ReportProgress(_writer?.DurationSeconds ?? 0);
            _journal?.Flush();
            _lastFlushUtc = DateTime.UtcNow;
            StatusChanged?.Invoke(GetStatus());
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(RecordingPipeline), "Periodic flush failed.", ex);
            AddWarning($"録音データの定期保存に失敗しました: {ex.Message}");
        }
    }

    public RecordingStatus GetStatus()
    {
        var elapsed = _writer is null
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(_writer.DurationSeconds);

        var overruns = (_micRing?.OverrunCount ?? 0) + (_systemRing?.OverrunCount ?? 0);
        var inserted = ((_micDrift?.InsertedSilenceSamples ?? 0) + (_systemDrift?.InsertedSilenceSamples ?? 0))
                       * 1000.0 / _options.SampleRate;
        var corrected = ((_micDrift?.CorrectedSamples ?? 0) + (_systemDrift?.CorrectedSamples ?? 0))
                        * 1000.0 / _options.SampleRate;

        return new RecordingStatus(
            _state,
            elapsed,
            _micMeter.Read(),
            _systemMeter.Read(),
            _micChain?.CurrentGainDb ?? 0,
            _systemChain?.CurrentGainDb ?? 0,
            _scheduler?.Status ?? default,
            _micSilenceSeconds,
            _systemSilenceSeconds,
            overruns,
            inserted,
            corrected,
            _writer?.SamplesWritten * 2 ?? 0);
    }

    /// <summary>
    /// Stops capture, drains the transcription queue and finalises the file.
    /// </summary>
    /// <param name="transcriptionDrainTimeout">
    /// How long to keep transcribing the backlog after the audio stops. The audio
    /// file is already complete at this point; this only affects how much of the
    /// tail makes it into the transcript.
    /// </param>
    public RecordingResult Stop(TimeSpan? transcriptionDrainTimeout = null)
    {
        lock (_stateSync)
        {
            if (_state is RecordingState.Idle or RecordingState.Stopping)
            {
                return new RecordingResult(_writer?.FilePath ?? string.Empty, TimeSpan.Zero, Warnings, false);
            }

            _state = RecordingState.Stopping;
        }

        try
        {
            _micSource?.Stop();
            _systemSource?.Stop();
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(RecordingPipeline), $"Stopping capture reported: {ex.Message}");
        }

        // Give the pump a moment to drain whatever is still in the rings, so the
        // last word spoken before Stop is on disk.
        Thread.Sleep((int)Math.Min(500, _options.PumpInterval.TotalMilliseconds * 10));
        _cts?.Cancel();
        _pump?.Join(TimeSpan.FromSeconds(5));

        var micTail = _micChunker?.Flush();
        if (micTail is not null)
        {
            _scheduler?.Enqueue(micTail);
        }

        var systemTail = _systemChunker?.Flush();
        if (systemTail is not null)
        {
            _scheduler?.Enqueue(systemTail);
        }

        var drained = true;
        if (_scheduler is not null)
        {
            drained = _scheduler.DrainAndStop(transcriptionDrainTimeout ?? TimeSpan.FromMinutes(10));
            _scheduler.CleanupSpillDirectory();
        }

        var duration = TimeSpan.FromSeconds(_writer?.DurationSeconds ?? 0);
        var path = _writer?.FilePath ?? string.Empty;

        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(RecordingPipeline), "Finalising the audio file failed.", ex);
        }

        _writer = null;
        _sleepPreventer.Restore();
        _state = RecordingState.Idle;

        if (!drained)
        {
            AddWarning("文字起こしのバックログが残ったまま停止しました。末尾の一部が未処理の可能性があります。");
        }

        _logger.Info(nameof(RecordingPipeline), $"Recording stopped after {duration:hh\\:mm\\:ss} -> {path}");
        return new RecordingResult(path, duration, Warnings, drained);
    }

    private void AddWarning(string message)
    {
        lock (_stateSync)
        {
            if (!_warnings.Contains(message))
            {
                _warnings.Add(message);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_state is RecordingState.Recording or RecordingState.Preparing)
            {
                Stop(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(RecordingPipeline), $"Dispose-time stop reported: {ex.Message}");
        }

        _cts?.Dispose();
        _micSource?.Dispose();
        _systemSource?.Dispose();
        _scheduler?.Dispose();
        _writer?.Dispose();

        // The sleep preventer is injected and outlives this pipeline - one
        // instance is shared by every recording in the session. Disposing it
        // here would leave the second and later meetings unable to keep the
        // machine awake, so only the request is released.
        _sleepPreventer.Restore();
    }
}

/// <param name="AudioFilePath">The WAV that was written.</param>
/// <param name="Duration">Length of the recording.</param>
/// <param name="Warnings">Non-fatal problems worth telling the user about.</param>
/// <param name="TranscriptionDrained">False when transcription still had a backlog at stop.</param>
public sealed record RecordingResult(string AudioFilePath, TimeSpan Duration, IReadOnlyList<string> Warnings, bool TranscriptionDrained);
