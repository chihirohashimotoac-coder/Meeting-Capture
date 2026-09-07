using MeetingRecorder.Core.Models;

namespace MeetingRecorder.App.ViewModels;

/// <summary>One editable line of the live transcript.</summary>
public sealed class TranscriptItemViewModel : ObservableObject
{
    private string _text;
    private string _sourceLabel;

    public TranscriptItemViewModel(TranscriptSegment segment)
    {
        Segment = segment;
        _text = segment.Text;
        _sourceLabel = segment.SourceLabel();
    }

    public TranscriptSegment Segment { get; }

    public string Id => Segment.Id;

    public string Timestamp => Segment.TimestampLabel();

    public AudioSourceKind Source => Segment.Source;

    public int? SpeakerId => Segment.SpeakerId;

    /// <summary>"マイク" or "PC音声 / Speaker 2" - derived from the capture stream.</summary>
    public string SourceLabel
    {
        get => _sourceLabel;
        private set => SetProperty(ref _sourceLabel, value);
    }

    /// <summary>Editable body text. Setting it marks the segment as human-edited.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                TextEdited?.Invoke(this, value);
            }
        }
    }

    public bool IsEdited => Segment.IsEdited;

    /// <summary>Raised when the user changes the text so the store can persist it.</summary>
    public event EventHandler<string>? TextEdited;

    /// <summary>Refreshes the displayed values after the underlying segment changed.</summary>
    public void Refresh(TranscriptSegment segment)
    {
        Segment.Text = segment.Text;
        Segment.SpeakerId = segment.SpeakerId;
        Segment.SpeakerName = segment.SpeakerName;
        Segment.IsEdited = segment.IsEdited;

        SetProperty(ref _text, segment.Text, nameof(Text));
        SourceLabel = segment.SourceLabel();
        OnPropertyChanged(nameof(IsEdited));
    }
}

/// <summary>A speaker cluster the user can rename.</summary>
public sealed class SpeakerViewModel : ObservableObject
{
    private string _displayName;

    public SpeakerViewModel(AudioSourceKind source, int speakerId, string? name)
    {
        Source = source;
        SpeakerId = speakerId;
        _displayName = name ?? $"Speaker {speakerId + 1}";
    }

    public AudioSourceKind Source { get; }

    public int SpeakerId { get; }

    public string SourceLabel => Source.ToDisplayLabel();

    public string DefaultLabel => $"{Source.ToDisplayLabel()} / Speaker {SpeakerId + 1}";

    /// <summary>
    /// The name the user typed. The application never fills this in on its own -
    /// a real person's name only ever appears because a human entered it.
    /// </summary>
    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }
}
