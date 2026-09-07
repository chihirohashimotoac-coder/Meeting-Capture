using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MeetingRecorder.App.Services;
using MeetingRecorder.App.ViewModels;

namespace MeetingRecorder.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(AppServices services)
    {
        InitializeComponent();

        _viewModel = new MainViewModel(services);
        DataContext = _viewModel;

        // Follow the transcript as it grows, but only while the user is already
        // at the bottom - otherwise scrolling back to check something earlier
        // would be yanked away on every new segment.
        ((INotifyCollectionChanged)_viewModel.Transcript).CollectionChanged += OnTranscriptChanged;

        Loaded += (_, _) => _viewModel.Activate();
    }

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _viewModel.Transcript.Count == 0)
        {
            return;
        }

        var scrollViewer = FindScrollViewer(TranscriptList);
        if (scrollViewer is null)
        {
            TranscriptList.ScrollIntoView(_viewModel.Transcript[^1]);
            return;
        }

        var atBottom = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 40;
        if (atBottom)
        {
            TranscriptList.ScrollIntoView(_viewModel.Transcript[^1]);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var result = FindScrollViewer(child);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
