using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.Core.Pipeline;

/// <summary>
/// Owns one meeting from "start recording" to "the folder on disk is complete".
/// </summary>
/// <remarks>
/// <para>
/// Splitting this from <see cref="RecordingPipeline"/> keeps the real-time path
/// free of file-format and bookkeeping concerns: the pipeline only ever writes
/// PCM into open WAVs, and everything that can afford to be slow - the single
/// constant gain applied to the finished file, transcoding to MP3, updating
/// metadata - happens here, after the audio is already safe on disk.
/// </para>
/// <para>
/// Stopping produces a complete meeting folder and nothing more. It does not
/// start a transcription; that is a separate thing the user asks for, and
/// <see cref="MeetingSummary.CanTranscribe"/> says whether it is available.
/// </para>
/// </remarks>
public sealed class MeetingSessionManager : IDisposable
{
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly IAudioTranscoder? _transcoder;
    private readonly ISleepPreventer _sleepPreventer;
    private readonly ILogger _logger;
    private readonly Func<string, int, IAudioFileWriter>? _writerFactory;

    private RecordingPipeline? _pipeline;
    private RecoveryJournal? _journal;
    private MeetingFolder? _folder;
    private MeetingMetadata? _metadata;
    private AppSettings? _settings;
    private bool _disposed;

    public MeetingSessionManager(
        IAudioCaptureFactory captureFactory,
        IAudioTranscoder? transcoder = null,
        ISleepPreventer? sleepPreventer = null,
        ILogger? logger = null,
        Func<string, int, IAudioFileWriter>? writerFactory = null,
        TranscriptStore? transcript = null)
    {
        _captureFactory = captureFactory;
        _transcoder = transcoder;
        _sleepPreventer = sleepPreventer ?? new NullSleepPreventer();
        _logger = logger ?? NullLogger.Instance;
        _writerFactory = writerFactory;
        Transcript = transcript ?? new TranscriptStore();
    }

    public TranscriptStore Transcript { get; }

    public MeetingFolder? Folder => _folder;

    public MeetingMetadata? Metadata => _metadata;

    public RecordingState State => _pipeline?.State ?? RecordingState.Idle;

    public bool IsRecording => State is RecordingState.Recording or RecordingState.Preparing;

    public event EventHandler<CaptureFault>? Fault;

    public event Action<RecordingStatus>? StatusChanged;

    public RecordingStatus GetStatus() => _pipeline?.GetStatus() ?? default;

    /// <summary>
    /// Silences the microphone leg. Settable before or during a recording; the
    /// value set here is what the next recording starts with.
    /// </summary>
    public bool MicrophoneMuted
    {
        get => _pipeline?.MicrophoneMuted ?? _pendingMicrophoneMuted;
        set
        {
            _pendingMicrophoneMuted = value;
            if (_pipeline is not null)
            {
                _pipeline.MicrophoneMuted = value;
            }
        }
    }

    /// <summary>Silences the PC-audio leg.</summary>
    public bool SystemAudioMuted
    {
        get => _pipeline?.SystemAudioMuted ?? _pendingSystemAudioMuted;
        set
        {
            _pendingSystemAudioMuted = value;
            if (_pipeline is not null)
            {
                _pipeline.SystemAudioMuted = value;
            }
        }
    }

    private bool _pendingMicrophoneMuted;
    private bool _pendingSystemAudioMuted;

    /// <summary>
    /// Creates the meeting folder and starts capture.
    /// </summary>
    /// <remarks>
    /// Nothing about speech recognition happens here. No model is resolved, no
    /// recognizer is constructed and no worker is started, so the time between
    /// the user pressing the button and the first sample reaching disk does not
    /// depend on which model they have installed, or whether they have one.
    /// </remarks>
    public MeetingFolder Start(AppSettings settings, string? title = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecording)
        {
            throw new InvalidOperationException("録音はすでに実行中です。");
        }

