using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.Core.Pipeline;

/// <summary>
/// The live transcript: thread-safe, journal-backed, and editable by the user.
/// </summary>
/// <remarks>
/// Segments arrive on the transcription worker thread and are read by the UI
/// thread, so every mutation takes the lock and every read returns a snapshot.
/// Every mutation is also appended to the recovery journal, which is what makes
/// an interrupted meeting recoverable down to the last recognized sentence.
/// </remarks>
public sealed class TranscriptStore
{
    private readonly object _sync = new();
    private readonly List<TranscriptSegment> _segments = new();
    private readonly Dictionary<string, string> _speakerNames = new(StringComparer.Ordinal);

    private RecoveryJournal? _journal;

    public event Action<TranscriptSegment>? SegmentAdded;

    public event Action<TranscriptSegment>? SegmentChanged;

    public event Action<TranscriptSegment>? SegmentRemoved;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _segments.Count;
            }
        }
    }

    public void AttachJournal(RecoveryJournal? journal)
    {
        lock (_sync)
        {
            _journal = journal;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _segments.Clear();
            _speakerNames.Clear();
        }
    }

    public void Add(TranscriptSegment segment)
    {
        lock (_sync)
        {
            ApplyKnownSpeakerName(segment);
            InsertOrdered(segment);
            _journal?.AppendSegment(segment);
        }

        SegmentAdded?.Invoke(segment);
    }

    public void AddRange(IEnumerable<TranscriptSegment> segments)
    {
        foreach (var segment in segments)
        {
            Add(segment);
        }
    }

    /// <summary>
    /// Swaps the whole transcript for a new one. Used by the second
    /// transcription pass, which produces its own segmentation rather than
    /// editing the live one.
    /// </summary>
    /// <remarks>
    /// Raised as removals followed by additions so any view bound to the events
    /// ends up consistent without needing to know this operation exists. Speaker
    /// names survive, because a person may have typed them.
    /// </remarks>
    public void ReplaceAll(IEnumerable<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var replacement = segments.OrderBy(s => s.StartMs).ToList();

        TranscriptSegment[] removed;
        lock (_sync)
        {
            removed = _segments.ToArray();
            _segments.Clear();
        }

        foreach (var segment in removed)
        {
            SegmentRemoved?.Invoke(segment);
        }

        foreach (var segment in replacement)
        {
            Add(segment);
        }
    }

    /// <summary>Applies a user edit to the text of a segment.</summary>
    public bool EditText(string id, string text)
    {
        TranscriptSegment? segment;
        lock (_sync)
        {
            segment = _segments.FirstOrDefault(s => s.Id == id);
            if (segment is null)
            {
                return false;
            }

            segment.Text = text;
            segment.IsEdited = true;
            _journal?.UpdateSegment(segment);
        }

        SegmentChanged?.Invoke(segment);
        return true;
    }

    /// <summary>Moves a segment to a different speaker cluster (or clears it).</summary>
    public bool AssignSpeaker(string id, int? speakerId, string? speakerName = null)
    {
        TranscriptSegment? segment;
        lock (_sync)
        {
            segment = _segments.FirstOrDefault(s => s.Id == id);
            if (segment is null)
            {
                return false;
            }

            segment.SpeakerId = speakerId;
            segment.SpeakerName = speakerName;
            segment.IsEdited = true;
            _journal?.UpdateSegment(segment);
        }

        SegmentChanged?.Invoke(segment);
        return true;
    }

    public bool Remove(string id)
    {
        TranscriptSegment? segment;
        lock (_sync)
        {
            segment = _segments.FirstOrDefault(s => s.Id == id);
            if (segment is null)
            {
                return false;
            }

            _segments.Remove(segment);
            segment.Text = string.Empty;
            segment.IsEdited = true;
            // The journal is append only, so a deletion is recorded as an update
            // to empty text; replaying it reproduces the deletion.
            _journal?.UpdateSegment(segment);
        }

        SegmentRemoved?.Invoke(segment);
        return true;
    }

    /// <summary>Inserts a segment the user typed in by hand.</summary>
    public TranscriptSegment InsertManual(long startMs, AudioSourceKind source, string text)
    {
        var segment = new TranscriptSegment
        {
            StartMs = startMs,
            EndMs = startMs,
            Source = source,
            Text = text,
            IsEdited = true,
        };

        Add(segment);
        return segment;
    }

    /// <summary>
    /// Renames a diarization cluster and applies it to every segment of that
    /// cluster in this meeting, as the requirements ask.
    /// </summary>
    public int RenameSpeaker(AudioSourceKind source, int speakerId, string name)
    {
        List<TranscriptSegment> changed;
        lock (_sync)
        {
            _speakerNames[SpeakerKey(source, speakerId)] = name;
            changed = _segments.Where(s => s.Source == source && s.SpeakerId == speakerId).ToList();
            foreach (var segment in changed)
            {
                segment.SpeakerName = name;
                _journal?.UpdateSegment(segment);
            }
        }

        foreach (var segment in changed)
        {
            SegmentChanged?.Invoke(segment);
        }

        return changed.Count;
    }

    public IReadOnlyDictionary<string, string> SpeakerNames
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, string>(_speakerNames, StringComparer.Ordinal);
            }
        }
    }

    public void RestoreSpeakerNames(IReadOnlyDictionary<string, string> names)
    {
        lock (_sync)
        {
            foreach (var pair in names)
            {
                _speakerNames[pair.Key] = pair.Value;
            }

            foreach (var segment in _segments)
            {
                ApplyKnownSpeakerName(segment);
            }
        }
    }

    public IReadOnlyList<TranscriptSegment> Snapshot()
    {
        lock (_sync)
        {
            return _segments.Select(Copy).ToList();
        }
    }

    /// <summary>Distinct (source, speaker) pairs seen so far, for the rename UI.</summary>
    public IReadOnlyList<(AudioSourceKind Source, int SpeakerId, string? Name)> Speakers()
    {
        lock (_sync)
        {
            return _segments
                .Where(s => s.SpeakerId.HasValue)
                .Select(s => (s.Source, SpeakerId: s.SpeakerId!.Value))
                .Distinct()
                .OrderBy(s => s.Source)
                .ThenBy(s => s.SpeakerId)
                .Select(s => (s.Source, s.SpeakerId, _speakerNames.GetValueOrDefault(SpeakerKey(s.Source, s.SpeakerId))))
                .ToList();
        }
    }

    public static string SpeakerKey(AudioSourceKind source, int speakerId) => $"{source}:{speakerId}";

    private void ApplyKnownSpeakerName(TranscriptSegment segment)
    {
        if (segment.SpeakerId is { } id && _speakerNames.TryGetValue(SpeakerKey(segment.Source, id), out var name))
        {
            segment.SpeakerName = name;
        }
    }

    private void InsertOrdered(TranscriptSegment segment)
    {
        // Chunks from the two streams complete out of order, so keep the list
        // sorted by start time as they arrive; the UI shows it directly.
        var index = _segments.FindLastIndex(s => s.StartMs <= segment.StartMs);
        _segments.Insert(index + 1, segment);
    }

    private static TranscriptSegment Copy(TranscriptSegment source) => new()
    {
        Id = source.Id,
        StartMs = source.StartMs,
        EndMs = source.EndMs,
        Source = source.Source,
        SpeakerId = source.SpeakerId,
        SpeakerName = source.SpeakerName,
        Text = source.Text,
        IsEdited = source.IsEdited,
        Confidence = source.Confidence,
    };
}
