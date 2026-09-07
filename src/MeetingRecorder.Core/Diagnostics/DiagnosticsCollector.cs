using System.Globalization;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Benchmark;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Pipeline;

namespace MeetingRecorder.Core.Diagnostics;

/// <summary>Environment facts the collector cannot determine on its own.</summary>
/// <param name="AppVersion">Product version string.</param>
/// <param name="OperatingSystem">OS description.</param>
/// <param name="ProcessArchitecture">x64 / arm64 etc.</param>
/// <param name="IsElevated">True when running as administrator.</param>
/// <param name="OutboundConnections">
/// Outbound TCP endpoints this process currently holds, if the host can report
/// them. <c>null</c> means "could not be determined" - which is reported as
/// such, never as "no connections".
/// </param>
public sealed record DiagnosticEnvironment(
    string AppVersion,
    string OperatingSystem,
    string ProcessArchitecture,
    bool IsElevated,
    IReadOnlyList<string>? OutboundConnections);

/// <summary>
/// Runs every check the application can perform on itself and produces a
/// <see cref="DiagnosticReport"/>.
/// </summary>
/// <remarks>
/// Platform neutral on purpose: the Windows specifics arrive through the same
/// interfaces the recorder uses, so the whole collector is exercised by the test
/// suite with synthetic devices, and the identical code runs against real WASAPI
/// endpoints on a user's machine.
/// </remarks>
public sealed class DiagnosticsCollector
{
    private readonly IAudioDeviceProvider _devices;
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly ModelStore _modelStore;
    private readonly ILogger _logger;

