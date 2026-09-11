using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Pipeline;

public enum PreflightSeverity
{
    Ok,

    /// <summary>Recording can start, but something will be missing or degraded.</summary>
    Warning,

    /// <summary>Recording must not start.</summary>
    Error,
}

public enum PreflightCheck
{
    Microphone,
    SystemAudio,
    SaveLocation,
    DiskSpace,
    SpeechModel,
}

/// <param name="Check">Which condition was evaluated.</param>
/// <param name="Severity">Whether recording can proceed.</param>
/// <param name="Title">Short label, e.g. "マイク".</param>
/// <param name="Detail">What was found.</param>
/// <param name="Remedy">What the user should do about it. Never empty for a warning or error.</param>
public sealed record PreflightResult(
    PreflightCheck Check,
    PreflightSeverity Severity,
    string Title,
    string Detail,
    string Remedy)
{
    public static PreflightResult Ok(PreflightCheck check, string title, string detail)
        => new(check, PreflightSeverity.Ok, title, detail, string.Empty);
}

/// <summary>
/// Runs the checks the requirements demand before recording starts, and - just
/// as importantly - explains what to do about anything that fails. A bare
/// "Error" dialog is explicitly forbidden.
/// </summary>
public sealed class PreflightChecker
{
    /// <summary>
    /// 48 kHz / 16-bit mono is 96 kB/s, so two hours is ~692 MB. Requiring 2 GB
    /// free leaves room for the WAV, the MP3 transcode (which needs both files on
    /// disk at once) and Windows itself.
    /// </summary>
    public const long RecommendedFreeBytes = 2L * 1024 * 1024 * 1024;

    public const long MinimumFreeBytes = 500L * 1024 * 1024;

    private readonly IAudioCaptureFactory _captureFactory;
    private readonly ModelStore _modelStore;
    private readonly ILogger _logger;

    public PreflightChecker(IAudioCaptureFactory captureFactory, ModelStore modelStore, ILogger? logger = null)
    {
        _captureFactory = captureFactory;
        _modelStore = modelStore;
        _logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<PreflightResult> Run(AppSettings settings)
    {
        var results = new List<PreflightResult>
        {
            CheckCapture(
                PreflightCheck.Microphone,
                "マイク",
                () => _captureFactory.CreateMicrophoneCapture(settings.MicrophoneDeviceId, 48000),
                "Windowsの「設定 > システム > サウンド > 入力」でマイクが有効か、"
                + "「プライバシーとセキュリティ > マイク」でデスクトップアプリのマイク使用が許可されているか確認してください。"),

            CheckCapture(
                PreflightCheck.SystemAudio,
                "PC内部音声 (WASAPIループバック)",
                () => _captureFactory.CreateSystemAudioCapture(settings.RenderDeviceId, 48000),
                "再生デバイス（スピーカー／ヘッドセット）が接続され、Windowsの既定の再生デバイスとして有効になっているか確認してください。"
                + "Bluetoothヘッドセットがハンズフリー(HFP)モードの場合は、ステレオ(A2DP)側を選択してください。"),

            CheckSaveLocation(settings),
            CheckDiskSpace(settings),
            CheckSpeechModel(settings),
        };

        return results;
    }

    private PreflightResult CheckCapture(PreflightCheck check, string title, Func<IAudioCaptureSource> factory, string remedy)
    {
        try
        {
            using var source = factory();
            return PreflightResult.Ok(check, title, $"利用可能: {source.DeviceName}");
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(PreflightChecker), $"{title} check failed: {ex.Message}");
            return new PreflightResult(
                check,
                PreflightSeverity.Error,
                title,
                $"取得できません: {ex.Message}",
                remedy);
        }
    }

