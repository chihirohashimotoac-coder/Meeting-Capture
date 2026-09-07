using System.IO;
using System.Windows;
using MeetingRecorder.App.Services;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.App.Views;

/// <summary>
/// First-run wizard: microphone, PC audio endpoint, save location, model.
/// </summary>
/// <remarks>
/// Everything here is remembered, so from the second run onwards the whole
/// workflow is "start MeetingRecorder.exe, press record". Nothing in this wizard
/// is mandatory except the save location - a user who declines the model
/// download still gets a working recorder, just without a transcript.
/// </remarks>
public partial class SetupWizardWindow : Window
{
    private readonly AppServices _services;
    private IReadOnlyList<AudioDeviceInfo> _captureDevices = Array.Empty<AudioDeviceInfo>();
    private IReadOnlyList<AudioDeviceInfo> _renderDevices = Array.Empty<AudioDeviceInfo>();

    public SetupWizardWindow(AppServices services)
    {
        InitializeComponent();

        _services = services;
        SaveRootBox.Text = services.Settings.SaveRoot ?? AppPaths.DefaultSaveRoot();

        LoadDevices();
        LoadModels();

        ProfileText.Text = services.Settings.Profile is null
            ? "「PC性能を測定」を押すと、この端末の実測性能に合わせてモデルとスレッド数を自動選択します。"
            : services.Settings.Profile.Rationale;
    }

    private void LoadDevices()
    {
        _captureDevices = _services.DeviceProvider.GetCaptureDevices();
        _renderDevices = _services.DeviceProvider.GetRenderDevices();

        MicCombo.ItemsSource = BuildRows(_captureDevices);
        RenderCombo.ItemsSource = BuildRows(_renderDevices);
        MicCombo.SelectedIndex = 0;
        RenderCombo.SelectedIndex = 0;

        MicStatusText.Text = _captureDevices.Count == 0
            ? "録音デバイスが見つかりません。マイクを接続し、Windowsのサウンド設定で有効にしてください。"
            : $"{_captureDevices.Count} 件の録音デバイスが見つかりました。";

        RenderStatusText.Text = _renderDevices.Count == 0
            ? "再生デバイスが見つかりません。PC内部音声の取得には再生デバイスが必要です。"
            : $"{_renderDevices.Count} 件の再生デバイスが見つかりました。";
    }

    private static List<DeviceRow> BuildRows(IReadOnlyList<AudioDeviceInfo> devices)
    {
        var rows = new List<DeviceRow> { new("（Windowsの既定デバイスを使用）", null) };
        rows.AddRange(devices.Select(d => new DeviceRow(d.IsDefault ? $"{d.Name}（既定）" : d.Name, d.Id)));
        return rows;
    }

    private void LoadModels()
    {
        ModelCombo.ItemsSource = ModelCatalog.SpeechModels
            .Select(m => new ModelRow(
                $"{m.DisplayName} / {m.SizeDisplay} / {m.License}"
                + (_services.ModelStore.IsPresent(m) ? "  [取得済み]" : "  [未取得]"),
                m.Id))
            .ToList();

        var target = _services.Settings.Profile?.SttModelId ?? ModelCatalog.WhisperBase;
        for (var i = 0; i < ModelCatalog.SpeechModels.Count; i++)
        {
            if (ModelCatalog.SpeechModels[i].Id == target)
            {
                ModelCombo.SelectedIndex = i;
                return;
            }
        }

        ModelCombo.SelectedIndex = 0;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "保存先フォルダーを選択" };
        if (Directory.Exists(SaveRootBox.Text))
        {
            dialog.InitialDirectory = SaveRootBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            SaveRootBox.Text = dialog.FolderName;
        }
    }

    private async void OnBenchmark(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "PC性能を測定しています...";
        IsEnabled = false;

        try
        {
            var profile = await Task.Run(() => _services.MeasureAndStoreProfile());
            ProfileText.Text = profile.Rationale;
            LoadModels();
            StatusText.Text = "測定が完了しました。";
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(SetupWizardWindow), "Benchmark failed.", ex);
            StatusText.Text = $"測定に失敗しました: {ex.Message}";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        var index = ModelCombo.SelectedIndex;
        if (index < 0 || index >= ModelCatalog.SpeechModels.Count)
        {
            return;
        }

        var descriptor = ModelCatalog.SpeechModels[index];
        if (ModelDownloadWindow.Run(this, _services, descriptor))
        {
            ModelStatusText.Text = $"「{descriptor.DisplayName}」を取得しました。";
            LoadModels();
        }
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        var saveRoot = SaveRootBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(saveRoot) || !AppPaths.CanWrite(saveRoot!))
        {
            MessageBox.Show(
                "保存先に書き込めません。書き込み権限のあるフォルダー（例: ドキュメント配下）を選択してください。",
                "初回設定",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var settings = _services.Settings;
        settings.SaveRoot = saveRoot;

        var mic = MicCombo.SelectedItem as DeviceRow;
        settings.MicrophoneDeviceId = mic?.Id;
        settings.MicrophoneDeviceName = mic?.Name;

        var render = RenderCombo.SelectedItem as DeviceRow;
        settings.RenderDeviceId = render?.Id;
        settings.RenderDeviceName = render?.Name;

        var index = ModelCombo.SelectedIndex;
        if (index >= 0 && index < ModelCatalog.SpeechModels.Count)
        {
            settings.SttModelId = ModelCatalog.SpeechModels[index].Id;
            if (settings.Profile is not null)
            {
                settings.Profile.SttModelId = settings.SttModelId;
            }
        }

        settings.SetupCompleted = true;
        _services.SaveSettings();

        DialogResult = true;
        Close();
    }

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
