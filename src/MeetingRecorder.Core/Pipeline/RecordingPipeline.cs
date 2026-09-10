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

    /// <summary>
    /// Audio the pump keeps buffered ahead of itself, in seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pump consumes on the wall clock, which is what keeps the two streams
    /// on one timeline. But an endpoint does not deliver on the wall clock: it
    /// delivers a whole buffer at a time, and the gap between two deliveries is
    /// whatever Windows decided to schedule. Consuming with nothing in hand
    /// means every one of those gaps is a hole punched into the recording -
    /// measured at roughly one 100 ms hole per second of meeting, which is
    /// exactly as bad as it sounds.
    /// </para>
    /// <para>
    /// So the pump waits this long before it takes its first sample, and from
    /// then on it is always this far behind the capture. Nothing is lost and
    /// nothing is added: the recording still advances at exactly one second per
    /// second, it simply has a cushion to absorb a late delivery instead of
    /// writing silence. 250 ms comfortably covers a 100 ms WASAPI buffer plus
    /// ordinary scheduling jitter on a loaded laptop, and it is the backlog
    /// <see cref="DriftCompensator"/> was always written to expect.
    /// </para>
    /// </remarks>
    public double JitterBufferSeconds { get; init; } = 0.25;

    /// <summary>How often the WAV header and the journal are flushed to disk.</summary>
    public TimeSpan AutoSaveInterval { get; init; } = TimeSpan.FromSeconds(15);

    public AudioProcessingSettings Processing { get; init; } = new();
}

