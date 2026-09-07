using System.Windows;
using MeetingRecorder.App.ViewModels;

namespace MeetingRecorder.App.Views;

public partial class SpeakerRenameWindow : Window
{
    public SpeakerRenameWindow(IReadOnlyList<SpeakerViewModel> speakers)
    {
        InitializeComponent();

        Speakers = speakers;
        SpeakerList.ItemsSource = speakers;
    }

    public IReadOnlyList<SpeakerViewModel> Speakers { get; }

    private void OnApply(object sender, RoutedEventArgs e)
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
