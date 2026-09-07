using System.Globalization;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Persistence;

/// <summary>
/// One meeting on disk:
/// <code>
/// 2026-09-07_1430_Meeting/
///   meeting.wav | meeting.mp3
///   transcript.txt
///   transcript.md
///   minutes.txt
///   minutes.md
///   metadata.json
///   session.journal   (only while recording / after a crash)
/// </code>
/// </summary>
public sealed class MeetingFolder
{
    public const string JournalFileName = "session.journal";
    public const string MetadataFileName = "metadata.json";

    private MeetingFolder(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    public string WavPath => System.IO.Path.Combine(Path, "meeting.wav");

    public string Mp3Path => System.IO.Path.Combine(Path, "meeting.mp3");

    public string TranscriptTextPath => System.IO.Path.Combine(Path, "transcript.txt");

    public string TranscriptMarkdownPath => System.IO.Path.Combine(Path, "transcript.md");

    public string MinutesTextPath => System.IO.Path.Combine(Path, "minutes.txt");

    public string MinutesMarkdownPath => System.IO.Path.Combine(Path, "minutes.md");

    public string MetadataPath => System.IO.Path.Combine(Path, MetadataFileName);

    public string JournalPath => System.IO.Path.Combine(Path, JournalFileName);

    public string AudioPath(RecordingFormat format)
        => format == RecordingFormat.Mp3 ? Mp3Path : WavPath;

    /// <summary>Creates (and returns) a new, uniquely named folder under <paramref name="saveRoot"/>.</summary>
    public static MeetingFolder Create(string saveRoot, DateTimeOffset startedAtLocal, string? title = null)
    {
        var baseName = BuildFolderName(startedAtLocal, title);
        var candidate = System.IO.Path.Combine(saveRoot, baseName);
        var suffix = 2;
        while (Directory.Exists(candidate))
        {
            candidate = System.IO.Path.Combine(saveRoot, $"{baseName}_{suffix++}");
        }

        Directory.CreateDirectory(candidate);
        return new MeetingFolder(candidate);
    }

    public static MeetingFolder Open(string path) => new(path);

    public static string BuildFolderName(DateTimeOffset startedAtLocal, string? title)
    {
        var stamp = startedAtLocal.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
        var suffix = string.IsNullOrWhiteSpace(title) ? "Meeting" : Sanitize(title!);
        return $"{stamp}_{suffix}";
    }

    /// <summary>
    /// Characters Windows rejects in a file or folder name.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than taken from <see cref="System.IO.Path.GetInvalidFileNameChars"/>
    /// because that method is host-relative: on Linux it returns only NUL and
    /// '/', so a name sanitised on a Linux CI runner would still be rejected by
    /// the Windows machine the product actually runs on.
    /// </remarks>
    private static readonly char[] WindowsInvalidNameChars =
        ("<>:\"/\\|?*" + new string(Enumerable.Range(0, 32).Select(c => (char)c).ToArray())).ToCharArray();

    /// <summary>Strips characters Windows rejects in a folder name.</summary>
    public static string Sanitize(string value)
    {
        var invalid = WindowsInvalidNameChars;
        var chars = value.Trim()
            .Select(c => invalid.Contains(c) || c == ' ' ? '_' : c)
            .ToArray();
        var cleaned = new string(chars).Trim('_');
        if (cleaned.Length > 60)
        {
            cleaned = cleaned[..60];
        }

        return cleaned.Length == 0 ? "Meeting" : cleaned;
    }

    public MeetingMetadata? ReadMetadata() => JsonStore.Read<MeetingMetadata>(MetadataPath);

    public void WriteMetadata(MeetingMetadata metadata) => JsonStore.WriteAtomic(MetadataPath, metadata);

    /// <summary>An unfinished journal is the marker for "this meeting crashed".</summary>
    public bool HasPendingJournal => File.Exists(JournalPath);
}