    private PreflightResult CheckSaveLocation(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SaveRoot))
        {
            return new PreflightResult(
                PreflightCheck.SaveLocation,
                PreflightSeverity.Error,
                "保存先",
                "保存先が設定されていません。",
                "設定画面で録音データの保存先フォルダーを選択してください。");
        }

        if (!AppPathsCanWrite(settings.SaveRoot!))
        {
            return new PreflightResult(
                PreflightCheck.SaveLocation,
                PreflightSeverity.Error,
                "保存先",
                $"書き込みできません: {settings.SaveRoot}",
                "書き込み権限のあるフォルダー（例: ドキュメント配下）を選び直してください。"
                + "OneDrive同期フォルダーやネットワークドライブは、切断時に録音が失敗することがあります。");
        }

        return PreflightResult.Ok(PreflightCheck.SaveLocation, "保存先", settings.SaveRoot!);
    }

    private static bool AppPathsCanWrite(string path) => Persistence.AppPaths.CanWrite(path);

    private PreflightResult CheckDiskSpace(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SaveRoot))
        {
            return new PreflightResult(
                PreflightCheck.DiskSpace,
                PreflightSeverity.Warning,
                "空き容量",
                "保存先が未設定のため確認できません。",
                "設定画面で保存先フォルダーを選択すると空き容量を確認できます。"
                + RecordingFootprint.Describe(settings));
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(settings.SaveRoot!));
            if (string.IsNullOrEmpty(root))
            {
                return PreflightResult.Ok(PreflightCheck.DiskSpace, "空き容量", "確認できません（ネットワークパス）。");
            }

            var drive = new DriveInfo(root);
            var free = drive.AvailableFreeSpace;

            // Everything the recording writes, not just the meeting file: the
            // per-stream working audio is another 64 kB/s while it is enabled,
            // and quoting the meeting file alone promised 1.67x the hours the
            // drive could actually take.
            var hours = RecordingFootprint.HoursThatFit(settings, free);

            if (free < MinimumFreeBytes)
            {
                return new PreflightResult(
                    PreflightCheck.DiskSpace,
                    PreflightSeverity.Error,
                    "空き容量",
                    $"空き容量が不足しています ({free / 1024.0 / 1024.0:F0} MB)。",
                    "不要なファイルを削除するか、別のドライブを保存先に指定してください。"
                    + RecordingFootprint.Describe(settings));
            }

            if (free < RecommendedFreeBytes)
            {
                return new PreflightResult(
                    PreflightCheck.DiskSpace,
                    PreflightSeverity.Warning,
                    "空き容量",
                    $"空き容量が少なめです ({free / 1024.0 / 1024.0 / 1024.0:F1} GB / 約{hours:F1}時間分)。",
                    "長時間の会議を録音する場合は、空き容量を確保してください。"
                    + RecordingFootprint.Describe(settings));
            }

            return PreflightResult.Ok(
                PreflightCheck.DiskSpace,
                "空き容量",
                $"{free / 1024.0 / 1024.0 / 1024.0:F1} GB（約{hours:F0}時間分）");
        }
        catch (Exception ex)
        {
            return new PreflightResult(
                PreflightCheck.DiskSpace,
                PreflightSeverity.Warning,
                "空き容量",
                $"確認できませんでした: {ex.Message}",
                "保存先ドライブが接続されているか確認してください。");
        }
    }

    private PreflightResult CheckSpeechModel(AppSettings settings)
    {
        if (!settings.SttEnabled)
        {
            return PreflightResult.Ok(PreflightCheck.SpeechModel, "音声認識モデル", "文字起こしは無効に設定されています（録音のみ実行します）。");
        }

        var modelId = settings.Profile?.SttModelId ?? settings.SttModelId;
        var descriptor = ModelCatalog.Find(modelId);
        if (descriptor is null)
        {
            return new PreflightResult(
                PreflightCheck.SpeechModel,
                PreflightSeverity.Warning,
                "音声認識モデル",
                "使用するモデルが決まっていません。",
                "設定画面を開くと自動判定が実行されます。モデル未取得のまま録音を開始した場合、"
                + "録音は正常に行われます。文字起こしは、モデルを取得したあとで「文字起こし」ボタンからいつでも実行できます。");
        }

        if (!_modelStore.IsPresent(descriptor))
        {
            return new PreflightResult(
                PreflightCheck.SpeechModel,
                PreflightSeverity.Warning,
                "音声認識モデル",
                $"「{descriptor.DisplayName}」({descriptor.SizeDisplay}) が未取得です。",
                "設定画面からモデルをダウンロードしてください。未取得のままでも録音は実行できます"
                + "（文字起こしのみ行われません）。");
        }

        return PreflightResult.Ok(PreflightCheck.SpeechModel, "音声認識モデル", descriptor.DisplayName);
    }
}
