using MeetingRecorder.Audio;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Benchmark;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Pipeline;
using MeetingRecorder.Core.Stt;
using MeetingRecorder.Diarization;
using MeetingRecorder.Stt;

namespace MeetingRecorder.App.Services;

/// <summary>
/// Composition root. One place that decides which concrete implementation each
/// interface gets, so the rest of the application never news up a Windows type.
/// </summary>
public sealed class AppServices : IDisposable
{
    private bool _disposed;

    public AppServices()
    {
        Logger = new AppLogger(AppPaths.LogDirectory);
        SettingsStore = new SettingsStore(AppPaths.SettingsFile, Logger);
        Settings = SettingsStore.Load();

        Settings.ModelDirectory ??= AppPaths.ModelDirectory;
        Settings.SaveRoot ??= AppPaths.DefaultSaveRoot();

        ModelStore = new ModelStore(Settings.ModelDirectory!);
        ModelDownloader = new ModelDownloader(ModelStore, Logger);
        CaptureFactory = new WasapiCaptureFactory(Logger);
        DeviceProvider = new WasapiDeviceProvider(Logger);
        Transcoder = new MediaFoundationTranscoder(Logger);
        SleepPreventer = new WindowsSleepPreventer(Logger);
        PreflightChecker = new PreflightChecker(CaptureFactory, ModelStore, Logger);

        // WAV is decoded by this project's own reader so that importing one
        // never depends on an operating-system codec; everything else goes
        // through the decoders Windows already ships.
        AudioDecoder = new CompositeAudioDecoder(new WavAudioDecoder(), new MediaFoundationAudioDecoder(Logger));
        MeetingImporter = new MeetingImporter(new AudioImportService(AudioDecoder, Logger), Logger);
        RecoveryService = new CrashRecoveryService(Logger);
        PerformanceMonitor = new PerformanceMonitor();
        LevelMonitor = new LevelMonitor(CaptureFactory, Logger);

        Logger.Info(nameof(AppServices), $"Started. Data root: {AppPaths.DataRoot} (portable: {AppPaths.IsPortable}).");
    }

    public AppLogger Logger { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; private set; }

    public ModelStore ModelStore { get; private set; }

    public ModelDownloader ModelDownloader { get; private set; }

    public IAudioCaptureFactory CaptureFactory { get; }

    public IAudioDeviceProvider DeviceProvider { get; }

    public IAudioTranscoder Transcoder { get; }

    public ISleepPreventer SleepPreventer { get; }

    public PreflightChecker PreflightChecker { get; }

    public IAudioDecoder AudioDecoder { get; }

    public MeetingImporter MeetingImporter { get; }

    public CrashRecoveryService RecoveryService { get; }

    public PerformanceMonitor PerformanceMonitor { get; }

    public LevelMonitor LevelMonitor { get; }

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);

        if (!string.Equals(ModelStore.Directory, Settings.ModelDirectory, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(Settings.ModelDirectory))
        {
            ModelStore = new ModelStore(Settings.ModelDirectory!);
            ModelDownloader = new ModelDownloader(ModelStore, Logger);
        }
    }

    /// <summary>
    /// Measures the machine and stores the resulting profile. Runs on a
    /// background thread; the user never chooses a "speed vs accuracy" preset.
    /// </summary>
    public PerformanceProfile MeasureAndStoreProfile(CancellationToken cancellationToken = default)
    {
        var capability = CapabilityProbe.Measure(cancellationToken: cancellationToken);
        var profile = ProfileSelector.Select(capability);

        Settings.Profile = profile;
        Settings.SttModelId ??= profile.SttModelId;
        Settings.LlmModelId ??= ProfileSelector.SelectTextGenerationModel(capability)?.Id;
        SaveSettings();

        Logger.Info(nameof(AppServices), $"Performance profile selected: {profile.Rationale}");
        return profile;
    }

    /// <summary>The model transcription will use, or null when none is selected.</summary>
    /// <remarks>
    /// One model, one setting. An explicit choice in the settings window wins
    /// over the automatic one, which is only a starting point.
    /// </remarks>
    public ModelDescriptor? TranscriptionModel()
        => ModelCatalog.Find(Settings.SttModelId ?? Settings.Profile?.SttModelId);

    /// <summary>
    /// Loads the recognizer for the currently selected model, or returns null
    /// when no model is available. Never fabricates a recognizer: a missing
    /// model means "you cannot transcribe yet", not "show made-up text".
    /// </summary>
    /// <remarks>
    /// Nothing calls this while a recording is running. It is reached from the
    /// transcription command and from nowhere else, which is what keeps model
    /// loading - hundreds of megabytes of file, and seconds of it - off the path
    /// between pressing "record" and the first sample landing on disk.
    /// </remarks>
    public ISpeechRecognizer? TryCreateTranscriptionRecognizer(out string? reason)
    {
        reason = null;

        if (!Settings.SttEnabled)
        {
            reason = "設定で文字起こしが無効になっています。";
            return null;
        }

        var descriptor = TranscriptionModel();
        if (descriptor is null)
        {
            reason = "使用する音声認識モデルが決まっていません。設定画面を開いてください。";
            return null;
        }

        if (!ModelStore.IsPresent(descriptor))
        {
            reason = $"音声認識モデル「{descriptor.DisplayName}」が未取得です。設定画面からダウンロードしてください。";
            return null;
        }

        try
        {
            // Nothing is competing for the CPU: there is no capture running that
            // this could disturb, so the recognizer gets the full recommended
            // thread count.
            var threads = Math.Max(
                Settings.Profile?.SttThreads ?? 4,
                CapabilityProbe.RecommendThreadCount(Environment.ProcessorCount));

            var options = SpeechRecognitionOptions.Offline(threads, Settings.SttLanguage);
            return new WhisperSpeechRecognizer(ModelStore.PathFor(descriptor), descriptor.Id, options, Logger);
        }
        catch (OutOfMemoryException ex)
        {
            Logger.Error(nameof(AppServices), "The speech model did not fit in memory.", ex);
            reason =
                $"「{descriptor.DisplayName}」を読み込むメモリが足りません。"
                + "他のアプリを閉じるか、設定画面でより小さいモデルを選んでください。";
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(AppServices), "Loading the speech model failed.", ex);
            reason = $"音声認識モデルの読み込みに失敗しました（{ex.Message}）。";
            return null;
        }
    }

    /// <summary>
    /// The speaker clusterer used during transcription, or null when it is off.
    /// </summary>
    /// <remarks>
    /// It runs inside <see cref="OfflineTranscriptionService"/>, after the
    /// recognizer, on a finished file - never during a recording. Clustering
    /// while capturing would put a second analysis on the machine at the one
    /// moment there is something irreplaceable to lose.
    /// </remarks>
    public ISpeakerDiarizer? TryCreateDiarizer()
    {
        if (Settings.Profile?.DiarizationEnabled != true || !Settings.DiarizationEnabled)
        {
            return null;
        }

        return new OnlineSpeakerDiarizer(logger: Logger);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LevelMonitor.Dispose();
        SleepPreventer.Dispose();
        Logger.Dispose();
    }
}
