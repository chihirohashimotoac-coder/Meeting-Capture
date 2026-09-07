using System.Globalization;
using System.Windows;
using MeetingRecorder.App.Services;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.App.Views;

/// <summary>
/// Offered at start-up when a meeting folder still carries an unfinished
/// journal.
/// </summary>
/// <remarks>
/// The design rule behind this window: pressing Stop must never be the thing
/// that decides whether a meeting survives. The audio is already on disk and
/// playable; recovery only regenerates the transcript and metadata files from
/// the journal. "Do not recover" leaves the audio exactly where it is - the app
/// never deletes a user's recording on their behalf.
/// </remarks>
public partial class RecoveryWindow : Window
{
    private readonly AppServices _services;
    private readonly IReadOnlyList<RecoverableMeeting> _meetings;

    public RecoveryWindow(AppServices services, IReadOnlyList<RecoverableMeeting> meetings)
    {
        InitializeComponent();

        _services = services;
        _meetings = meetings;

        MeetingList.ItemsSource = meetings.Select(m => new RecoveryRow(
            m.DisplayName,
            m.StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            TranscriptExporter.FormatDuration(m.AudioSeconds),
            m.SegmentCount)).ToList();

        StatusText.Text = $"{meetings.Count} 件の未完了の会議があります。";
    }

    private void OnRecoverAll(object sender, RoutedEventArgs e)
    {
        var recovered = 0;
        foreach (var meeting in _meetings)
        {
            try
            {
                _services.RecoveryService.Recover(meeting);
                recovered++;
            }
            catch (Exception ex)
            {
                _services.Logger.Error(nameof(RecoveryWindow), $"Recovering '{meeting.Folder.Name}' failed.", ex);
            }
        }

        MessageBox.Show(
            recovered == _meetings.Count
                ? $"{recovered} 件の会議を復旧しました。\n保存先: {_services.Settings.SaveRoot}"
                : $"{recovered}/{_meetings.Count} 件を復旧しました。詳細はログを確認してください。",
            "MeetingRecorder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "復旧候補から除外します。録音済みの音声ファイルは削除されません。よろしいですか？",
            "MeetingRecorder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var meeting in _meetings)
        {
            _services.RecoveryService.Dismiss(meeting);
        }

        Close();
    }
}
