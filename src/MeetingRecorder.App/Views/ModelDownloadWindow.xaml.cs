using System.Windows;
using MeetingRecorder.App.Services;
using MeetingRecorder.Core.ModelManagement;

namespace MeetingRecorder.App.Views;

/// <summary>
/// The one place in the product where an outbound connection is made, and the
/// only place the user ever sees a download.
/// </summary>
/// <remarks>
/// Everything the requirements ask to be disclosed before downloading is on
/// screen before the button is pressed: name, purpose, publisher, exact URL,
/// size, destination path and licence. Afterwards the SHA-256 result is shown -
/// including, honestly, when the project has no pinned hash to compare against
/// and could only record the one it computed.
/// </remarks>
public partial class ModelDownloadWindow : Window
{
    private readonly AppServices _services;
    private readonly ModelDescriptor _descriptor;
    private readonly CancellationTokenSource _cts = new();

    private ModelDownloadWindow(AppServices services, ModelDescriptor descriptor)
    {
        InitializeComponent();

        _services = services;
        _descriptor = descriptor;

        NameText.Text = descriptor.DisplayName;
        PurposeText.Text = descriptor.PurposeDescription;
        PublisherText.Text = descriptor.Publisher;
        UrlText.Text = descriptor.Url;
        SizeText.Text = $"約 {descriptor.SizeDisplay}";
        DestinationText.Text = services.ModelStore.PathFor(descriptor);
        LicenseText.Text = $"{descriptor.License}  ({descriptor.LicenseUrl})";
        IntegrityText.Text = descriptor.Sha256 is null
            ? "SHA-256: このモデルには既知のハッシュが登録されていません。ダウンロード後に計算した値をローカルの models.json に記録します。"
            : $"SHA-256: {descriptor.Sha256}（照合します）";

        ProgressText.Text = "「ダウンロード開始」を押すと通信を開始します。";
    }

    /// <summary>Shows the dialog and returns true when the model is on disk afterwards.</summary>
    public static bool Run(Window? owner, AppServices services, ModelDescriptor descriptor)
    {
        var window = new ModelDownloadWindow(services, descriptor);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        window.ShowDialog();
        return services.ModelStore.IsPresent(descriptor);
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = false;
        CloseButton.Content = "中止";

        var progress = new Progress<ModelDownloadProgress>(report =>
        {
            Progress.IsIndeterminate = report.Fraction is null;
            if (report.Fraction is { } fraction)
            {
                Progress.Value = fraction;
            }

            ProgressText.Text = report.Phase switch
            {
                ModelDownloadPhase.Connecting => "接続中...",
                ModelDownloadPhase.Downloading =>
                    $"ダウンロード中 {report.BytesReceived / 1024.0 / 1024.0:F1} MB"
                    + (report.TotalBytes is { } total ? $" / {total / 1024.0 / 1024.0:F1} MB" : string.Empty)
                    + $"  ({report.BytesPerSecond / 1024.0 / 1024.0:F1} MB/s"
                    + (report.Remaining is { } remaining ? $", 残り {remaining.TotalMinutes:F0} 分)" : ")"),
                ModelDownloadPhase.Verifying => "SHA-256 を計算しています...",
                _ => "完了しました。",
            };
        });

        try
        {
            var result = await _services.ModelDownloader.DownloadAsync(_descriptor, progress, _cts.Token);

            IntegrityText.Text = result.VerifiedAgainstPinnedHash
                ? $"SHA-256 照合に成功しました: {result.Sha256}"
                : $"SHA-256 を記録しました（照合用の既知ハッシュなし）: {result.Sha256}";

            ProgressText.Text = $"ダウンロードが完了しました（{result.SizeBytes / 1024.0 / 1024.0:F1} MB）。";
            Progress.Value = 1;
            CloseButton.Content = "閉じる";
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "ダウンロードを中止しました。再開すると途中から続きをダウンロードします。";
            DownloadButton.IsEnabled = true;
            CloseButton.Content = "閉じる";
        }
        catch (Exception ex)
        {
            _services.Logger.Error(nameof(ModelDownloadWindow), "Model download failed.", ex);
            ProgressText.Text = $"ダウンロードに失敗しました: {ex.Message}";
            DownloadButton.IsEnabled = true;
            CloseButton.Content = "閉じる";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        Close();
    }
}
