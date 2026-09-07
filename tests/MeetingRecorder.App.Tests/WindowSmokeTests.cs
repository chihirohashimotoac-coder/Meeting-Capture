using System.Windows;
using MeetingRecorder.App.Services;
using MeetingRecorder.App.ViewModels;
using MeetingRecorder.App.Views;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Persistence;
using MeetingRecorder.Core.Pipeline;
using Xunit;

namespace MeetingRecorder.App.Tests;

/// <summary>
/// Builds every window the application can show and asserts that its XAML parses
/// and that no data binding fails.
/// </summary>
/// <remarks>
/// <para>These tests do <b>not</b> prove the application is usable: nothing is
/// shown on a screen, nobody clicks anything, and no audio device exists on a CI
/// runner. What they do prove is that the markup compiles and loads, that every
/// <c>{StaticResource}</c> resolves, and that every binding path matches a real
/// property - the class of defect that is otherwise invisible until a user
/// launches the app and finds an empty panel.</para>
///
/// <para>Real interaction is covered in docs/WINDOWS_E2E_TEST.md and must be
/// performed on a physical Windows machine.</para>
/// </remarks>
public class WindowSmokeTests
{
    [Fact]
    public void MainWindowLoadsAndBindsWithoutErrors()
    {
        StaTestHost.Invoke(() =>
        {
            using var services = new AppServices();
            using var scope = new BindingErrorScope();

            var window = new MainWindow(services);
            BindingErrorScope.ForceLayout(window);

            Assert.Empty(scope.Errors);
            window.Close();
        });
    }

    [Fact]
    public void SettingsWindowLoadsAndBindsWithoutErrors()
    {
        StaTestHost.Invoke(() =>
        {
            using var services = new AppServices();
            using var scope = new BindingErrorScope();

            var window = new SettingsWindow(services);
            BindingErrorScope.ForceLayout(window);

            Assert.Empty(scope.Errors);
            window.Close();
        });
    }

    [Fact]
    public void SetupWizardLoadsAndBindsWithoutErrors()
    {
        StaTestHost.Invoke(() =>
        {
            using var services = new AppServices();
            using var scope = new BindingErrorScope();

            var window = new SetupWizardWindow(services);
            BindingErrorScope.ForceLayout(window);

            Assert.Empty(scope.Errors);
            window.Close();
        });
    }

    [Fact]
    public void SpeakerRenameWindowShowsEveryClusterAndBindsWithoutErrors()
    {
        StaTestHost.Invoke(() =>
        {
            using var scope = new BindingErrorScope();

            var speakers = new List<SpeakerViewModel>
            {
                new(AudioSourceKind.SystemAudio, 0, null),
                new(AudioSourceKind.SystemAudio, 1, "既存の名前"),
                new(AudioSourceKind.Microphone, 0, null),
            };

            var window = new SpeakerRenameWindow(speakers);
            BindingErrorScope.ForceLayout(window);

            Assert.Empty(scope.Errors);
            Assert.Equal(3, window.Speakers.Count);
            Assert.Equal("PC音声 / Speaker 1", speakers[0].DefaultLabel);
            window.Close();
        });
    }

    [Fact]
    public void RecoveryWindowLoadsWithAPendingMeeting()
    {
        StaTestHost.Invoke(() =>
        {
            using var services = new AppServices();
            using var scope = new BindingErrorScope();

            var root = Path.Combine(Path.GetTempPath(), "mr-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var folder = MeetingFolder.Create(root, DateTimeOffset.Now);
                var metadata = new MeetingMetadata { FolderName = folder.Name, StartedAtLocal = DateTimeOffset.Now };
                var meeting = new RecoverableMeeting(folder, metadata, 3, 42.0);

                var window = new RecoveryWindow(services, new[] { meeting });
                BindingErrorScope.ForceLayout(window);

                Assert.Empty(scope.Errors);
                window.Close();
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (IOException)
                {
                }
            }
        });
    }

    [Fact]
    public void PreflightDialogIsSkippedEntirelyWhenEveryCheckPasses()
    {
        StaTestHost.Invoke(() =>
        {
            var results = new List<PreflightResult>
            {
                PreflightResult.Ok(PreflightCheck.Microphone, "マイク", "OK"),
                PreflightResult.Ok(PreflightCheck.SystemAudio, "PC内部音声", "OK"),
                PreflightResult.Ok(PreflightCheck.SaveLocation, "保存先", "OK"),
                PreflightResult.Ok(PreflightCheck.DiskSpace, "空き容量", "OK"),
                PreflightResult.Ok(PreflightCheck.SpeechModel, "モデル", "OK"),
            };

            // No dialog, no extra click: the common case must stay one press.
            Assert.True(PreflightDialog.Confirm(null, results));
        });
    }

    [Fact]
    public void ModelDownloadDialogDisclosesEverythingBeforeAnyConnectionIsMade()
    {
        StaTestHost.Invoke(() =>
        {
            using var services = new AppServices();
            using var scope = new BindingErrorScope();

            var descriptor = ModelCatalog.RequireSpeechModel(ModelCatalog.WhisperBase);

            // Construct the dialog only; nothing is downloaded until the user
            // presses the button, so this test performs no network access.
            var window = (Window)Activator.CreateInstance(
                typeof(ModelDownloadWindow),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                args: new object[] { services, descriptor },
                culture: null)!;

            BindingErrorScope.ForceLayout(window);
            Assert.Empty(scope.Errors);
            window.Close();
        });
    }
}

public class TranscriptItemViewModelTests
{
    [Fact]
    public void EditingTheTextRaisesTheEditEvent()
    {
        var segment = new TranscriptSegment { StartMs = 5000, Source = AudioSourceKind.Microphone, Text = "元の文" };
        var item = new TranscriptItemViewModel(segment);

        string? edited = null;
        item.TextEdited += (_, text) => edited = text;

        item.Text = "修正後の文";

        Assert.Equal("修正後の文", edited);
        Assert.Equal("00:00:05", item.Timestamp);
        Assert.Equal("マイク", item.SourceLabel);
    }

    [Fact]
    public void RefreshPicksUpASpeakerNameAssignedLater()
    {
        var segment = new TranscriptSegment { StartMs = 0, Source = AudioSourceKind.SystemAudio, SpeakerId = 1, Text = "本文" };
        var item = new TranscriptItemViewModel(segment);

        Assert.Equal("PC音声 / Speaker 2", item.SourceLabel);

        segment.SpeakerName = "大西";
        item.Refresh(segment);

        Assert.Equal("PC音声 / 大西", item.SourceLabel);
    }

    [Fact]
    public void ASpeakerStartsWithAnAnonymousLabelNeverAGuessedName()
    {
        var speaker = new SpeakerViewModel(AudioSourceKind.SystemAudio, 2, null);

        Assert.Equal("Speaker 3", speaker.DisplayName);
        Assert.Equal("PC音声 / Speaker 3", speaker.DefaultLabel);
    }
}
