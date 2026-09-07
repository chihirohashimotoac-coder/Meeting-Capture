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

/// <summary>View model behind the main window.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TranscriptItemViewModel> _itemsById = new(StringComparer.Ordinal);
    private readonly Stopwatch _tickClock = Stopwatch.StartNew();

    private MeetingSessionManager? _session;
    private ISpeechRecognizer? _recognizer;
    private ISpeakerDiarizer? _diarizer;
    private MeetingSummary? _lastSummary;
    private TimeSpan _lastTick;

    private string _stateText = "待機中";
    private string _elapsedText = "00:00:00";
    private double _micLevel;
    private double _systemLevel;
    private double _micPeakLevel;
    private double _systemPeakLevel;
    private string _micLevelText = "-- dBFS";
    private string _systemLevelText = "-- dBFS";
    private string _sttLatencyText = "文字起こし遅延: -";
    private string _saveRootText = string.Empty;
    private string _modelText = string.Empty;
    private string _statusMessage = string.Empty;
    private string _diagnosticsText = string.Empty;
    private string _meetingTitle = string.Empty;
    private string _warningText = string.Empty;
    private bool _isRecording;
    private bool _isBusy;
    private bool _disposed;

    public MainViewModel(AppServices services)
    {
        _services = services;

        StartCommand = new AsyncRelayCommand(StartRecordingAsync, () => !IsRecording && !IsBusy);
        StopCommand = new AsyncRelayCommand(StopRecordingAsync, () => IsRecording && !IsBusy);
        OpenSettingsCommand = new RelayCommand(OpenSettings, () => !IsRecording);
        OpenFolderCommand = new RelayCommand(OpenSaveFolder);
        GenerateMinutesCommand = new AsyncRelayCommand(GenerateMinutesAsync, () => !IsRecording && !IsBusy && _lastSummary is not null);
        SaveTranscriptCommand = new RelayCommand(SaveTranscript, () => _session is not null);
        RenameSpeakerCommand = new RelayCommand(RenameSpeakers, () => Speakers.Count > 0);

        RefreshSettingsDisplay();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // 10 Hz: smooth enough for level meters, cheap enough to be invisible
            // in the CPU graph next to transcription.
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += OnTimerTick;
    }

    public ObservableCollection<TranscriptItemViewModel> Transcript { get; } = new();

    public ObservableCollection<SpeakerViewModel> Speakers { get; } = new();

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public AsyncRelayCommand GenerateMinutesCommand { get; }

    public RelayCommand SaveTranscriptCommand { get; }

    public RelayCommand RenameSpeakerCommand { get; }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
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

    public string SttLatencyText
    {
        get => _sttLatencyText;
        private set => SetProperty(ref _sttLatencyText, value);
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

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RaiseCommandStates();
            }
        }
    }

    public bool IsIdle => !IsRecording;

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

        var modelId = _services.Settings.Profile?.SttModelId ?? _services.Settings.SttModelId;
        var descriptor = ModelCatalog.Find(modelId);

        if (!_services.Settings.SttEnabled)
        {
            ModelText = "文字起こし: 無効（録音のみ）";
        }
        else if (descriptor is null)
        {
            ModelText = "モデル: 未選択";
        }
        else
        {
            var present = _services.ModelStore.IsPresent(descriptor);
            ModelText = present
                ? $"モデル: {descriptor.DisplayName}"
                : $"モデル: {descriptor.DisplayName}（未取得）";
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var now = _tickClock.Elapsed;
        var delta = now - _lastTick;
        _lastTick = now;

        if (IsRecording && _session is not null)
        {
            var status = _session.GetStatus();
            UpdateFromStatus(status);
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

        var stt = status.Stt;
        SttLatencyText = stt.QueuedChunks == 0 && stt.ProcessedChunks == 0
            ? "文字起こし遅延: -"
            : $"文字起こし処理遅延: {stt.LatencySeconds:F0}秒（未処理 {stt.QueuedChunks} 件 / {stt.QueuedSeconds:F0}秒）";

        var resources = _services.PerformanceMonitor.Sample();
        DiagnosticsText =
            $"CPU {resources.CpuPercent:F0}% / RAM {resources.WorkingSetMb:F0}MB / " +
            $"RTF {(stt.RealTimeFactor > 0 ? stt.RealTimeFactor.ToString("F2") : "-")} / " +
            $"バックログ {stt.QueuedSeconds:F0}s / ディスク退避 {stt.SpilledChunks} / " +
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
            if (!PreflightDialog.Confirm(Application.Current.MainWindow, results))
            {
                StatusMessage = "録音を開始しませんでした。";
                _services.LevelMonitor.Start(_services.Settings);
                return;
            }

            _recognizer = _services.TryCreateRecognizer(out var reason);
            if (_recognizer is null && !string.IsNullOrWhiteSpace(reason))
            {
                WarningText = reason!;
            }

            _diarizer = _services.TryCreateDiarizer();

            // Release the previous meeting's session before starting another.
            _session?.Dispose();

            _session = new MeetingSessionManager(
                _services.CaptureFactory,
                _services.Transcoder,
                _services.SleepPreventer,
                _services.Logger);

            _session.Transcript.SegmentAdded += OnSegmentAdded;
            _session.Transcript.SegmentChanged += OnSegmentChanged;
            _session.Fault += OnCaptureFault;

            Transcript.Clear();
            Speakers.Clear();
            _itemsById.Clear();

            var title = string.IsNullOrWhiteSpace(MeetingTitle) ? null : MeetingTitle.Trim();
            var folder = _session.Start(_services.Settings, _recognizer, _diarizer, title);

            IsRecording = true;
            StateText = "録音中";
            StatusMessage = $"録音中: {folder.Path}";
            _lastSummary = null;
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Starting the recording failed.", ex);
            MessageBox.Show(
                $"録音を開始できませんでした。\n\n{ex.Message}",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            StateText = "待機中";
            IsRecording = false;
            _services.LevelMonitor.Start(_services.Settings);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopRecordingAsync()
    {
        if (_session is null)
        {
            return;
        }

        IsBusy = true;
        StateText = "停止処理中";
        StatusMessage = "録音を停止し、残りの文字起こしを処理しています...";

        try
        {
            var session = _session;
            var summary = await Task.Run(() => session.Stop(TimeSpan.FromMinutes(10)));
            _lastSummary = summary;

            IsRecording = false;
            StateText = "待機中";
            ElapsedText = FormatDuration(summary.Metadata.Duration);
            StatusMessage = $"保存しました: {summary.Folder.Path}";

            if (summary.Warnings.Count > 0)
            {
                WarningText = string.Join(" / ", summary.Warnings);
            }

            RefreshSpeakers();
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Stopping the recording failed.", ex);
            MessageBox.Show(
                $"録音の停止処理でエラーが発生しました。\n録音データは保存先フォルダーに残っています。\n\n{ex.Message}",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _recognizer?.Dispose();
            _recognizer = null;
            _diarizer?.Dispose();
            _diarizer = null;

            IsBusy = false;
            IsRecording = false;
            _services.LevelMonitor.Start(_services.Settings);
        }
    }

    private void OnSegmentAdded(TranscriptSegment segment)
    {
        // Raised on the transcription worker thread.
        Application.Current?.Dispatcher.BeginInvoke(() =>
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
        });
    }

    private void OnSegmentChanged(TranscriptSegment segment)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_itemsById.TryGetValue(segment.Id, out var item))
            {
                item.Refresh(segment);
            }
        });
    }

    private void OnItemTextEdited(object? sender, string text)
    {
        if (sender is TranscriptItemViewModel item)
        {
            _session?.Transcript.EditText(item.Id, text);
        }
    }

    private void OnCaptureFault(object? sender, CaptureFault fault)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            WarningText = fault.Message;
        });
    }

    private void RefreshSpeakers()
    {
        if (_session is null)
        {
            return;
        }

        Speakers.Clear();
        foreach (var (source, speakerId, name) in _session.Transcript.Speakers())
        {
            Speakers.Add(new SpeakerViewModel(source, speakerId, name));
        }

        RenameSpeakerCommand.RaiseCanExecuteChanged();
    }

    private void RenameSpeakers()
    {
        if (_session is null || Speakers.Count == 0)
        {
            return;
        }

        var dialog = new SpeakerRenameWindow(Speakers.ToList())
        {
            Owner = Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var speaker in dialog.Speakers)
        {
            if (!string.IsNullOrWhiteSpace(speaker.DisplayName) && speaker.DisplayName != speaker.DefaultLabel)
            {
                _session.Transcript.RenameSpeaker(speaker.Source, speaker.SpeakerId, speaker.DisplayName.Trim());
            }
        }

        _session.SaveTranscript();
        StatusMessage = "話者名を反映し、文字起こしを保存しました。";
    }

    private void SaveTranscript()
    {
        try
        {
            _session?.SaveTranscript();
            StatusMessage = "文字起こしを保存しました。";
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Saving the transcript failed.", ex);
            MessageBox.Show($"文字起こしの保存に失敗しました。\n\n{ex.Message}", "MeetingRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task GenerateMinutesAsync()
    {
        if (_lastSummary is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "議事録を生成しています（ローカル処理のみ）...";

        IMinutesGenerator? generator = null;
        try
        {
            var segments = _session?.Transcript.Snapshot() ?? _lastSummary.Segments;
            var request = new MinutesRequest(_lastSummary.Metadata, segments);

            generator = CreateMinutesGenerator();
            var progress = new Progress<MinutesProgress>(p =>
            {
                StatusMessage = p.Fraction is { } fraction
                    ? $"議事録生成中: {p.Stage} ({fraction * 100:F0}%)"
                    : $"議事録生成中: {p.Stage}";
            });

            var document = await generator.GenerateAsync(request, progress);

            await File.WriteAllTextAsync(_lastSummary.Folder.MinutesMarkdownPath, document.Markdown, new System.Text.UTF8Encoding(true));
            await File.WriteAllTextAsync(_lastSummary.Folder.MinutesTextPath, document.PlainText, new System.Text.UTF8Encoding(true));

            _lastSummary.Metadata.MinutesGenerated = true;
            _lastSummary.Folder.WriteMetadata(_lastSummary.Metadata);

            StatusMessage = $"議事録を保存しました（{document.GeneratorName}）: {_lastSummary.Folder.MinutesMarkdownPath}";

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

    private void OpenSettings()
    {
        _services.LevelMonitor.Stop();

        var window = new SettingsWindow(_services) { Owner = Application.Current.MainWindow };
        window.ShowDialog();

        RefreshSettingsDisplay();
        _services.LevelMonitor.Start(_services.Settings);
    }

    private void OpenSaveFolder()
    {
        var path = _lastSummary?.Folder.Path ?? _services.Settings.SaveRoot;
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
        OpenSettingsCommand.RaiseCanExecuteChanged();
        GenerateMinutesCommand.RaiseCanExecuteChanged();
        SaveTranscriptCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Called when the window is closing while a recording is running.</summary>
    public bool ConfirmClose()
    {
        if (!IsRecording)
        {
            return true;
        }

        var answer = MessageBox.Show(
            "録音中です。終了すると録音を停止して保存します。よろしいですか？",
            "MeetingRecorder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        try
        {
            _session?.Stop(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(MainViewModel), "Stop-on-close failed; the journal will drive recovery.", ex);
        }

        return true;
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
        _session?.Dispose();
        _recognizer?.Dispose();
        _diarizer?.Dispose();
    }
}
