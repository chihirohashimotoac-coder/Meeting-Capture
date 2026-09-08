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

    /// <summary>
    /// Loads the recognizer for the currently selected model, or returns null
    /// when no model is available. Never fabricates a recognizer: a missing
    /// model means "record without a transcript", not "show made-up text".
    /// </summary>
    public ISpeechRecognizer? TryCreateRecognizer(out string? reason)
    {
        reason = null;

        if (!Settings.SttEnabled)
        {
            reason = "設定で文字起こしが無効になっています。";
            return null;
        }

        var modelId = Settings.Profile?.SttModelId ?? Settings.SttModelId;
        var descriptor = ModelCatalog.Find(modelId);
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
            var threads = Settings.Profile?.SttThreads ?? CapabilityProbe.RecommendThreadCount(Environment.ProcessorCount);
            var options = SpeechRecognitionOptions.Live(threads, Settings.SttLanguage);
            return new WhisperSpeechRecognizer(ModelStore.PathFor(descriptor), descriptor.Id, options, Logger);
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(AppServices), "Loading the speech model failed.", ex);
            reason = $"音声認識モデルの読み込みに失敗しました（{ex.Message}）。録音のみ実行します。";
            return null;
        }
    }

    /// <summary>
    /// The model the second pass should use, or null when this machine has
    /// nothing better than the live one.
    /// </summary>
    public ModelDescriptor? RefinementModel()
    {
        var explicitId = Settings.RefinementModelId ?? Settings.Profile?.RefinementModelId;
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            return ModelCatalog.Find(explicitId);
        }

        return null;
    }

    /// <summary>
    /// Creates the recognizer for the second pass. Separate from
    /// <see cref="TryCreateRecognizer"/> because it decodes differently: a wider
    /// beam and whisper's temperature fallback, neither of which the live pass
    /// can afford.
    /// </summary>
    public ISpeechRecognizer? TryCreateRefinementRecognizer(out string? reason)
    {
        reason = null;

        var descriptor = RefinementModel();
        if (descriptor is null)
        {
            reason = "このPCの性能では、速報より高精度なモデルを使えません。";
            return null;
        }

        if (!ModelStore.IsPresent(descriptor))
        {
            reason = $"高精度モデル「{descriptor.DisplayName}」が未取得です。設定画面からダウンロードしてください。";
            return null;
        }

        try
        {
            // More threads than the live pass: nothing is competing for the CPU
            // once recording has stopped.
            var threads = Math.Max(
                Settings.Profile?.SttThreads ?? 4,
                CapabilityProbe.RecommendThreadCount(Environment.ProcessorCount));

            var options = SpeechRecognitionOptions.Accurate(threads, Settings.SttLanguage);
            return new WhisperSpeechRecognizer(ModelStore.PathFor(descriptor), descriptor.Id, options, Logger);
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(AppServices), "Loading the refinement model failed.", ex);
            reason = $"高精度モデルの読み込みに失敗しました（{ex.Message}）。";
            return null;
        }
    }

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
