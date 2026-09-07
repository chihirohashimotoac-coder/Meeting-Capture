using System.Windows;
using System.Windows.Threading;
using MeetingRecorder.App.Services;
using MeetingRecorder.App.Views;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.App;

public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Unhandled exceptions are logged locally and shown to the user. They are
        // never transmitted anywhere: this application has no crash reporting
        // service and no telemetry of any kind.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            _services = new AppServices();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"起動に失敗しました。\n\n{ex.Message}",
                "MeetingRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // A meeting that was interrupted by a crash or a power loss is offered
        // for recovery before anything else happens.
        OfferCrashRecovery(_services);

        if (!_services.Settings.SetupCompleted)
        {
            var wizard = new SetupWizardWindow(_services);
            if (wizard.ShowDialog() != true)
            {
                Shutdown(0);
                return;
            }
        }

        var main = new MainWindow(_services);
        MainWindow = main;
        main.Show();
    }

    private void OfferCrashRecovery(AppServices services)
    {
        try
        {
            var recoverable = services.RecoveryService.Scan(services.Settings.SaveRoot);
            if (recoverable.Count == 0)
            {
                return;
            }

            var dialog = new RecoveryWindow(services, recoverable);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            services.Logger.Error(nameof(App), "Crash recovery scan failed.", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Logger.Error(nameof(App), "Unhandled UI exception.", e.Exception);

        MessageBox.Show(
            "予期しないエラーが発生しました。\n" +
            "録音中の場合、録音データは保存先フォルダーに残っており、次回起動時に復旧できます。\n\n" +
            $"{e.Exception.Message}\n\nログ: {AppPaths.LogDirectory}",
            "MeetingRecorder",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep the process alive: a UI-thread exception must not throw away a
        // recording that is still being written by the pump thread.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _services?.Logger.Error(nameof(App), "Unhandled non-UI exception.", exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
