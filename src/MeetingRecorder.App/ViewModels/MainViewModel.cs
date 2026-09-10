using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using MeetingRecorder.App.Services;
using MeetingRecorder.App.Views;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Stt;
using MeetingRecorder.Minutes;

namespace MeetingRecorder.App.ViewModels;

/// <summary>
/// Where a meeting is in the one workflow this application has.
/// </summary>
/// <remarks>
/// <code>
/// NoAudio -> Recording -> AudioReady -> Transcribing -> TranscriptReady -> (minutes)
///               import ------^
/// </code>
/// Every command's availability is derived from this, which is what keeps the
/// two halves apart: stopping a recording moves to <see cref="AudioReady"/> and
/// stops there, and only the user pressing "文字起こし" moves it on.
/// </remarks>
public enum MeetingWorkflowState
{
    /// <summary>Nothing loaded. Record, or import a file.</summary>
    NoAudio,

    /// <summary>Capture is running.</summary>
    Recording,

    /// <summary>Audio exists and is complete. Nothing is being done to it.</summary>
    AudioReady,

    /// <summary>A transcription the user asked for is running.</summary>
    Transcribing,

    /// <summary>A transcript exists. Minutes can be generated from it.</summary>
    TranscriptReady,
}

/// <summary>View model behind the main window.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TranscriptItemViewModel> _itemsById = new(StringComparer.Ordinal);
    private readonly Stopwatch _tickClock = Stopwatch.StartNew();
    private readonly TranscriptStore _transcript = new();

    private MeetingSessionManager? _session;
    private MeetingSummary? _meeting;
    private TimeSpan _lastTick;

    private MeetingWorkflowState _workflowState = MeetingWorkflowState.NoAudio;
    private string _elapsedText = "00:00:00";
    private double _micLevel;
    private double _systemLevel;
    private double _micPeakLevel;
    private double _systemPeakLevel;
    private string _micLevelText = "-- dBFS";
    private string _systemLevelText = "-- dBFS";
    private string _audioSummaryText = "録音またはインポートした音声はまだありません。";
    private string _saveRootText = string.Empty;
    private string _modelText = string.Empty;
    private string _statusMessage = string.Empty;
    private string _diagnosticsText = string.Empty;
    private string _meetingTitle = string.Empty;
    private string _warningText = string.Empty;
    private string _transcriptionStatus = string.Empty;
    private double _transcriptionProgress;
    private bool _isBusy;
    private bool _disposed;

    private CancellationTokenSource? _transcriptionCts;

    public MainViewModel(AppServices services)
    {
        _services = services;

        _transcript.SegmentAdded += OnSegmentAdded;
        _transcript.SegmentChanged += OnSegmentChanged;
        _transcript.SegmentRemoved += OnSegmentRemoved;

        // Recording and transcribing are mutually exclusive, and the exclusion
        // points both ways. Transcription is minutes of inference on every core
        // this machine has; starting a recording underneath it would put exactly
        // the load on the capture path that removing live transcription was
        // meant to take off it.
        StartCommand = new AsyncRelayCommand(StartRecordingAsync, () => !IsRecording && !IsTranscribing && !IsBusy);
        StopCommand = new AsyncRelayCommand(StopRecordingAsync, () => IsRecording && !IsBusy);
        ImportAudioCommand = new AsyncRelayCommand(ImportAudioAsync, () => !IsRecording && !IsTranscribing && !IsBusy);
        OpenSettingsCommand = new RelayCommand(OpenSettings, () => !IsRecording);
        OpenFolderCommand = new RelayCommand(OpenSaveFolder);
        TranscribeCommand = new AsyncRelayCommand(TranscribeAsync, CanTranscribe);
        CancelTranscriptionCommand = new RelayCommand(() => _transcriptionCts?.Cancel(), () => IsTranscribing);
        GenerateMinutesCommand = new AsyncRelayCommand(GenerateMinutesAsync, CanGenerateMinutes);
        SaveTranscriptCommand = new RelayCommand(SaveTranscript, () => _meeting is not null && Transcript.Count > 0);
        RenameSpeakerCommand = new RelayCommand(RenameSpeakers, () => CanRenameSpeakers);
        ToggleMicrophoneMuteCommand = new RelayCommand(() => MicrophoneMuted = !MicrophoneMuted);
        ToggleSystemAudioMuteCommand = new RelayCommand(() => SystemAudioMuted = !SystemAudioMuted);
        ExportAudioCommand = new RelayCommand(ExportAudio, () => !IsRecording && LastAudioPath() is not null);

        RefreshSettingsDisplay();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // 10 Hz: smooth enough for level meters, cheap enough to be
            // invisible next to a recording.
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += OnTimerTick;
    }

    public ObservableCollection<TranscriptItemViewModel> Transcript { get; } = new();

    public ObservableCollection<SpeakerViewModel> Speakers { get; } = new();

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand ImportAudioCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public AsyncRelayCommand TranscribeCommand { get; }

    public RelayCommand CancelTranscriptionCommand { get; }

    public AsyncRelayCommand GenerateMinutesCommand { get; }

    public RelayCommand SaveTranscriptCommand { get; }

    public RelayCommand RenameSpeakerCommand { get; }

    public RelayCommand ToggleMicrophoneMuteCommand { get; }

    public RelayCommand ToggleSystemAudioMuteCommand { get; }

    public RelayCommand ExportAudioCommand { get; }

    // ---- Workflow state --------------------------------------------------

    public MeetingWorkflowState WorkflowState
    {
        get => _workflowState;
        private set
        {
            if (SetProperty(ref _workflowState, value))
            {
                OnPropertyChanged(nameof(IsRecording));
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsTranscribing));
                OnPropertyChanged(nameof(IsNotTranscribing));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(NextStepText));
                OnPropertyChanged(nameof(CanRenameSpeakers));
                RaiseCommandStates();
            }
        }
    }

    public bool IsRecording => WorkflowState == MeetingWorkflowState.Recording;

    public bool IsIdle => !IsRecording;

    public bool IsTranscribing => WorkflowState == MeetingWorkflowState.Transcribing;

    public bool IsNotTranscribing => !IsTranscribing;

    /// <summary>The one line that says what the application is doing right now.</summary>
    public string StateText => WorkflowState switch
    {
        MeetingWorkflowState.Recording => "録音中",
        MeetingWorkflowState.AudioReady => "待機中（音声あり）",
        MeetingWorkflowState.Transcribing => "文字起こし中",
        MeetingWorkflowState.TranscriptReady => "文字起こし完了",
        _ => "待機中",
    };

    /// <summary>
    /// What to do next, spelled out. A first-time user should be able to read
    /// the order - record, stop, transcribe, minutes - off the window itself.
    /// </summary>
    public string NextStepText => WorkflowState switch
    {
        MeetingWorkflowState.NoAudio =>
            "① 「録音開始」で録音するか、「音声ファイルをインポート」で既存の音声を読み込みます。",
        MeetingWorkflowState.Recording =>
            "② 会議が終わったら「録音停止」を押します。録音中は文字起こしを行いません。",
        MeetingWorkflowState.AudioReady =>
            "③ 「文字起こし」を押すと、保存された音声をこのPC内で文字起こしします（録音停止では自動実行されません）。",
        MeetingWorkflowState.Transcribing =>
            "文字起こし中です。中止しても録音データはそのまま残ります。",
        MeetingWorkflowState.TranscriptReady =>
            "④ 本文を編集して保存できます。必要であれば「議事録を生成」を実行してください。",
        _ => string.Empty,
    };

    /// <summary>
    /// Silences the microphone leg. Applies immediately, including mid-recording,
    /// and is remembered for the next meeting.
    /// </summary>
    public bool MicrophoneMuted
    {
        get => _services.Settings.MicrophoneMuted;
        set
        {
            if (_services.Settings.MicrophoneMuted == value)
            {
                return;
            }

            _services.Settings.MicrophoneMuted = value;
            if (_session is not null)
            {
                _session.MicrophoneMuted = value;
            }

            _services.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MicrophoneMuteText));
            OnPropertyChanged(nameof(MuteWarningText));
            OnPropertyChanged(nameof(HasMuteWarning));
        }
    }

    /// <summary>Silences the PC-audio leg.</summary>
    public bool SystemAudioMuted
    {
        get => _services.Settings.SystemAudioMuted;
        set
        {
            if (_services.Settings.SystemAudioMuted == value)
            {
                return;
            }

            _services.Settings.SystemAudioMuted = value;
            if (_session is not null)
            {
                _session.SystemAudioMuted = value;
            }

            _services.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SystemAudioMuteText));
            OnPropertyChanged(nameof(MuteWarningText));
            OnPropertyChanged(nameof(HasMuteWarning));
        }
    }

    public string MicrophoneMuteText => MicrophoneMuted ? "マイク: ミュート中" : "マイク: 録音する";

    public string SystemAudioMuteText => SystemAudioMuted ? "PC音声: ミュート中" : "PC音声: 録音する";

    /// <summary>
    /// Muting both is a legitimate choice, but it records silence, and finding
    /// that out after the meeting is the kind of thing that loses an hour of
    /// someone's work.
    /// </summary>
    public string MuteWarningText => MicrophoneMuted && SystemAudioMuted
        ? "マイクとPC音声の両方がミュートされています。このまま録音すると無音のファイルになります。"
        : string.Empty;

    public bool HasMuteWarning => !string.IsNullOrWhiteSpace(MuteWarningText);

    public string TranscriptionStatus
    {
        get => _transcriptionStatus;
        private set => SetProperty(ref _transcriptionStatus, value);
    }

    /// <summary>0..1, for the transcription progress bar.</summary>
    public double TranscriptionProgress
    {
        get => _transcriptionProgress;
        private set => SetProperty(ref _transcriptionProgress, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    /// <summary>Microphone level as 0..1 for the meter bar.</summary>
    public double MicLevel
    {
        get => _micLevel;
        private set => SetProperty(ref _micLevel, value);
    }

    public double SystemLevel
    {
        get => _systemLevel;
        private set => SetProperty(ref _systemLevel, value);
    }

    public double MicPeakLevel
    {
        get => _micPeakLevel;
        private set => SetProperty(ref _micPeakLevel, value);
    }

    public double SystemPeakLevel
    {
        get => _systemPeakLevel;
        private set => SetProperty(ref _systemPeakLevel, value);
    }

    public string MicLevelText
    {
        get => _micLevelText;
        private set => SetProperty(ref _micLevelText, value);
    }

    public string SystemLevelText
    {
        get => _systemLevelText;
        private set => SetProperty(ref _systemLevelText, value);
    }

    /// <summary>Which audio is loaded, and whether it can be transcribed.</summary>
    public string AudioSummaryText
    {
        get => _audioSummaryText;
        private set => SetProperty(ref _audioSummaryText, value);
    }

    public string SaveRootText
    {
        get => _saveRootText;
        private set => SetProperty(ref _saveRootText, value);
    }

    public string ModelText
    {
        get => _modelText;
        private set => SetProperty(ref _modelText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        private set => SetProperty(ref _diagnosticsText, value);
    }

    public string MeetingTitle
    {
        get => _meetingTitle;
        set => SetProperty(ref _meetingTitle, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>
    /// Speaker naming is offered only where the audio can support it.
    /// </summary>
    /// <remarks>
    /// A recording made here has a microphone file and a loopback file, so
    /// clusters mean something. An imported file is one anonymous source: there
    /// is nothing to name, and offering the button would imply the application
    /// knows who is talking when it does not.
    /// </remarks>
    public bool CanRenameSpeakers
        => !IsRecording && !IsTranscribing && Speakers.Count > 0 && _meeting?.HasSourceAttribution == true;

    /// <summary>Starts idle monitoring so the meters are live before recording.</summary>
    public void Activate()
    {
        _services.LevelMonitor.Start(_services.Settings);
        _timer.Start();

        if (_services.LevelMonitor.MicrophoneError is { } micError)
        {
            WarningText = $"マイクを監視できません: {micError}";
        }
        else if (_services.LevelMonitor.SystemAudioError is { } systemError)
        {
            WarningText = $"PC内部音声を監視できません: {systemError}";
        }
    }

    public void RefreshSettingsDisplay()
    {
        SaveRootText = _services.Settings.SaveRoot ?? "(未設定)";

        var descriptor = _services.TranscriptionModel();

        if (!_services.Settings.SttEnabled)
        {
            ModelText = "文字起こし: 無効（録音のみ）";
        }
        else if (descriptor is null)
        {
            ModelText = "文字起こしモデル: 未選択";
        }
        else
        {
            var present = _services.ModelStore.IsPresent(descriptor);
            ModelText = present
                ? $"文字起こしモデル: {descriptor.DisplayName}"
                : $"文字起こしモデル: {descriptor.DisplayName}（未取得）";
        }

        TranscribeCommand.RaiseCanExecuteChanged();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var now = _tickClock.Elapsed;
        var delta = now - _lastTick;
        _lastTick = now;

        if (IsRecording && _session is not null)
        {
            UpdateFromStatus(_session.GetStatus());
        }
        else
        {
            _services.LevelMonitor.Tick(delta);
            UpdateMeters(_services.LevelMonitor.MicrophoneLevel, _services.LevelMonitor.SystemAudioLevel);
        }
    }

    private void UpdateFromStatus(RecordingStatus status)
    {
        ElapsedText = FormatDuration(status.Elapsed);
        UpdateMeters(status.MicLevel, status.SystemLevel);

        var resources = _services.PerformanceMonitor.Sample();
        DiagnosticsText =
            $"CPU {resources.CpuPercent:F0}% / RAM {resources.WorkingSetMb:F0}MB / " +
            $"書き込み {status.BytesWritten / 1024.0 / 1024.0:F0}MB / " +
            $"バッファ溢れ {status.BufferOverruns} / 無音補間 {status.SilenceInsertedMs / 1000.0:F1}s / " +
            $"同期補正 {status.DriftCorrectedMs:F0}ms";

        if (status.MicSilenceSeconds > 30 && status.SystemSilenceSeconds > 30)
        {
            WarningText = "マイクとPC内部音声の両方が30秒以上無音です。デバイス設定を確認してください（録音は継続中）。";
        }
    }

    private void UpdateMeters(LevelSnapshot mic, LevelSnapshot system)
    {
        MicLevel = mic.Normalized();
        MicPeakLevel = mic.NormalizedPeak();
        SystemLevel = system.Normalized();
        SystemPeakLevel = system.NormalizedPeak();

        MicLevelText = FormatLevel(mic);
        SystemLevelText = FormatLevel(system);
    }

    private static string FormatLevel(LevelSnapshot snapshot)
        => snapshot.PeakDb <= AudioMath.MinDb + 1
            ? "無信号"
            : $"{snapshot.RmsDb:F0} dBFS (peak {snapshot.PeakDb:F0})";

    private static string FormatDuration(TimeSpan value)
        => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";

    // ---- Recording -------------------------------------------------------

    private async Task StartRecordingAsync()
    {
        IsBusy = true;
        try
        {
            WarningText = string.Empty;
            StatusMessage = "録音前チェックを実行しています...";

            // The monitor holds the same endpoints; release them first.
            _services.LevelMonitor.Stop();

            var results = await Task.Run(() => _services.PreflightChecker.Run(_services.Settings));
            if (!PreflightDialog.Confirm(Application.Current?.MainWindow, results))
            {
                StatusMessage = "録音を開始しませんでした。";
                _services.LevelMonitor.Start(_services.Settings);
                return;
            }

            // Release the previous meeting's session before starting another.
            _session?.Dispose();
            _session = new MeetingSessionManager(
                _services.CaptureFactory,
                _services.Transcoder,
                _services.SleepPreventer,
                _services.Logger,
                writerFactory: null,
                transcript: _transcript);

            _session.Fault += OnCaptureFault;

            ClearTranscript();
            _meeting = null;

            var title = string.IsNullOrWhiteSpace(MeetingTitle) ? null : MeetingTitle.Trim();

            // With automatic saving off the meeting is still recorded to disk,
            // just not into the save folder: it goes to a working area and stays
            // there until the user exports it. Everything downstream - the
            // journal, crash recovery, transcription - is unchanged, because
            // only the root differs.
            var settings = _services.Settings;
            if (!settings.AutoSaveRecordings)
            {
                settings = settings.Clone();
                settings.SaveRoot = AppPaths.WorkingRecordingRoot;
            }

            var folder = _session.Start(settings, title);

            WorkflowState = MeetingWorkflowState.Recording;
            StatusMessage = _services.Settings.AutoSaveRecordings
                ? $"録音中: {folder.Path}"
                : $"録音中（自動保存オフ・作業用フォルダー）: {folder.Path}";
            AudioSummaryText = "録音中です。文字起こしは停止後に手動で実行します。";
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Starting the recording failed.", ex);
            MessageBox.Show(
                $"録音を開始できませんでした。\n\n{ex.Message}",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            WorkflowState = MeetingWorkflowState.NoAudio;
            _services.LevelMonitor.Start(_services.Settings);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Stops the recording. It does not start a transcription, and nothing else
    /// in this method does either.
    /// </summary>
    private async Task StopRecordingAsync()
    {
        if (_session is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "録音を停止しています...";

        try
        {
            var session = _session;
            var summary = await Task.Run(session.Stop);
            _meeting = summary;

            ElapsedText = FormatDuration(summary.Metadata.Duration);
            WorkflowState = MeetingWorkflowState.AudioReady;
            StatusMessage = _services.Settings.AutoSaveRecordings
                ? $"録音を停止しました: {summary.Folder.Path}"
                : "録音を停止しました。自動保存はオフです。「音声を出力...」で保存先を指定してください。";

            RefreshAudioSummary();

            if (summary.Warnings.Count > 0)
            {
                WarningText = string.Join(" / ", summary.Warnings);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Stopping the recording failed.", ex);
            MessageBox.Show(
                $"録音の停止処理でエラーが発生しました。\n録音データは保存先フォルダーに残っています。\n\n{ex.Message}",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            WorkflowState = MeetingWorkflowState.AudioReady;
        }
        finally
        {
            IsBusy = false;
            _services.LevelMonitor.Start(_services.Settings);
            RaiseCommandStates();
        }
    }

    // ---- Import ----------------------------------------------------------

    private async Task ImportAudioAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "文字起こしする音声ファイルを選択",
            Filter = AudioImportFormats.FileDialogFilter,
            Multiselect = false,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = dialog.FileName;
        IsBusy = true;
        WarningText = string.Empty;
        StatusMessage = $"「{Path.GetFileName(path)}」を読み込んでいます...";

        try
        {
            var settings = _services.Settings;
            var summary = await Task.Run(() => _services.MeetingImporter.Import(settings, path));

            _session?.Dispose();
            _session = null;

            ClearTranscript();
            _meeting = summary;
            ElapsedText = FormatDuration(summary.Metadata.Duration);
            WorkflowState = MeetingWorkflowState.AudioReady;
            StatusMessage = $"読み込みました: {summary.Folder.Path}（元のファイルは変更していません）";
            RefreshAudioSummary();

            if (summary.Warnings.Count > 0)
            {
                WarningText = string.Join(" / ", summary.Warnings);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), $"Importing '{path}' failed.", ex);
            MessageBox.Show(
                $"音声ファイルを読み込めませんでした。\n元のファイルは変更していません。\n\n{ex.Message}",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            StatusMessage = "音声ファイルの読み込みに失敗しました。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Transcription ---------------------------------------------------

    /// <summary>The audio the export button would write, or null when there is none.</summary>
    private string? LastAudioPath()
    {
        var path = _meeting?.AudioPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private bool CanTranscribe()
        => _meeting is not null
           && !IsRecording
           && !IsTranscribing
           && !IsBusy
           && _services.Settings.SttEnabled
           && (_meeting.CanTranscribe || CanRebuildWorkingAudio(_meeting));

    private static bool CanRebuildWorkingAudio(MeetingSummary meeting)
        => meeting.IsImported
           && !string.IsNullOrWhiteSpace(meeting.Metadata.ImportedFromPath)
           && File.Exists(meeting.Metadata.ImportedFromPath!);

    private bool CanGenerateMinutes()
        => _meeting is not null && !IsRecording && !IsTranscribing && !IsBusy && Transcript.Count > 0;

    /// <summary>
    /// Transcribes the audio that is loaded. Reached only from the button.
    /// </summary>
    /// <remarks>
    /// This is the only place in the application that loads a speech model. It
    /// runs on a thread-pool thread over a file that is already closed, so
    /// nothing it does - not the model load, not the inference, not the speaker
    /// clustering - can touch a recording. There is no recording while it runs,
    /// in both directions: <see cref="CanTranscribe"/> refuses to start this
    /// during capture, and <c>StartCommand</c> refuses to start a recording
    /// while this is running.
    /// </remarks>
    private async Task TranscribeAsync()
    {
        var meeting = _meeting;
        if (meeting is null || IsTranscribing)
        {
            // Guard against a double press: the command is disabled while this
            // runs, but a command can still be invoked from a binding twice
            // before the state change lands.
            return;
        }

        if (!meeting.CanTranscribe && !await TryRebuildWorkingAudioAsync(meeting))
        {
            return;
        }

        var recognizer = _services.TryCreateTranscriptionRecognizer(out var reason);
        if (recognizer is null)
        {
            var message = reason ?? "文字起こしを開始できません。";
            TranscriptionStatus = message;
            StatusMessage = message;
            MessageBox.Show(message, "MeetingRecorder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var diarizer = meeting.HasSourceAttribution ? _services.TryCreateDiarizer() : null;
        _transcriptionCts = new CancellationTokenSource();
        WorkflowState = MeetingWorkflowState.Transcribing;
        TranscriptionProgress = 0;
        TranscriptionStatus = "文字起こしの準備をしています...";

        // Fully qualified: this view model has a TranscriptionProgress property
        // of its own.
        var progress = new Progress<MeetingRecorder.Core.Stt.TranscriptionProgress>(p =>
        {
            TranscriptionProgress = p.Fraction;
            TranscriptionStatus =
                $"文字起こし中... {p.Fraction * 100:F0}%"
                + $"（{TimeSpan.FromSeconds(p.ProcessedSeconds):mm\\:ss} / {TimeSpan.FromSeconds(p.TotalSeconds):mm\\:ss}）";
        });

        var outcome = TranscriptionOutcome.Failed;
        try
        {
            var previous = _transcript.Snapshot();
            var token = _transcriptionCts.Token;
            var language = _services.Settings.SttLanguage;
            var sources = meeting.TranscriptionSources;

            var segments = await Task.Run(() => new OfflineTranscriptionService(_services.Logger).Transcribe(
                sources,
                recognizer,
                language,
                previous,
                progress,
                diarizer,
                token));

            // Replacing rather than merging: a transcription produces its own
            // segmentation, and interleaving two of them would read as neither.
            _transcript.ReplaceAll(segments);

            meeting.Metadata.SttModelId = recognizer.ModelId;
            meeting.Metadata.DiarizationUsed = segments.Any(s => s.SpeakerId.HasValue);
            meeting.SaveTranscript(_transcript.Snapshot(), _transcript.SpeakerNames);

            outcome = TranscriptionOutcome.Completed;
            WorkflowState = MeetingWorkflowState.TranscriptReady;
            TranscriptionStatus = $"文字起こしが完了しました（{segments.Count} 件）。";
            StatusMessage = $"{TranscriptionStatus} 保存先: {meeting.Folder.Path}";
            RefreshSpeakers();
        }
        catch (OperationCanceledException)
        {
            // Cancelling costs the time already spent and nothing else. The
            // audio - both the recording and the working copy - is untouched.
            outcome = TranscriptionOutcome.Cancelled;
            TranscriptionStatus = "文字起こしを中止しました。音声はそのまま残っています。";
            StatusMessage = TranscriptionStatus;
            WorkflowState = Transcript.Count > 0 ? MeetingWorkflowState.TranscriptReady : MeetingWorkflowState.AudioReady;
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Transcription failed.", ex);
            TranscriptionStatus = $"文字起こしに失敗しました（{ex.Message}）。音声はそのまま残っています。";
            StatusMessage = TranscriptionStatus;
            WorkflowState = Transcript.Count > 0 ? MeetingWorkflowState.TranscriptReady : MeetingWorkflowState.AudioReady;
            MessageBox.Show(
                $"文字起こしに失敗しました。\n録音した音声は保存先フォルダーにそのまま残っています。\n\n{ex.Message}",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            recognizer.Dispose();
            diarizer?.Dispose();
            _transcriptionCts?.Dispose();
            _transcriptionCts = null;
            TranscriptionProgress = 0;

            // Only a completed transcription may delete its own input. A failure
            // or a cancellation leaves it, because the obvious next step is to
            // try again - possibly with a different model.
            RecognitionAudioCleanup.DeleteIfAppropriate(
                meeting.RecognitionAudioDirectory ?? meeting.Folder.Path,
                outcome,
                _services.Settings.DeleteRecognitionAudioAfterTranscription,
                _services.Logger);

            RefreshAudioSummary();
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// Re-creates the working audio for an imported meeting whose copy was
    /// deleted after a previous transcription.
    /// </summary>
    private async Task<bool> TryRebuildWorkingAudioAsync(MeetingSummary meeting)
    {
        if (!CanRebuildWorkingAudio(meeting))
        {
            var message = meeting.IsImported
                ? "文字起こし用の音声が削除されており、元のファイルも見つかりません。"
                  + "元のファイルを同じ場所に戻すか、もう一度インポートしてください。"
                : "この録音には文字起こし用の音声がありません。"
                  + "設定の「文字起こし用の音声を保存する」を有効にして録音した会議でのみ実行できます。";

            TranscriptionStatus = message;
            StatusMessage = message;
            MessageBox.Show(message, "MeetingRecorder", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        // Decoding an hour of audio is not instant, so it does not happen on the
        // UI thread.
        StatusMessage = "元のファイルから作業用音声を作り直しています...";
        IsBusy = true;

        string? reason;
        bool rebuilt;
        try
        {
            var settings = _services.Settings;
            var result = await Task.Run(() =>
            {
                var ok = _services.MeetingImporter.TryReimport(meeting, settings, out var failure);
                return (Ok: ok, Failure: failure);
            });

            rebuilt = result.Ok;
            reason = result.Failure;
        }
        finally
        {
            IsBusy = false;
        }

        if (rebuilt)
        {
            return true;
        }

        TranscriptionStatus = reason ?? "作業用音声を作り直せませんでした。";
        StatusMessage = TranscriptionStatus;
        MessageBox.Show(TranscriptionStatus, "MeetingRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>
    /// Says what audio is loaded and whether it can (still) be transcribed, so
    /// the relationship between the delete setting and re-transcribing is
    /// visible rather than discovered.
    /// </summary>
    private void RefreshAudioSummary()
    {
        var meeting = _meeting;
        if (meeting is null)
        {
            AudioSummaryText = "録音またはインポートした音声はまだありません。";
            return;
        }

        var what = meeting.IsImported
            ? $"インポート: {Path.GetFileName(meeting.Metadata.AudioFileName)}"
            : $"録音: {meeting.Folder.Name}";

        string transcription;
        if (meeting.CanTranscribe)
        {
            transcription = meeting.HasSourceAttribution
                ? "文字起こし可能（マイク／PC音声を別々に保持）"
                : "文字起こし可能（1系統・話者は判別しません）";
        }
        else if (CanRebuildWorkingAudio(meeting))
        {
            transcription = "作業用音声は削除済み。元のファイルから作り直して再実行できます";
        }
        else if (meeting.IsImported)
        {
            transcription = "作業用音声が削除され、元のファイルも見つかりません（再文字起こし不可）";
        }
        else
        {
            transcription = "作業用音声がないため文字起こしできません";
        }

        AudioSummaryText = $"{what} / {FormatDuration(meeting.Metadata.Duration)} / {transcription}";
    }

    /// <summary>Writes the finished recording wherever the user chooses.</summary>
    private void ExportAudio()
    {
        var source = LastAudioPath();
        if (source is null)
        {
            MessageBox.Show(
                "出力できる音声がありません。録音を停止するか、音声ファイルをインポートしてください。",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var extension = Path.GetExtension(source);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(source),
            DefaultExt = extension,
            Filter = extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                ? "MP3 音声 (*.mp3)|*.mp3|すべてのファイル (*.*)|*.*"
                : "WAV 音声 (*.wav)|*.wav|すべてのファイル (*.*)|*.*",
            Title = "音声の出力先",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // Copy rather than move: the meeting folder stays complete, and an
            // export that fails half way has not taken the original with it.
            File.Copy(source, dialog.FileName, overwrite: true);
            StatusMessage = $"音声を出力しました: {dialog.FileName}";
            _services.Logger.Info(nameof(MainViewModel), "Exported the recording on request.");
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Exporting the recording failed.", ex);
            MessageBox.Show(
                $"音声の出力に失敗しました。\n元のデータは保存先フォルダーに残っています。\n\n{ex.Message}",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ---- Transcript view -------------------------------------------------

    private void ClearTranscript()
    {
        _transcript.Clear();
        foreach (var item in _itemsById.Values)
        {
            item.TextEdited -= OnItemTextEdited;
        }

        _itemsById.Clear();
        Transcript.Clear();
        Speakers.Clear();
        TranscriptionStatus = string.Empty;
        TranscriptionProgress = 0;
    }

    private void OnSegmentAdded(TranscriptSegment segment)
    {
        Dispatch(() =>
        {
            var item = new TranscriptItemViewModel(segment);
            item.TextEdited += OnItemTextEdited;
            _itemsById[segment.Id] = item;
            Transcript.Add(item);

            if (segment.SpeakerId.HasValue && !Speakers.Any(s => s.Source == segment.Source && s.SpeakerId == segment.SpeakerId))
            {
                Speakers.Add(new SpeakerViewModel(segment.Source, segment.SpeakerId.Value, segment.SpeakerName));
                RenameSpeakerCommand.RaiseCanExecuteChanged();
            }

            SaveTranscriptCommand.RaiseCanExecuteChanged();
            GenerateMinutesCommand.RaiseCanExecuteChanged();
        });
    }

    private void OnSegmentRemoved(TranscriptSegment segment)
    {
        Dispatch(() =>
        {
            if (_itemsById.Remove(segment.Id, out var item))
            {
                item.TextEdited -= OnItemTextEdited;
                Transcript.Remove(item);
            }
        });
    }

    private void OnSegmentChanged(TranscriptSegment segment)
    {
        Dispatch(() =>
        {
            if (_itemsById.TryGetValue(segment.Id, out var item))
            {
                item.Refresh(segment);
            }
        });
    }

    /// <summary>
    /// Runs on the UI thread when there is one, and inline when there is not.
    /// </summary>
    /// <remarks>
    /// Transcript events now arrive on a thread-pool thread rather than a
    /// transcription worker, but the requirement is the same and so is the
    /// answer. The inline path keeps the view model usable in a test host with
    /// no <see cref="Application"/>.
    /// </remarks>
    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    private void OnItemTextEdited(object? sender, string text)
    {
        if (sender is TranscriptItemViewModel item)
        {
            _transcript.EditText(item.Id, text);
        }
    }

    private void OnCaptureFault(object? sender, CaptureFault fault)
    {
        Dispatch(() => WarningText = fault.Message);
    }

    private void RefreshSpeakers()
    {
        Dispatch(() =>
        {
            Speakers.Clear();
            foreach (var (source, speakerId, name) in _transcript.Speakers())
            {
                Speakers.Add(new SpeakerViewModel(source, speakerId, name));
            }

            OnPropertyChanged(nameof(CanRenameSpeakers));
            RenameSpeakerCommand.RaiseCanExecuteChanged();
        });
    }

    private void RenameSpeakers()
    {
        if (_meeting is null || Speakers.Count == 0)
        {
            return;
        }

        if (!_meeting.HasSourceAttribution)
        {
            MessageBox.Show(
                "インポートした音声は1つの音源しかないため、話者を特定できません。"
                + "話者名の編集は、このアプリで録音した会議（マイクとPC音声を別々に保持したもの）でのみ行えます。",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new SpeakerRenameWindow(Speakers.ToList())
        {
            Owner = Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var speaker in dialog.Speakers)
        {
            if (!string.IsNullOrWhiteSpace(speaker.DisplayName) && speaker.DisplayName != speaker.DefaultLabel)
            {
                _transcript.RenameSpeaker(speaker.Source, speaker.SpeakerId, speaker.DisplayName.Trim());
            }
        }

        SaveTranscript();
        StatusMessage = "話者名を反映し、文字起こしを保存しました。";
    }

    private void SaveTranscript()
    {
        try
        {
            _meeting?.SaveTranscript(_transcript.Snapshot(), _transcript.SpeakerNames);
            StatusMessage = "文字起こしを保存しました。";
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Saving the transcript failed.", ex);
            MessageBox.Show($"文字起こしの保存に失敗しました。\n\n{ex.Message}", "MeetingRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- Minutes ---------------------------------------------------------

    private async Task GenerateMinutesAsync()
    {
        var meeting = _meeting;
        var segments = _transcript.Snapshot();

        if (meeting is null || segments.Count == 0)
        {
            // Minutes are made from a transcript, so there has to be one. A
            // recording on its own is not enough and the command is disabled
            // until it is transcribed.
            MessageBox.Show(
                "議事録は文字起こしの結果から作成します。先に「文字起こし」を実行してください。",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        StatusMessage = "議事録を生成しています（ローカル処理のみ）...";

        IMinutesGenerator? generator = null;
        try
        {
            var request = new MinutesRequest(meeting.Metadata, segments);

            generator = CreateMinutesGenerator();
            var progress = new Progress<MinutesProgress>(p =>
            {
                StatusMessage = p.Fraction is { } fraction
                    ? $"議事録生成中: {p.Stage} ({fraction * 100:F0}%)"
                    : $"議事録生成中: {p.Stage}";
            });

            var document = await generator.GenerateAsync(request, progress);

            await File.WriteAllTextAsync(meeting.Folder.MinutesMarkdownPath, document.Markdown, new System.Text.UTF8Encoding(true));
            await File.WriteAllTextAsync(meeting.Folder.MinutesTextPath, document.PlainText, new System.Text.UTF8Encoding(true));

            meeting.Metadata.MinutesGenerated = true;
            meeting.Save();

            StatusMessage = $"議事録を保存しました（{document.GeneratorName}）: {meeting.Folder.MinutesMarkdownPath}";

            if (document.Warnings.Count > 0)
            {
                WarningText = string.Join(" / ", document.Warnings);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Minutes generation failed.", ex);
            MessageBox.Show($"議事録の生成に失敗しました。\n\n{ex.Message}", "MeetingRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusMessage = "議事録の生成に失敗しました。";
        }
        finally
        {
            generator?.Dispose();
            IsBusy = false;
        }
    }

    private IMinutesGenerator CreateMinutesGenerator()
    {
        var settings = _services.Settings;
        if (!settings.MinutesEnabled || settings.Profile?.MinutesEnabled == false)
        {
            return new ExtractiveMinutesGenerator();
        }

        var descriptor = ModelCatalog.Find(settings.LlmModelId);
        if (descriptor is null || !_services.ModelStore.IsPresent(descriptor))
        {
            return new ExtractiveMinutesGenerator();
        }

        return new LocalLlmMinutesGenerator(
            _services.ModelStore.PathFor(descriptor),
            descriptor.DisplayName,
            settings.Profile?.SttThreads ?? 4,
            logger: _services.Logger);
    }

    // ---- Window plumbing -------------------------------------------------

    private void OpenSettings()
    {
        _services.LevelMonitor.Stop();

        var window = new SettingsWindow(_services) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();

        RefreshSettingsDisplay();
        _services.LevelMonitor.Start(_services.Settings);
    }

    private void OpenSaveFolder()
    {
        var path = _meeting?.Folder.Path ?? _services.Settings.SaveRoot;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Logger.Warn(nameof(MainViewModel), $"Could not open '{path}': {ex.Message}");
        }
    }

    private void RaiseCommandStates()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ImportAudioCommand.RaiseCanExecuteChanged();
        OpenSettingsCommand.RaiseCanExecuteChanged();
        TranscribeCommand.RaiseCanExecuteChanged();
        CancelTranscriptionCommand.RaiseCanExecuteChanged();
        GenerateMinutesCommand.RaiseCanExecuteChanged();
        SaveTranscriptCommand.RaiseCanExecuteChanged();
        RenameSpeakerCommand.RaiseCanExecuteChanged();
        ExportAudioCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanRenameSpeakers));
    }

    /// <summary>Called when the window is closing while a recording is running.</summary>
    public bool ConfirmClose()
    {
        if (IsTranscribing)
        {
            var answer = MessageBox.Show(
                "文字起こしの実行中です。終了すると中止されます。録音した音声は残ります。よろしいですか？",
                "MeetingRecorder",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }

            _transcriptionCts?.Cancel();
        }

        if (!IsRecording)
        {
            return ConfirmUnsavedRecordings();
        }

        var recordingAnswer = MessageBox.Show(
            "録音中です。終了すると録音を停止して保存します。よろしいですか？",
            "MeetingRecorder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (recordingAnswer != MessageBoxResult.Yes)
        {
            return false;
        }

        try
        {
            _session?.Stop();
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Stop-on-close failed; the journal will drive recovery.", ex);
        }

        return ConfirmUnsavedRecordings();
    }

    /// <summary>
    /// Points out recordings sitting in the working folder before the window
    /// closes on them. Nothing is deleted here: the app says where they are and
    /// lets the user decide, which is the whole reason automatic saving was
    /// turned off.
    /// </summary>
    private bool ConfirmUnsavedRecordings()
    {
        try
        {
            var root = AppPaths.WorkingRecordingRoot;
            if (!Directory.Exists(root))
            {
                return true;
            }

            var pending = Directory.GetDirectories(root).Length;
            if (pending == 0)
            {
                return true;
            }

            var answer = MessageBox.Show(
                $"自動保存がオフのため、{pending} 件の録音が作業用フォルダーに残っています。\n"
                + $"{root}\n\n"
                + "このまま終了しますか？（データは削除されません。次回起動後も「音声を出力...」で保存できます）",
                "MeetingRecorder",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            return answer == MessageBoxResult.Yes;
        }
        catch (Exception ex)
        {
            // Never block shutdown over a bookkeeping check.
            _services.Logger.Warn(nameof(MainViewModel), $"Could not check for unsaved recordings: {ex.Message}");
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _transcriptionCts?.Cancel();
        _transcriptionCts?.Dispose();
        _session?.Dispose();
    }
}