/// <summary>
/// The recording engine: two capture streams in, one mixed meeting file plus -
/// when asked for - one 16 kHz working file per stream out.
/// </summary>
/// <remarks>
/// <para><b>This class does not transcribe.</b> It never loads a speech model,
/// never calls a recognizer, and holds no reference to one. Transcription is
/// something the user asks for afterwards, on a finished file
/// (<see cref="Stt.OfflineTranscriptionService"/>). The reason is the one
/// sentence this whole application is arranged around: a transcript can be
/// produced again from the audio any number of times, and audio that was never
/// captured cannot be produced at all. So while a meeting is being recorded,
/// this process does as close to nothing else as it can.</para>
///
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
/// the audio files. It consumes exactly as much audio per tick as the wall
/// clock says has elapsed, pads a starved stream with silence, removes any DC
/// offset, mixes at a fixed gain, and writes. Per tick that is a handful of
/// multiply-adds per sample and two sequential writes - there is no inference,
/// no clustering and no allocation on this path.
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
    private Resampler? _micToRecognition;
    private Resampler? _systemToRecognition;
    private float[] _recognitionScratch = Array.Empty<float>();

    private volatile bool _microphoneMuted;
    private volatile bool _systemAudioMuted;

    // Per-stream 16 kHz copies, kept only when the user wants to be able to
    // transcribe this meeting afterwards. See StartRecognitionCapture.
    private IAudioFileWriter? _micRecognitionWriter;
    private IAudioFileWriter? _systemRecognitionWriter;
    private string? _recognitionAudioDirectory;

    private IAudioCaptureSource? _micSource;
    private IAudioCaptureSource? _systemSource;
    private IAudioFileWriter? _writer;
    private RecoveryJournal? _journal;

    private Thread? _pump;
    private CancellationTokenSource? _cts;
    private Stopwatch? _clock;
    private long _producedSamples;
    private double _micSilenceSeconds;
    private double _systemSilenceSeconds;
    private DateTime _lastFlushUtc;
    private volatile RecordingState _state = RecordingState.Idle;
    private volatile bool _draining;
    private bool _disposed;

    public RecordingPipeline(
        RecordingPipelineOptions options,
        IAudioCaptureFactory captureFactory,
        ISleepPreventer? sleepPreventer = null,
        ILogger? logger = null,
        Func<string, int, IAudioFileWriter>? writerFactory = null)
    {
        _options = options;
        _captureFactory = captureFactory;
        _sleepPreventer = sleepPreventer ?? new NullSleepPreventer();
        _logger = logger ?? NullLogger.Instance;
        _writerFactory = writerFactory ?? ((path, rate) => new WavFileWriter(path, rate));
    }

    /// <summary>
    /// Silences the microphone leg without stopping it. Can be changed while
    /// recording.
    /// </summary>
    /// <remarks>
    /// Muting zeroes the audio in the pump rather than closing the endpoint. The
    /// stream therefore keeps flowing, the timeline keeps advancing at wall-clock
    /// rate, and un-muting takes effect on the very next block instead of paying
    /// for a device restart. Nothing muted reaches the file or the meters.
    /// </remarks>
    public bool MicrophoneMuted
    {
        get => _microphoneMuted;
        set
        {
            if (_microphoneMuted == value)
            {
                return;
            }

            _microphoneMuted = value;
            if (value)
            {
                _micMeter.Reset();
            }

            _logger.Info(nameof(RecordingPipeline), value ? "Microphone muted." : "Microphone un-muted.");
        }
    }

    /// <summary>Silences the PC-audio leg without stopping it.</summary>
    public bool SystemAudioMuted
    {
        get => _systemAudioMuted;
        set
        {
            if (_systemAudioMuted == value)
            {
                return;
            }

            _systemAudioMuted = value;
            if (value)
            {
                _systemMeter.Reset();
            }

            _logger.Info(nameof(RecordingPipeline), value ? "PC audio muted." : "PC audio un-muted.");
        }
    }

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
    /// Starts capture.
    /// </summary>
    /// <param name="recognitionAudioDirectory">
    /// When set, each capture stream is also written here as 16 kHz mono WAV, so
    /// the meeting can be transcribed afterwards with its per-stream attribution
    /// intact. Costs about
    /// <see cref="RecognitionAudioNames.MegabytesPerStreamPerHour"/> MB per hour
    /// per stream; null disables it.
    /// </param>
    /// <remarks>
    /// The critical path here is: resolve the devices, open the file, start
    /// capture, report. No model is loaded, no inference is prepared and no
    /// analysis is scheduled, so pressing "record" costs the same whether or not
    /// a speech model is installed.
    /// </remarks>
    public void Start(
        string audioFilePath,
        AppSettings settings,
        RecoveryJournal? journal,
        string? recognitionAudioDirectory = null)
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

        _micRing = AudioRingBuffer.ForSeconds(rate, _options.RingBufferSeconds);
        _systemRing = AudioRingBuffer.ForSeconds(rate, _options.RingBufferSeconds);
        _micChain = new AudioProcessingChain(_options.Processing, rate);
        _systemChain = new AudioProcessingChain(_options.Processing, rate);
        // The compensator's idea of "normal" has to match the cushion the pump
        // keeps, or it would read the cushion as drift and correct it away.
        _micDrift = new DriftCompensator(rate, _options.JitterBufferSeconds);
        _systemDrift = new DriftCompensator(rate, _options.JitterBufferSeconds);
        _mixer = new StreamMixer(_options.Processing, rate);
        _micToRecognition = new Resampler(rate, SpeechConstants.SampleRate);
        _systemToRecognition = new Resampler(rate, SpeechConstants.SampleRate);
        StartRecognitionCapture(recognitionAudioDirectory);
        _microphoneMuted = settings.MicrophoneMuted;
        _systemAudioMuted = settings.SystemAudioMuted;

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
            StopRecognitionCapture();
            _state = RecordingState.Faulted;
            throw new InvalidOperationException("マイクとPC内部音声のどちらも取得できませんでした。録音を開始できません。");
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
            // must win against the UI and against anything the user starts next.
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
        StopRecognitionCapture();
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
        // While muted the meter must read silence: a moving bar next to a muted
        // stream tells the user their voice is being recorded when it is not.
        // The samples still enter the ring so the drift compensator keeps seeing
        // a healthy stream; the pump is where they are actually discarded.
        if (!_microphoneMuted)
        {
            _micMeter.Update(samples);
        }

        _micRing?.Write(samples);
    }

    private void OnSystemData(ReadOnlySpan<float> samples)
    {
        if (!_systemAudioMuted)
        {
            _systemMeter.Update(samples);
        }

        _systemRing?.Write(samples);
    }

    private void PumpLoop(CancellationToken token)
    {
        var rate = _options.SampleRate;
        var tick = _options.PumpInterval;
        var maxBlock = rate; // never process more than one second in a single pass

        var micBuffer = new float[maxBlock];
        var systemBuffer = new float[maxBlock];
        var mixBuffer = new float[maxBlock];
        _recognitionScratch = new float[(maxBlock / (rate / SpeechConstants.SampleRate)) + 16];

        try
        {
            while (!token.IsCancellationRequested)
            {
                int need;
                var caughtUp = true;
                if (_draining)
                {
                    // Capture has stopped and the pump is one jitter buffer
                    // behind, so what is left in the rings is the tail of the
                    // meeting. Take exactly that - asking the wall clock for more
                    // would only pad the end with silence and count it as a gap.
                    need = (int)Math.Min(
                        maxBlock,
                        Math.Max(_micRing?.Count ?? 0, _systemRing?.Count ?? 0));

                    if (need <= 0)
                    {
                        break;
                    }
                }
                else
                {
                    // One jitter buffer behind real time - see JitterBufferSeconds.
                    var elapsed = _clock!.Elapsed;
                    var target = (long)(
                        Math.Max(0.0, elapsed.TotalSeconds - _options.JitterBufferSeconds) * rate);
                    need = (int)Math.Min(target - _producedSamples, maxBlock);

                    if (need <= 0)
                    {
                        Thread.Sleep(tick);
                        continue;
                    }

                    // Only rest once the file has caught up with the clock;
                    // otherwise loop straight round and keep closing the gap.
                    caughtUp = target - _producedSamples - need < rate / 100;
                }

                ProcessBlock(micBuffer.AsSpan(0, need), systemBuffer.AsSpan(0, need), mixBuffer.AsSpan(0, need), tick.TotalSeconds);
                _producedSamples += need;

                if (DateTime.UtcNow - _lastFlushUtc >= _options.AutoSaveInterval)
                {
                    FlushToDisk();
                }

                if (!_draining && caughtUp)
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

    private void ProcessBlock(Span<float> mic, Span<float> system, Span<float> mix, double tickSeconds)
    {
        var rate = _options.SampleRate;

        FillStream(_micRing, mic, _micDrift!, tickSeconds, ref _micSilenceSeconds, rate);
        FillStream(_systemRing, system, _systemDrift!, tickSeconds, ref _systemSilenceSeconds, rate);

        // Muting happens here, before anything reads the audio, so a muted leg
        // reaches neither the meeting file nor the working audio - while the
        // block itself is still produced, keeping the timeline on the wall clock.
        if (_microphoneMuted)
        {
            mic.Clear();
        }

        if (_systemAudioMuted)
        {
            system.Clear();
        }

        // One filter, applied once, feeding both destinations. The recognition
        // path and the file path used to diverge here because the file was run
        // through a gate, an AGC, a compressor and a limiter that would have
        // wrecked recognition. None of those stages exists any more, so there is
        // nothing left to diverge about: the working audio is the meeting audio
        // before the mix, at 16 kHz.
        _micChain!.Process(mic);
        _systemChain!.Process(system);

        WriteRecognitionAudio(_micToRecognition!, mic, _micRecognitionWriter);
        WriteRecognitionAudio(_systemToRecognition!, system, _systemRecognitionWriter);

        _mixer!.Mix(mic, system, mix);
        _writer!.Write(mix);
    }

    /// <summary>
    /// Opens the per-stream working audio files, or leaves them closed when the
    /// meeting is not going to be transcribed.
    /// </summary>
    private void StartRecognitionCapture(string? directory)
    {
        _recognitionAudioDirectory = null;
        _micRecognitionWriter = null;
        _systemRecognitionWriter = null;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            _micRecognitionWriter = new WavFileWriter(
                Path.Combine(directory, RecognitionAudioNames.Microphone), SpeechConstants.SampleRate);
            _systemRecognitionWriter = new WavFileWriter(
                Path.Combine(directory, RecognitionAudioNames.SystemAudio), SpeechConstants.SampleRate);
            _recognitionAudioDirectory = directory;
            _logger.Info(nameof(RecordingPipeline), $"Keeping per-stream working audio in '{directory}'.");
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(RecordingPipeline), "Could not open the working audio files.", ex);
            AddWarning("文字起こし用の音声を保存できません。この録音は停止後に文字起こしできません。");
            StopRecognitionCapture();
        }
    }

    private void StopRecognitionCapture()
    {
        try
        {
            _micRecognitionWriter?.Flush();
            _micRecognitionWriter?.Dispose();
            _systemRecognitionWriter?.Flush();
            _systemRecognitionWriter?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(RecordingPipeline), $"Closing the working audio reported: {ex.Message}");
        }
        finally
        {
            _micRecognitionWriter = null;
            _systemRecognitionWriter = null;
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
            if (!_draining)
            {
                // While draining the rings are supposed to run dry; counting that
                // as a starved stream would report a gap at the end of every
                // healthy recording.
                drift.ReportSilenceInserted(inserted);
            }

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

    private void WriteRecognitionAudio(Resampler resampler, ReadOnlySpan<float> source, IAudioFileWriter? writer)
    {
        if (writer is null)
        {
            return;
        }

        var needed = resampler.EstimateOutputLength(source.Length);
        if (_recognitionScratch.Length < needed)
        {
            _recognitionScratch = new float[needed];
        }

        var written = resampler.Process(source, _recognitionScratch);

        try
        {
            writer.Write(_recognitionScratch.AsSpan(0, written));
        }
        catch (Exception ex)
        {
            // Losing the ability to transcribe is a disappointment; losing the
            // meeting is not acceptable. Stop keeping the copies and carry on.
            _logger.Error(nameof(RecordingPipeline), "Writing the working audio failed; this meeting will not be transcribable.", ex);
            AddWarning("文字起こし用の音声を保存できませんでした。この録音は停止後に文字起こしできません。");
            StopRecognitionCapture();
        }
    }

    private void FlushToDisk()
    {
        try
        {
            _writer?.Flush();
            _micRecognitionWriter?.Flush();
            _systemRecognitionWriter?.Flush();
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
            _micSilenceSeconds,
            _systemSilenceSeconds,
            overruns,
            inserted,
            corrected,
            _writer?.SamplesWritten * 2 ?? 0);
    }

    /// <summary>
    /// Stops capture and finalises the files.
    /// </summary>
    /// <remarks>
    /// Stopping a recording stops a recording. It does not start a
    /// transcription, and it does not wait for one: the moment this returns, the
    /// meeting is complete on disk and the user is free to close the
    /// application, copy the folder or do nothing at all.
    /// </remarks>
    public RecordingResult Stop()
    {
        lock (_stateSync)
        {
            if (_state is RecordingState.Idle or RecordingState.Stopping)
            {
                return new RecordingResult(_writer?.FilePath ?? string.Empty, TimeSpan.Zero, Warnings);
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

        // Drain: the pump normally runs one jitter buffer behind the capture, so
        // stopping without emptying the rings would throw away the last quarter
        // second of the meeting - which is usually somebody finishing a
        // sentence. Draining lets it consume everything that was captured, then
        // stops. Bounded, because a wedged ring must not hang the application.
        _draining = true;
        var drainDeadline = DateTime.UtcNow
            + TimeSpan.FromSeconds(_options.JitterBufferSeconds + 1.0);
        while (DateTime.UtcNow < drainDeadline)
        {
            if ((_micRing?.Count ?? 0) == 0 && (_systemRing?.Count ?? 0) == 0)
            {
                // One more tick so the pump writes what it has just read.
                Thread.Sleep(_options.PumpInterval);
                break;
            }

            Thread.Sleep(10);
        }

        _cts?.Cancel();
        _pump?.Join(TimeSpan.FromSeconds(5));
        _draining = false;

        var duration = TimeSpan.FromSeconds(_writer?.DurationSeconds ?? 0);
        var path = _writer?.FilePath ?? string.Empty;
        var recognitionDirectory = _recognitionAudioDirectory;
        StopRecognitionCapture();

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

        ReportCaptureGaps(duration);

        _logger.Info(nameof(RecordingPipeline), $"Recording stopped after {duration:hh\\:mm\\:ss} -> {path}");
        return new RecordingResult(path, duration, Warnings, recognitionDirectory);
    }

    /// <summary>
    /// Says so when the microphone starved and silence had to be written in its
    /// place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The microphone leg delivers continuously or it is broken - unlike loopback,
    /// which legitimately delivers nothing at all while the endpoint is silent, so
    /// only the microphone is judged here.
    /// </para>
    /// <para>
    /// The pump keeps a jitter buffer precisely so this does not happen, and in a
    /// healthy recording the figure is zero. It being non-zero means audio that
    /// was spoken is not in the file, and that is worth a sentence at the end of
    /// the meeting rather than a number in a diagnostics line nobody reads.
    /// </para>
    /// </remarks>
    private void ReportCaptureGaps(TimeSpan duration)
    {
        var insertedSamples = _micDrift?.InsertedSilenceSamples ?? 0;
        if (insertedSamples <= 0 || duration <= TimeSpan.Zero)
        {
            return;
        }

        var insertedSeconds = insertedSamples / (double)_options.SampleRate;
        var fraction = insertedSeconds / duration.TotalSeconds;

        _logger.Info(
            nameof(RecordingPipeline),
            $"Microphone silence inserted: {insertedSeconds:F2}s of {duration.TotalSeconds:F0}s ({fraction * 100:F2}%).");

        // A tenth of a percent is a handful of milliseconds around a device
        // hiccup and is not worth alarming anybody about. Above one percent the
        // recording audibly stutters.
        if (fraction < 0.001)
        {
            return;
        }

        var message =
            $"マイクの音声が途切れ、合計 {insertedSeconds:F1} 秒（録音全体の {fraction * 100:F1}%）を無音で補いました。"
            + "その部分の音声は残っていません。"
            + "録音中に他の重い処理を動かしていた場合は、次回は停止してからお試しください。";

        AddWarning(message);
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
                Stop();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(RecordingPipeline), $"Dispose-time stop reported: {ex.Message}");
        }

        StopRecognitionCapture();
        _cts?.Dispose();
        _micSource?.Dispose();
        _systemSource?.Dispose();
        _writer?.Dispose();

        // The sleep preventer is injected and outlives this pipeline - one
        // instance is shared by every recording in the session. Disposing it
        // here would leave the second and later meetings unable to keep the
        // machine awake, so only the request is released.
        _sleepPreventer.Restore();
    }
}

/// <param name="AudioFilePath">The mixed recording the user keeps.</param>
/// <param name="Duration">Wall-clock length of the recording.</param>
/// <param name="Warnings">Non-fatal problems, copied into metadata.json.</param>
/// <param name="RecognitionAudioDirectory">
/// Where the per-stream working audio was kept, or null when it was not. This
/// is what a later transcription reads.
/// </param>
public sealed record RecordingResult(
    string AudioFilePath,
    TimeSpan Duration,
    IReadOnlyList<string> Warnings,
    string? RecognitionAudioDirectory = null);