    public DiagnosticsCollector(
        IAudioDeviceProvider devices,
        IAudioCaptureFactory captureFactory,
        ModelStore modelStore,
        ILogger? logger = null)
    {
        _devices = devices;
        _captureFactory = captureFactory;
        _modelStore = modelStore;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <param name="settings">Saved settings, so the probe uses the endpoints the user actually picked.</param>
    /// <param name="environment">Host facts the collector cannot read itself.</param>
    /// <param name="captureSeconds">How long to listen on each endpoint. 0 skips the live probe.</param>
    public DiagnosticReport Collect(AppSettings settings, DiagnosticEnvironment environment, double captureSeconds = 5.0)
    {
        var report = new DiagnosticReport
        {
            AppVersion = environment.AppVersion,
            OperatingSystem = environment.OperatingSystem,
            ProcessArchitecture = environment.ProcessArchitecture,
            IsElevated = environment.IsElevated,
            LogicalCores = Environment.ProcessorCount,
            TotalRamGb = CapabilityProbe.GetTotalRamGb(),
            DataRoot = AppPaths.DataRoot,
            PortableMode = AppPaths.IsPortable,
        };

        AddDeviceChecks(report, settings);
        AddPreflight(report, settings);

        if (captureSeconds > 0)
        {
            AddCaptureProbes(report, settings, TimeSpan.FromSeconds(captureSeconds));
        }
        else
        {
            report.Checks.Add(DiagnosticCheck.NotTested(
                "D-04/05", "音声取得テスト", "--seconds 0 が指定されたため実行していません。"));
        }

        AddPerformance(report, settings);
        AddModels(report, settings);
        AddNetwork(report, environment);

        return report;
    }

    private void AddDeviceChecks(DiagnosticReport report, AppSettings settings)
    {
        var capture = SafeEnumerate(() => _devices.GetCaptureDevices(), "録音");
        report.Checks.Add(capture.Count == 0
            ? new DiagnosticCheck(
                "D-01", "録音デバイス (マイク)", DiagnosticStatus.Error,
                "利用可能な録音デバイスが 0 件です。",
                "マイクを接続し、「設定 > システム > サウンド > 入力」で有効になっているか確認してください。"
                + "また「プライバシーとセキュリティ > マイク」でデスクトップアプリのマイク使用が許可されているか確認してください。")
            : DiagnosticCheck.Ok(
                "D-01", "録音デバイス (マイク)",
                $"{capture.Count} 件: " + string.Join(" / ", capture.Select(Describe))));

        var render = SafeEnumerate(() => _devices.GetRenderDevices(), "再生");
        report.Checks.Add(render.Count == 0
            ? new DiagnosticCheck(
                "D-02", "再生デバイス (ループバック元)", DiagnosticStatus.Error,
                "利用可能な再生デバイスが 0 件です。",
                "PC内部音声は再生デバイスのループバックとして取得するため、スピーカーまたはヘッドセットが必要です。")
            : DiagnosticCheck.Ok(
                "D-02", "再生デバイス (ループバック元)",
                $"{render.Count} 件: " + string.Join(" / ", render.Select(Describe))));

        static string Describe(AudioDeviceInfo device) => device.IsDefault ? $"{device.Name}（既定）" : device.Name;
    }

    private IReadOnlyList<AudioDeviceInfo> SafeEnumerate(Func<IReadOnlyList<AudioDeviceInfo>> enumerate, string label)
    {
        try
        {
            return enumerate();
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(DiagnosticsCollector), $"Enumerating {label} devices failed.", ex);
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    private void AddPreflight(DiagnosticReport report, AppSettings settings)
    {
        var checker = new PreflightChecker(_captureFactory, _modelStore, _logger);
        foreach (var result in checker.Run(settings))
        {
            report.Checks.Add(new DiagnosticCheck(
                $"D-03/{result.Check}",
                $"録音開始前チェック: {result.Title}",
                result.Severity switch
                {
                    PreflightSeverity.Ok => DiagnosticStatus.Ok,
                    PreflightSeverity.Warning => DiagnosticStatus.Warning,
                    _ => DiagnosticStatus.Error,
                },
                result.Detail,
                result.Remedy));
        }
    }

    private void AddCaptureProbes(DiagnosticReport report, AppSettings settings, TimeSpan duration)
    {
        var probe = new CaptureProbe(_captureFactory, _logger);

        var mic = probe.Run(AudioSourceKind.Microphone, settings.MicrophoneDeviceId, duration);
        report.Checks.Add(BuildProbeCheck(
            "D-04", "マイク音声取得テスト", mic, duration,
            noSignalRemedy:
                "テスト中はマイクに向かって話してください。無音のままなら、マイクがミュートされていないか、"
                + "「プライバシーとセキュリティ > マイク」でデスクトップアプリのアクセスが許可されているかを確認してください。",
            noDataRemedy:
                "マイクからデータが届いていません。デバイスを接続し直し、Windowsのサウンド設定で既定の入力デバイスに設定してください。"));

        var system = probe.Run(AudioSourceKind.SystemAudio, settings.RenderDeviceId, duration);
        report.Checks.Add(BuildProbeCheck(
            "D-05", "PC内部音声取得テスト (WASAPIループバック)", system, duration,
            noSignalRemedy:
                "テスト中にPCから音を再生してください（YouTube等）。WASAPIループバックは再生音が無いとデータを返しません。",
            noDataRemedy:
                "再生デバイスからループバックデータが届いていません。テスト中に何か音声を再生したうえで再実行してください。"
                + "Bluetoothヘッドセットの場合、ハンズフリー(HFP)ではなくステレオ(A2DP)側を選択してください。"));
    }

    private static DiagnosticCheck BuildProbeCheck(
        string id,
        string title,
        CaptureProbeResult result,
        TimeSpan duration,
        string noSignalRemedy,
        string noDataRemedy)
    {
        if (!result.Opened)
        {
            return new DiagnosticCheck(id, title, DiagnosticStatus.Error,
                $"エンドポイントを開けませんでした: {result.Error}", noDataRemedy);
        }

        var expected = (long)(duration.TotalSeconds * 48000);
        var coverage = expected > 0 ? result.Samples * 100.0 / expected : 0;
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"デバイス「{result.DeviceName}」から {duration.TotalSeconds:F0} 秒間に {result.Samples:N0} サンプル受信 "
            + $"（想定の {coverage:F0}%）/ peak {result.PeakDb:F1} dBFS / RMS {result.RmsDb:F1} dBFS");

        if (result.Samples == 0)
        {
            return new DiagnosticCheck(id, title, DiagnosticStatus.Warning,
                detail + " — データがまったく届いていません。", noDataRemedy);
        }

        if (!result.HasSignal)
        {
            return new DiagnosticCheck(id, title, DiagnosticStatus.Warning,
                detail + " — データは届いていますが無音です。", noSignalRemedy);
        }

        return DiagnosticCheck.Ok(id, title, detail + " — 信号を検出しました。");
    }

    private static void AddPerformance(DiagnosticReport report, AppSettings settings)
    {
        var capability = CapabilityProbe.Measure();
        var profile = ProfileSelector.Select(capability);
        var model = ModelCatalog.RequireSpeechModel(profile.SttModelId);
        var estimated = ProfileSelector.EstimateRealTimeFactor(model, capability);

        var saved = settings.Profile is null
            ? "（保存済みプロファイルなし）"
            : $"（保存済み: {settings.Profile.SttModelId} / スレッド {settings.Profile.SttThreads}）";

        report.Checks.Add(new DiagnosticCheck(
            "D-06",
            "PC性能の実測と自動設定",
            estimated <= ProfileSelector.TargetRealTimeFactor ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
            string.Create(CultureInfo.InvariantCulture,
                $"実測スコア {capability.MultiThreadScore:F2} / 推定RTF {estimated:F2} / 選択モデル {model.DisplayName} "
                + $"/ スレッド {profile.SttThreads} / 話者分離 {(profile.DiarizationEnabled ? "有効" : "無効")} {saved}"),
            estimated <= ProfileSelector.TargetRealTimeFactor
                ? string.Empty
                : "このPCではリアルタイム文字起こしが遅れる可能性があります。録音は影響を受けませんが、"
                  + "文字起こしは録音停止後もしばらく処理が続きます。"));
    }

    private void AddModels(DiagnosticReport report, AppSettings settings)
    {
        var modelId = settings.Profile?.SttModelId ?? settings.SttModelId;
        var descriptor = ModelCatalog.Find(modelId);

        if (!settings.SttEnabled)
        {
            report.Checks.Add(DiagnosticCheck.Ok("D-07", "音声認識モデル", "文字起こしは無効に設定されています（録音のみ）。"));
            return;
        }

        if (descriptor is null)
        {
            report.Checks.Add(new DiagnosticCheck("D-07", "音声認識モデル", DiagnosticStatus.Warning,
                "使用するモデルが未選択です。",
                "設定画面を開くと自動判定が実行されます。未取得のままでも録音は可能です。"));
            return;
        }

        if (!_modelStore.IsPresent(descriptor))
        {
            report.Checks.Add(new DiagnosticCheck("D-07", "音声認識モデル", DiagnosticStatus.Warning,
                $"「{descriptor.DisplayName}」({descriptor.SizeDisplay}) が未取得です。",
                "設定画面からダウンロードしてください。未取得でも録音は実行できます（文字起こしのみ行われません）。"));
            return;
        }

        var entry = _modelStore.FindManifestEntry(descriptor.Id);
        var verified = entry?.VerifiedAgainstPinnedHash == true;
        report.Checks.Add(new DiagnosticCheck("D-07", "音声認識モデル",
            verified ? DiagnosticStatus.Ok : DiagnosticStatus.Warning,
            $"「{descriptor.DisplayName}」取得済み。SHA-256 "
            + (verified ? "照合済み。" : "の照合記録がありません（旧バージョンで取得した可能性があります）。"),
            verified ? string.Empty : "設定画面から再ダウンロードすると、既知のハッシュと照合されます。"));
    }

    private static void AddNetwork(DiagnosticReport report, DiagnosticEnvironment environment)
    {
        if (environment.OutboundConnections is null)
        {
            report.Checks.Add(DiagnosticCheck.NotTested(
                "D-08", "外部通信",
                "このプラットフォームではプロセスの接続一覧を取得できませんでした。"
                + "docs/WINDOWS_E2E_TEST.md の T-45 の手順で確認してください。"));
            return;
        }

        report.Checks.Add(environment.OutboundConnections.Count == 0
            ? DiagnosticCheck.Ok("D-08", "外部通信",
                "このプロセスが確立している外向きTCP接続は 0 件です（モデルのダウンロード中を除き、この状態が正常です）。")
            : new DiagnosticCheck("D-08", "外部通信", DiagnosticStatus.Warning,
                $"外向きTCP接続を {environment.OutboundConnections.Count} 件検出しました: "
                + string.Join(", ", environment.OutboundConnections.Take(10)),
                "モデルのダウンロード中でなければ想定外です。docs/WINDOWS_E2E_TEST.md の T-45 の手順で詳細を確認してください。"));
    }
}