        if (string.IsNullOrWhiteSpace(settings.SaveRoot))
        {
            throw new InvalidOperationException("保存先が設定されていません。");
        }

        _settings = settings;
        Transcript.Clear();

        var startedAt = DateTimeOffset.Now;
        Directory.CreateDirectory(settings.SaveRoot!);
        _folder = MeetingFolder.Create(settings.SaveRoot!, startedAt, title);

        _metadata = new MeetingMetadata
        {
            FolderName = _folder.Name,
            Title = title,
            StartedAtLocal = startedAt,
            AudioFileName = Path.GetFileName(_folder.WavPath),
            Format = RecordingFormat.Wav,
            SampleRate = 48000,
            Channels = 1,
            MicrophoneDeviceName = settings.MicrophoneDeviceName,
            RenderDeviceName = settings.RenderDeviceName,
        };

        _folder.WriteMetadata(_metadata);

        _journal = new RecoveryJournal(_folder.JournalPath, _logger)
        {
            FlushInterval = TimeSpan.FromSeconds(Math.Max(5, settings.AutoSaveIntervalSeconds)),
        };
        _journal.WriteHeader(_metadata);

        var options = new RecordingPipelineOptions
        {
            Processing = settings.Processing,
            AutoSaveInterval = TimeSpan.FromSeconds(Math.Max(5, settings.AutoSaveIntervalSeconds)),
        };

        _pipeline = new RecordingPipeline(options, _captureFactory, Transcript, _sleepPreventer, _logger, _writerFactory);
        _pipeline.Fault += (_, fault) => Fault?.Invoke(this, fault);
        _pipeline.StatusChanged += status => StatusChanged?.Invoke(status);

        // Per-stream audio has to be captured while the meeting runs; there is
        // no way to recover it afterwards from a mixed file. Only kept when the
        // user wants to be able to transcribe this meeting.
        var recognitionAudio = settings.SttEnabled && settings.KeepRecognitionAudio
            ? _folder.Path
            : null;

        _pipeline.Start(_folder.WavPath, settings, _journal, recognitionAudio);

        // A toggle flipped while idle applies to the recording that just started.
        _pipeline.MicrophoneMuted = _pendingMicrophoneMuted || settings.MicrophoneMuted;
        _pipeline.SystemAudioMuted = _pendingSystemAudioMuted || settings.SystemAudioMuted;

