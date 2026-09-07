using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Stt;

namespace MeetingRecorder.Core.Pipeline;

/// <summary>
/// Owns one meeting from "start recording" to "the folder on disk is complete".
/// </summary>
/// <remarks>
/// Splitting this from <see cref="RecordingPipeline"/> keeps the real-time path
/// free of file-format and bookkeeping concerns: the pipeline only ever writes
/// PCM into an open WAV, and everything that can afford to be slow - transcoding
/// to MP3, rendering transcripts, updating metadata - happens here, after the
/// audio is already safe on disk.
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
        Func<string, int, IAudioFileWriter>? writerFactory = null)
    {
        _captureFactory = captureFactory;
        _transcoder = transcoder;
        _sleepPreventer = sleepPreventer ?? new NullSleepPreventer();
        _logger = logger ?? NullLogger.Instance;
        _writerFactory = writerFactory;
    }

    public TranscriptStore Transcript { get; } = new();

    public MeetingFolder? Folder => _folder;

    public MeetingMetadata? Metadata => _metadata;

    public RecordingState State => _pipeline?.State ?? RecordingState.Idle;

    public bool IsRecording => State is RecordingState.Recording or RecordingState.Preparing;

    public event EventHandler<CaptureFault>? Fault;

    public event Action<RecordingStatus>? StatusChanged;

    public RecordingStatus GetStatus() => _pipeline?.GetStatus() ?? default;

    /// <summary>Creates the meeting folder and starts capture.</summary>
    public MeetingFolder Start(
        AppSettings settings,
        ISpeechRecognizer? recognizer,
        ISpeakerDiarizer? diarizer = null,
        string? title = null)
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

        var profile = settings.Profile ?? new PerformanceProfile();
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
            SttModelId = recognizer?.ModelId,
            DiarizationUsed = diarizer is { IsEnabled: true },
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
            Profile = profile,
            TranscriptionEnabled = settings.SttEnabled,
            AutoSaveInterval = TimeSpan.FromSeconds(Math.Max(5, settings.AutoSaveIntervalSeconds)),
        };

        _pipeline = new RecordingPipeline(options, _captureFactory, Transcript, _sleepPreventer, _logger, _writerFactory);
        _pipeline.Fault += (_, fault) => Fault?.Invoke(this, fault);
        _pipeline.StatusChanged += status => StatusChanged?.Invoke(status);

        var spill = Path.Combine(_folder.Path, ".stt-spill");
        _pipeline.Start(_folder.WavPath, settings, recognizer, _journal, spill, diarizer);

        _logger.Info(nameof(MeetingSessionManager), $"Meeting '{_folder.Name}' started.");
        return _folder;
    }

    /// <summary>
    /// Stops the recording and finishes the folder: transcript files, optional
    /// MP3 transcode, metadata, and removal of the crash journal.
    /// </summary>
    public MeetingSummary Stop(TimeSpan? transcriptionDrainTimeout = null)
    {
        if (_pipeline is null || _folder is null || _metadata is null || _settings is null)
        {
            throw new InvalidOperationException("録音は開始されていません。");
        }

        var result = _pipeline.Stop(transcriptionDrainTimeout);
        var warnings = new List<string>(result.Warnings);

        _metadata.StoppedAtLocal = DateTimeOffset.Now;
        _metadata.DurationSeconds = result.Duration.TotalSeconds;
        _metadata.DiarizationUsed = _metadata.DiarizationUsed && Transcript.Snapshot().Any(s => s.SpeakerId.HasValue);

        var audioPath = result.AudioFilePath;

        if (_settings.Format == RecordingFormat.Mp3)
        {
            audioPath = TranscodeToMp3(result.AudioFilePath, warnings);
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
        // meeting complete. Temporary files go with it.
        TryDeleteJournal();
        TryCleanupTemporaryFiles();

        _pipeline.Dispose();
        _pipeline = null;

        var summary = new MeetingSummary(_folder, _metadata, segments, audioPath, warnings);
        _logger.Info(
            nameof(MeetingSessionManager),
            $"Meeting '{_folder.Name}' finished: {result.Duration:hh\\:mm\\:ss}, {segments.Count} segments, {warnings.Count} warnings.");
        return summary;
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

    private void TryCleanupTemporaryFiles()
    {
        try
        {
            var spill = Path.Combine(_folder!.Path, ".stt-spill");
            if (Directory.Exists(spill))
            {
                Directory.Delete(spill, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _logger.Warn(nameof(MeetingSessionManager), $"Could not delete temporary files: {ex.Message}");
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

/// <param name="Folder">Where the meeting was written.</param>
/// <param name="Metadata">Final metadata as saved.</param>
/// <param name="Segments">Transcript at the moment recording stopped.</param>
/// <param name="AudioPath">The delivered audio file (WAV or MP3).</param>
/// <param name="Warnings">Anything the user should know about this recording.</param>
public sealed record MeetingSummary(
    MeetingFolder Folder,
    MeetingMetadata Metadata,
    IReadOnlyList<Models.TranscriptSegment> Segments,
    string AudioPath,
    IReadOnlyList<string> Warnings);
