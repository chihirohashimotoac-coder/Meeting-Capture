using System.Windows;
using MeetingRecorder.App.Services;
using MeetingRecorder.Core.Benchmark;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;

    public SettingsWindow(AppServices services)
    {
        InitializeComponent();

        _services = services;
        LoadDevices();
        LoadModels();
        LoadValues();

        // Attached after the initial selection so that populating the combo does
        // not fire it before the controls it writes to are ready.
        ModelCombo.SelectionChanged += (_, _) => UpdateModelEstimateText();
    }

    private void LoadDevices()
    {
        var settings = _services.Settings;

        var capture = _services.DeviceProvider.GetCaptureDevices().ToList();
        var render = _services.DeviceProvider.GetRenderDevices().ToList();

        MicCombo.ItemsSource = BuildDeviceRows(capture);
        RenderCombo.ItemsSource = BuildDeviceRows(render);

        MicCombo.SelectedIndex = IndexOf(capture, settings.MicrophoneDeviceId);
        RenderCombo.SelectedIndex = IndexOf(render, settings.RenderDeviceId);

        if (capture.Count == 0)
        {
            StatusText.Text = "録音デバイスが見つかりません。マイクを接続してから「デバイス一覧を再取得」を押してください。";
        }
        else if (render.Count == 0)
        {
            StatusText.Text = "再生デバイスが見つかりません。PC内部音声の取得には再生デバイスが必要です。";
        }
    }

    /// <summary>Row 0 is always "Windows default"; the rest are real endpoints.</summary>
    private static List<DeviceRow> BuildDeviceRows(IReadOnlyList<AudioDeviceInfo> devices)
    {
        var rows = new List<DeviceRow> { new("（Windowsの既定デバイスを使用）", null) };
        rows.AddRange(devices.Select(d => new DeviceRow(d.IsDefault ? $"{d.Name}（既定）" : d.Name, d.Id)));
        return rows;
    }

    private static int IndexOf(IReadOnlyList<AudioDeviceInfo> devices, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return 0;
        }

        for (var i = 0; i < devices.Count; i++)
        {
            if (string.Equals(devices[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private void LoadModels()
    {
        ModelCombo.ItemsSource = ModelCatalog.SpeechModels.Select(BuildModelRow).ToList();
        LlmCombo.ItemsSource = ModelCatalog.TextGenerationModels.Select(BuildModelRow).ToList();

        SelectModel(ModelCombo, ModelCatalog.SpeechModels, _services.Settings.SttModelId ?? _services.Settings.Profile?.SttModelId);
        SelectModel(LlmCombo, ModelCatalog.TextGenerationModels, _services.Settings.LlmModelId);
    }

    private ModelRow BuildModelRow(ModelDescriptor descriptor) => new(
        $"{descriptor.DisplayName} / {descriptor.SizeDisplay} / {descriptor.License}"
        + (_services.ModelStore.IsPresent(descriptor) ? "  [取得済み]" : "  [未取得]"),
        descriptor.Id);

    private static void SelectModel(System.Windows.Controls.ComboBox combo, IReadOnlyList<ModelDescriptor> models, string? id)
    {
        for (var i = 0; i < models.Count; i++)
        {
            if (string.Equals(models[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = models.Count > 0 ? 0 : -1;
    }

    private void LoadValues()
    {
        var settings = _services.Settings;

        SaveRootBox.Text = settings.SaveRoot ?? AppPaths.DefaultSaveRoot();
        FollowDefaultCheck.IsChecked = settings.FollowDefaultDevices;
        WavRadio.IsChecked = settings.Format == RecordingFormat.Wav;
        Mp3Radio.IsChecked = settings.Format == RecordingFormat.Mp3;
        SttEnabledCheck.IsChecked = settings.SttEnabled;
        DiarizationCheck.IsChecked = settings.DiarizationEnabled;
        MinutesCheck.IsChecked = settings.MinutesEnabled;
        SleepCheck.IsChecked = settings.PreventSleepWhileRecording;
        AutoSaveBox.Text = settings.AutoSaveIntervalSeconds.ToString();
        KeepRecognitionAudioCheck.IsChecked = settings.KeepRecognitionAudio;
        DeleteRecognitionAudioCheck.IsChecked = settings.DeleteRecognitionAudioAfterTranscription;
        AutoSaveRecordingCheck.IsChecked = settings.AutoSaveRecordings;
        MicMutedCheck.IsChecked = settings.MicrophoneMuted;
        SystemMutedCheck.IsChecked = settings.SystemAudioMuted;
        UpdateModelEstimateText();

        var mp3 = _services.Transcoder.IsFormatSupported(RecordingFormat.Mp3);
        Mp3AvailabilityText.Text = mp3
            ? "MP3エンコーダー: Windows標準のMedia Foundationを使用します（追加インストール不要）。"
            : "MP3エンコーダーが見つかりません（Windows N/KNエディションなど）。MP3を選んでもWAVで保存されます。";
        Mp3Radio.IsEnabled = mp3;

        UpdateProfileText();

        PathsText.Text =
            $"設定ファイル: {AppPaths.SettingsFile}\n" +
            $"ログ: {AppPaths.LogDirectory}\n" +
            $"AIモデル: {_services.ModelStore.Directory}\n" +
            $"起動モード: {(AppPaths.IsPortable ? "ポータブル（実行ファイルの隣に保存）" : "ユーザープロファイル（%LOCALAPPDATA%）")}";
    }

    /// <summary>
    /// Says what the selected model will cost in waiting, so a choice that ties
    /// the machine up for an afternoon is made deliberately.
    /// </summary>
    /// <remarks>
    /// The figure is an estimate scaled from the measured CPU score, and it is
    /// labelled as one. It is not presented as a measurement of this machine
    /// transcribing this model, because it is not.
    /// </remarks>
    private void UpdateModelEstimateText()
    {
        var descriptor = SelectedDescriptor(ModelCombo, ModelCatalog.SpeechModels);
        if (descriptor is null)
        {
            ModelEstimateText.Text = string.Empty;
            return;
        }

        var present = _services.ModelStore.IsPresent(descriptor)
            ? "取得済み"
            : $"未取得（{descriptor.SizeDisplay} のダウンロードが必要）";

        var profile = _services.Settings.Profile;
        if (profile is null || profile.CpuScore <= 0)
        {
            ModelEstimateText.Text =
                $"{descriptor.DisplayName} — {present}。"
                + "「PC性能を測定して自動設定」を実行すると、このPCでのおおよその処理時間を表示します。";
            return;
        }

        var capability = new MachineCapability(
            profile.LogicalCores, profile.TotalRamGb, profile.CpuScore, profile.CpuScore, 0);
        var factor = ProfileSelector.EstimateProcessingFactor(descriptor, capability);
        var fits = ProfileSelector.FitsInMemory(descriptor, capability);

        var memory = fits
            ? string.Empty
            : $" このPCのRAM（{profile.TotalRamGb:F0}GB）では動作が不安定になる可能性があります。";

        ModelEstimateText.Text =
            $"{descriptor.DisplayName} — {present} / 推定処理時間: 音声長の約 {factor:F1} 倍"
            + $"（1時間の録音でおよそ {TimeSpan.FromHours(factor):h\時\間mm\分}。実測値ではなく推定です）。{memory}";
    }

    private void UpdateProfileText()
    {
        var profile = _services.Settings.Profile;
        ProfileText.Text = profile is null
            ? "PC性能はまだ測定されていません。「PC性能を測定して自動設定」を押すと、この端末の実測値からモデル・スレッド数・話者分離の可否を自動選択します。"
            : $"自動設定: {profile.Rationale}\nスレッド数 {profile.SttThreads} / "
              + $"話者分離 {(profile.DiarizationEnabled ? "有効" : "無効")} / 測定日時 {profile.MeasuredAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        LoadDevices();
        StatusText.Text = "デバイス一覧を再取得しました。";
    }

    private void OnBrowseSaveRoot(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "録音データの保存先フォルダーを選択",
            Multiselect = false,
        };

        if (Directory.Exists(SaveRootBox.Text))
        {
            dialog.InitialDirectory = SaveRootBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            SaveRootBox.Text = dialog.FolderName;
        }
    }

    private void OnDownloadModel(object sender, RoutedEventArgs e)
    {
        var descriptor = SelectedDescriptor(ModelCombo, ModelCatalog.SpeechModels);
        if (descriptor is null)
        {
            return;
        }

        if (ModelDownloadWindow.Run(this, _services, descriptor))
        {
            StatusText.Text = $"「{descriptor.DisplayName}」を取得しました。";
            LoadModels();
            UpdateModelEstimateText();
        }
    }

    private void OnDownloadLlm(object sender, RoutedEventArgs e)
    {
        var descriptor = SelectedDescriptor(LlmCombo, ModelCatalog.TextGenerationModels);
        if (descriptor is null)
        {
            return;
        }

        if (!descriptor.License.StartsWith("Apache", StringComparison.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(
                $"「{descriptor.DisplayName}」のライセンスは {descriptor.License} です。\n" +
                "社内での利用可否を確認したうえでダウンロードしてください。続行しますか？\n\n" +
                descriptor.LicenseUrl,
                "ライセンスの確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (ModelDownloadWindow.Run(this, _services, descriptor))
        {
            StatusText.Text = $"「{descriptor.DisplayName}」を取得しました。";
            LoadModels();
        }
    }

    private static ModelDescriptor? SelectedDescriptor(System.Windows.Controls.ComboBox combo, IReadOnlyList<ModelDescriptor> models)
        => combo.SelectedIndex >= 0 && combo.SelectedIndex < models.Count ? models[combo.SelectedIndex] : null;

    private async void OnBenchmark(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "PC性能を測定しています（数秒かかります）...";
        IsEnabled = false;

        try
        {
            var profile = await Task.Run(() => _services.MeasureAndStoreProfile());
            UpdateProfileText();
            LoadModels();
            SelectModel(ModelCombo, ModelCatalog.SpeechModels, profile.SttModelId);
            UpdateModelEstimateText();
            StatusText.Text = "測定が完了し、設定を更新しました。";
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(SettingsWindow), "Benchmark failed.", ex);
            StatusText.Text = $"測定に失敗しました: {ex.Message}";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var settings = _services.Settings;

        settings.MicrophoneDeviceId = SelectedDeviceId(MicCombo);
        settings.MicrophoneDeviceName = SelectedDeviceName(MicCombo);
        settings.RenderDeviceId = SelectedDeviceId(RenderCombo);
        settings.RenderDeviceName = SelectedDeviceName(RenderCombo);
        settings.FollowDefaultDevices = FollowDefaultCheck.IsChecked == true;

        var saveRoot = SaveRootBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            MessageBox.Show("保存先を指定してください。", "設定", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!AppPaths.CanWrite(saveRoot!))
        {
            MessageBox.Show(
                $"指定された保存先に書き込めません。\n{saveRoot}\n\n" +
                "書き込み権限のあるフォルダー（例: ドキュメント配下）を選び直してください。",
                "設定",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        settings.SaveRoot = saveRoot;
        settings.Format = Mp3Radio.IsChecked == true ? RecordingFormat.Mp3 : RecordingFormat.Wav;
        settings.SttEnabled = SttEnabledCheck.IsChecked == true;
        settings.DiarizationEnabled = DiarizationCheck.IsChecked == true;
        settings.MinutesEnabled = MinutesCheck.IsChecked == true;
        settings.PreventSleepWhileRecording = SleepCheck.IsChecked == true;
        settings.KeepRecognitionAudio = KeepRecognitionAudioCheck.IsChecked == true;
        settings.DeleteRecognitionAudioAfterTranscription = DeleteRecognitionAudioCheck.IsChecked == true;
        settings.AutoSaveRecordings = AutoSaveRecordingCheck.IsChecked == true;
        settings.MicrophoneMuted = MicMutedCheck.IsChecked == true;
        settings.SystemAudioMuted = SystemMutedCheck.IsChecked == true;

        if (int.TryParse(AutoSaveBox.Text, out var interval))
        {
            settings.AutoSaveIntervalSeconds = Math.Clamp(interval, 5, 300);
        }

        var sttModel = SelectedDescriptor(ModelCombo, ModelCatalog.SpeechModels);
        if (sttModel is not null)
        {
            settings.SttModelId = sttModel.Id;
            if (settings.Profile is not null)
            {
                settings.Profile.SttModelId = sttModel.Id;
            }
        }

        settings.LlmModelId = SelectedDescriptor(LlmCombo, ModelCatalog.TextGenerationModels)?.Id;

        _services.SaveSettings();
        DialogResult = true;
        Close();
    }

    private static string? SelectedDeviceId(System.Windows.Controls.ComboBox combo)
        => (combo.SelectedItem as DeviceRow)?.Id;

    private static string? SelectedDeviceName(System.Windows.Controls.ComboBox combo)
        => (combo.SelectedItem as DeviceRow)?.Name;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
