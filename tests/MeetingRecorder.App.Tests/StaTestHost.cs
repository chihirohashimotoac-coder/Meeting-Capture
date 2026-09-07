using System.Windows;
using System.Windows.Threading;

namespace MeetingRecorder.App.Tests;

/// <summary>
/// A single, shared STA thread with a WPF dispatcher and one
/// <see cref="Application"/> instance, so UI types can be constructed inside a
/// normal xUnit run.
/// </summary>
/// <remarks>
/// WPF requires an STA thread and an <see cref="Application"/> whose resource
/// dictionary holds the brushes and styles the windows reference; without it,
/// every <c>{StaticResource}</c> in the markup would fail. Only one
/// <see cref="Application"/> may exist per process, so it is created here once.
/// </remarks>
public static class StaTestHost
{
    private static readonly object Sync = new();
    private static Dispatcher? _dispatcher;

    public static void Invoke(Action action)
    {
        EnsureStarted();
        Exception? failure = null;

        _dispatcher!.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (failure is not null)
        {
            throw new InvalidOperationException($"UI thread work failed: {failure.Message}", failure);
        }
    }

    private static void EnsureStarted()
    {
        lock (Sync)
        {
            if (_dispatcher is not null)
            {
                return;
            }

            var ready = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                if (Application.Current is null)
                {
                    var application = new MeetingRecorder.App.App();

                    // Loads App.xaml's resource dictionary, which the windows
                    // reference through {StaticResource}.
                    application.InitializeComponent();
                }

                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "MeetingRecorder.UiTests",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The WPF test host did not start.");
            }
        }
    }
}
