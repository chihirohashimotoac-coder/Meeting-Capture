using System.Text;
using System.Text.Json;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Persistence;

public enum JournalEntryKind
{
    Header,
    Segment,
    SegmentUpdate,
    Progress,
    Note,
    Completed,
}

/// <summary>One line of the append-only journal.</summary>
public sealed class JournalEntry
{
    public JournalEntryKind Kind { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;

    public MeetingMetadata? Metadata { get; set; }

    public TranscriptSegment? Segment { get; set; }

    public double? DurationSeconds { get; set; }

    public string? Message { get; set; }
}

/// <summary>
/// Append-only crash journal, one JSON object per line.
/// </summary>
/// <remarks>
/// <para>
/// The requirement is blunt: losing a two hour meeting because the user never
/// pressed Stop is unacceptable. So nothing about a meeting depends on a clean
/// shutdown. The WAV is written continuously with a self-healing header, and
/// every recognized segment is appended here and flushed within
/// <see cref="AppSettings.AutoSaveIntervalSeconds"/>.
/// </para>
/// <para>
/// JSON Lines is used rather than a rewritten JSON document because appending
/// can never corrupt what is already on disk: a torn last line is simply
/// skipped when reading, and everything before it survives.
/// </para>
/// </remarks>
public sealed class RecoveryJournal : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private readonly ILogger _logger;
    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private bool _disposed;

    public RecoveryJournal(string path, ILogger? logger = null)
    {
        Path = path;
        _logger = logger ?? NullLogger.Instance;
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
    }

    public string Path { get; }

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(15);

    public void WriteHeader(MeetingMetadata metadata)
        => Append(new JournalEntry { Kind = JournalEntryKind.Header, Metadata = metadata }, force: true);

    public void AppendSegment(TranscriptSegment segment)
        => Append(new JournalEntry { Kind = JournalEntryKind.Segment, Segment = segment });

    public void UpdateSegment(TranscriptSegment segment)
        => Append(new JournalEntry { Kind = JournalEntryKind.SegmentUpdate, Segment = segment });

    public void ReportProgress(double durationSeconds)
        => Append(new JournalEntry { Kind = JournalEntryKind.Progress, DurationSeconds = durationSeconds });

    public void Note(string message)
        => Append(new JournalEntry { Kind = JournalEntryKind.Note, Message = message }, force: true);

    public void MarkCompleted()
        => Append(new JournalEntry { Kind = JournalEntryKind.Completed }, force: true);

    private void Append(JournalEntry entry, bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(entry, JsonStore.Options.WriteIndented
                    ? new JsonSerializerOptions(JsonStore.Options) { WriteIndented = false }
                    : JsonStore.Options));

                if (force || DateTime.UtcNow - _lastFlushUtc >= FlushInterval)
                {
                    _writer.Flush();
                    _lastFlushUtc = DateTime.UtcNow;
                }
            }
            catch (IOException ex)
            {
                _logger.Error(nameof(RecoveryJournal), "Failed to append to the recovery journal.", ex);
            }
        }
    }

    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                _writer.Flush();
                _lastFlushUtc = DateTime.UtcNow;
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Reads a journal, skipping a torn final line.</summary>
    public static IReadOnlyList<JournalEntry> Read(string path)
    {
        var entries = new List<JournalEntry>();
        if (!File.Exists(path))
        {
            return entries;
        }

        var compact = new JsonSerializerOptions(JsonStore.Options) { WriteIndented = false };
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line, compact);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // Torn last line after a power loss - everything before it stands.
            }
        }

        return entries;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _disposed = true;
            try
            {
                _writer.Flush();
            }
            catch (IOException)
            {
            }

            _writer.Dispose();
        }
    }
}
