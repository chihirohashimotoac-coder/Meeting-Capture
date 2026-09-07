using System.Windows;
using MeetingRecorder.Core.Pipeline;

namespace MeetingRecorder.App.Views;

/// <summary>
/// Shows the result of every pre-flight check, with the remedy for anything
/// that failed.
/// </summary>
/// <remarks>
/// The requirement is explicit: a bare "Error" is not acceptable. Every row here
/// carries what was checked, what was found, and what the user should do about
/// it. An error blocks recording; a warning does not - the user can proceed with
/// full knowledge of what will be missing.
/// </remarks>
public partial class PreflightDialog : Window
{
    private PreflightDialog(IReadOnlyList<PreflightResult> results)
    {
        InitializeComponent();

        var errors = results.Count(r => r.Severity == PreflightSeverity.Error);
        var warnings = results.Count(r => r.Severity == PreflightSeverity.Warning);

        SummaryText.Text = errors > 0
            ? $"録音を開始できません（エラー {errors} 件 / 警告 {warnings} 件）。下記の対処を行ってください。"
            : warnings > 0
                ? $"警告が {warnings} 件あります。このまま録音を開始することもできます。"
                : "すべてのチェックに合格しました。";

        ResultsList.ItemsSource = results.Select(Row).ToList();
        ProceedButton.IsEnabled = errors == 0;
        ProceedButton.Content = errors == 0 && warnings == 0 ? "録音を開始" : "このまま録音を開始";
    }

    private static PreflightRow Row(PreflightResult result) => new(
        result.Severity switch
        {
            PreflightSeverity.Ok => "OK",
            PreflightSeverity.Warning => "警告",
            _ => "エラー",
        },
        result.Title,
        result.Detail,
        result.Remedy,
        string.IsNullOrWhiteSpace(result.Remedy) ? Visibility.Collapsed : Visibility.Visible);

    /// <summary>
    /// Returns true when recording may start. Silent when every check passed, so
    /// the common case stays one click.
    /// </summary>
    public static bool Confirm(Window? owner, IReadOnlyList<PreflightResult> results)
    {
        if (results.All(r => r.Severity == PreflightSeverity.Ok))
        {
            return true;
        }

        var dialog = new PreflightDialog(results);
        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }

    private void OnProceed(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