        _logger.Info(nameof(MeetingSessionManager), $"Meeting '{_folder.Name}' started.");
        return _folder;
    }

    /// <summary>
    /// Stops the recording and finishes the folder: the one constant gain, an
    /// optional MP3 transcode, metadata, and removal of the crash journal.
    /// </summary>
    public MeetingSummary Stop()
    {
        if (_pipeline is null || _folder is null || _metadata is null || _settings is null)
        {
            throw new InvalidOperationException("録音は開始されていません。");
        }

        var result = _pipeline.Stop();
        var warnings = new List<string>(result.Warnings);

        _metadata.StoppedAtLocal = DateTimeOffset.Now;
        _metadata.DurationSeconds = result.Duration.TotalSeconds;

        var audioPath = result.AudioFilePath;
        NormalizeRecording(audioPath, warnings);

        if (_settings.Format == RecordingFormat.Mp3)
        {
            audioPath = TranscodeToMp3(audioPath, warnings);
        }

        _metadata.AudioFileName = Path.GetFileName(audioPath);
        _metadata.Format = _metadata.AudioFileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            ? RecordingFormat.Mp3
            : RecordingFormat.Wav;
        _metadata.SpeakerNames = new Dictionary<string, string>(Transcript.SpeakerNames);
        _metadata.Warnings = warnings;

        var segments = Transcript.Snapshot();
        TranscriptExporter.Write(_folder, _metadata, segments);
        _folder.WriteMetadata(_metadata);

        _journal?.MarkCompleted();
        _journal?.Dispose();
        _journal = null;
        Transcript.AttachJournal(null);

        // The journal is the crash marker; deleting it is what declares this
        // meeting complete.
        TryDeleteJournal();

        _pipeline.Dispose();
        _pipeline = null;

        var summary = new MeetingSummary(
            _folder, _metadata, audioPath, warnings, result.RecognitionAudioDirectory);
        _logger.Info(
            nameof(MeetingSessionManager),
            $"Meeting '{_folder.Name}' finished: {result.Duration:hh\\:mm\\:ss}, {warnings.Count} warnings.");
        return summary;
    }

    /// <summary>
    /// Applies the single constant gain that makes the recording comfortable to
    /// listen back to.
    /// </summary>
    /// <remarks>
    /// This is the only volume adjustment in the application, it happens once,
    /// after the file is closed and its true peak is known, and it multiplies
    /// every sample by the same number. Doing it here rather than while
    /// recording is what makes that possible: a live stage would have to react
    /// to audio it has not heard yet, which is precisely what an AGC does and
    /// precisely what this application does not.
    /// </remarks>
    private void NormalizeRecording(string audioPath, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            return;
        }

        try
        {
            var result = PeakNormalizer.Normalize(audioPath, _settings!.Processing, _logger, _writerFactory);
            _metadata!.AppliedGainDb = result.Applied ? result.GainDb : null;
            _metadata.SourceClipped = result.Scan.IsClipped;

            if (result.Scan.IsClipped)
            {
                // Say what happened rather than pretend it was fixed: the peaks
                // were flattened before this application ever saw them.
                warnings.Add(
                    $"入力音声が録音時点で既に歪んでいます（全体の {result.Scan.ClippedFraction * 100:F2}% が最大振幅）。"
                    + "後処理では復元できません。マイクの入力レベルや再生音量を下げて録音し直してください。");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(MeetingSessionManager), "Peak normalization failed; the recording is unchanged.", ex);
            warnings.Add($"録音後の音量調整に失敗したため、録音した音量のまま保存しました（{ex.Message}）。");
        }
    }

    private string TranscodeToMp3(string wavPath, List<string> warnings)
    {
        if (_transcoder is null || !_transcoder.IsFormatSupported(RecordingFormat.Mp3))
        {
            warnings.Add("この環境ではMP3エンコーダーを利用できないため、WAVのまま保存しました。");
            return wavPath;
        }

        try
        {
            var mp3Path = _folder!.Mp3Path;
            _transcoder.Transcode(wavPath, mp3Path, RecordingFormat.Mp3, _settings!.Mp3BitrateKbps);

            if (File.Exists(mp3Path) && new FileInfo(mp3Path).Length > 0)
            {
                // Only now is it safe to remove the WAV: the deliverable exists.
                File.Delete(wavPath);
                return mp3Path;
            }

            warnings.Add("MP3への変換結果が空でした。WAVのまま保存しています。");
            return wavPath;
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(MeetingSessionManager), "MP3 transcode failed; keeping the WAV.", ex);
            warnings.Add($"MP3への変換に失敗したため、WAVのまま保存しました（{ex.Message}）。");
            return wavPath;
        }
    }

    /// <summary>Re-renders transcript.txt / transcript.md after the user edits the transcript.</summary>
    public void SaveTranscript()
    {
        if (_folder is null || _metadata is null)
        {
            return;
        }

        _metadata.SpeakerNames = new Dictionary<string, string>(Transcript.SpeakerNames);
        TranscriptExporter.Write(_folder, _metadata, Transcript.Snapshot());
        _folder.WriteMetadata(_metadata);
    }

    private void TryDeleteJournal()
    {
        try
        {
            if (_folder is not null && File.Exists(_folder.JournalPath))
            {
                File.Delete(_folder.JournalPath);
            }
        }
        catch (IOException ex)
        {
            _logger.Warn(nameof(MeetingSessionManager), $"Could not delete the journal: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pipeline?.Dispose();
        _journal?.Dispose();
    }
}
