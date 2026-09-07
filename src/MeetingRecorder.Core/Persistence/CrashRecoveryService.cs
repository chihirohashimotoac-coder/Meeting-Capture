using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Persistence;

/// <param name="Folder">The meeting folder left behind.</param>
/// <param name="Metadata">Metadata reconstructed from the journal header.</param>
/// <param name="SegmentCount">How many transcript segments survived.</param>
/// <param name="AudioSeconds">Playable audio length measured from the WAV itself.</param>
public sealed record RecoverableMeeting(
    MeetingFolder Folder,
    MeetingMetadata Metadata,
    int SegmentCount,
    double AudioSeconds)
{
    public DateTimeOffset StartedAt => Metadata.StartedAtLocal;

    public string DisplayName => Metadata.Title ?? Folder.Name;
}

/// <summary>
/// Finds meetings that were interrupted by a crash and rebuilds their
/// deliverables from the audio file plus the append-only journal.
/// </summary>
public sealed class CrashRecoveryService
{
    private readonly ILogger _logger;

    public CrashRecoveryService(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Scans <paramref name="saveRoot"/> for meetings with an unfinished journal.</summary>
    public IReadOnlyList<RecoverableMeeting> Scan(string? saveRoot)
    {
        var result = new List<RecoverableMeeting>();
        if (string.IsNullOrWhiteSpace(saveRoot) || !Directory.Exists(saveRoot))
        {
            return result;
        }

        foreach (var directory in Directory.EnumerateDirectories(saveRoot))
        {
            var folder = MeetingFolder.Open(directory);
            if (!folder.HasPendingJournal)
            {
                continue;
            }

            try
            {
                var entries = RecoveryJournal.Read(folder.JournalPath);
                if (entries.Count == 0)
                {
                    continue;
                }

                if (entries[^1].Kind == JournalEntryKind.Completed)
                {
                    // Journal was completed but never deleted (e.g. the file was
                    // locked at shutdown). Nothing to recover; tidy it away.
                    TryDelete(folder.JournalPath);
                    continue;
                }

                var metadata = entries.FirstOrDefault(e => e.Kind == JournalEntryKind.Header)?.Metadata
                               ?? folder.ReadMetadata()
                               ?? new MeetingMetadata { FolderName = folder.Name, StartedAtLocal = Directory.GetCreationTime(directory) };

                var segments = RebuildSegments(entries);
                var audioSeconds = MeasureAudio(folder);

                result.Add(new RecoverableMeeting(folder, metadata, segments.Count, audioSeconds));
            }
            catch (Exception ex)
            {
                _logger.Error(nameof(CrashRecoveryService), $"Could not inspect '{directory}'.", ex);
            }
        }

        return result.OrderByDescending(r => r.StartedAt).ToList();
    }

    /// <summary>
    /// Rebuilds transcript.txt / transcript.md / metadata.json for an interrupted
    /// meeting and clears its journal.
    /// </summary>
    public MeetingMetadata Recover(RecoverableMeeting meeting)
    {
        var entries = RecoveryJournal.Read(meeting.Folder.JournalPath);
        var segments = RebuildSegments(entries);
        var metadata = meeting.Metadata;

        metadata.RecoveredFromCrash = true;
        metadata.DurationSeconds = MeasureAudio(meeting.Folder) is var seconds and > 0
            ? seconds
            : entries.LastOrDefault(e => e.DurationSeconds.HasValue)?.DurationSeconds ?? 0;
        metadata.StoppedAtLocal ??= metadata.StartedAtLocal.AddSeconds(metadata.DurationSeconds);
        if (!metadata.Warnings.Contains(RecoveryWarning))
        {
            metadata.Warnings.Add(RecoveryWarning);
        }

        TranscriptExporter.Write(meeting.Folder, metadata, segments);
        meeting.Folder.WriteMetadata(metadata);
        TryDelete(meeting.Folder.JournalPath);

        _logger.Info(
            nameof(CrashRecoveryService),
            $"Recovered '{meeting.Folder.Name}': {segments.Count} segments, {metadata.DurationSeconds:F1}s of audio.");
        return metadata;
    }

    /// <summary>
    /// Leaves the audio in place but stops offering the meeting for recovery.
    /// The user's recording is never deleted on their behalf.
    /// </summary>
    public void Dismiss(RecoverableMeeting meeting)
    {
        TryDelete(meeting.Folder.JournalPath);
        _logger.Info(nameof(CrashRecoveryService), $"Recovery dismissed for '{meeting.Folder.Name}'; audio kept.");
    }

    public const string RecoveryWarning = "異常終了から復旧したデータです。末尾が欠けている可能性があります。";

    public static List<TranscriptSegment> RebuildSegments(IReadOnlyList<JournalEntry> entries)
    {
        var byId = new Dictionary<string, TranscriptSegment>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.Segment is null)
            {
                continue;
            }

            switch (entry.Kind)
            {
                case JournalEntryKind.Segment:
                    if (!byId.ContainsKey(entry.Segment.Id))
                    {
                        order.Add(entry.Segment.Id);
                    }

                    byId[entry.Segment.Id] = entry.Segment;
                    break;

                case JournalEntryKind.SegmentUpdate:
                    if (byId.ContainsKey(entry.Segment.Id))
                    {
                        byId[entry.Segment.Id] = entry.Segment;
                    }
                    else
                    {
                        order.Add(entry.Segment.Id);
                        byId[entry.Segment.Id] = entry.Segment;
                    }

                    break;
            }
        }

        return order.Select(id => byId[id]).OrderBy(s => s.StartMs).ToList();
    }

    private double MeasureAudio(MeetingFolder folder)
    {
        try
        {
            if (!File.Exists(folder.WavPath))
            {
                return 0;
            }

            using var reader = new WavFileReader(folder.WavPath);
            return reader.DurationSeconds;
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(CrashRecoveryService), $"Could not measure '{folder.WavPath}': {ex.Message}");
            return 0;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.Warn(nameof(CrashRecoveryService), $"Could not delete '{path}': {ex.Message}");
        }
    }
}
