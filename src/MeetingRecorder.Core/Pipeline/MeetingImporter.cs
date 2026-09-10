using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;

namespace MeetingRecorder.Core.Pipeline;

/// <summary>
/// Brings an audio file the user already has into the application, so it can be
/// transcribed by the same path a recording is.
/// </summary>
/// <remarks>
/// <para><b>The imported file is input, and input is read-only.</b> It is
/// opened for reading and nothing else: not moved, not renamed, not re-encoded
/// in place, not deleted when the meeting folder is tidied up. Its path is
/// written into the metadata so the working audio can be produced again later,
/// which is the only lasting relationship the two files have.
/// </para>
/// <para><b>No source is inferred.</b> A recording made here has two files and
/// therefore knows which words came from the room and which came from the
/// speakers. One imported file has one source; who is speaking in it is not
/// written down anywhere, and this class does not invent it. Every segment from
/// an import is labelled <see cref="AudioSourceKind.Imported"/> and speaker
/// naming is not offered.
/// </para>
/// </remarks>
public sealed class MeetingImporter
{
    private readonly AudioImportService _importService;
    private readonly ILogger _logger;

    public MeetingImporter(AudioImportService importService, ILogger? logger = null)
    {
        _importService = importService;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Checks a candidate file without touching it, so the UI can refuse early and say why.</summary>
    public bool CanImport(string sourcePath, out string? reason) => _importService.CanImport(sourcePath, out reason);

    /// <summary>
    /// Creates a meeting folder for <paramref name="sourcePath"/> and decodes it
    /// into the 16 kHz working audio a transcription reads.
    /// </summary>
    /// <param name="title">
    /// Folder name suffix. Defaults to the imported file's own name, which is
    /// what makes the folder findable afterwards.
    /// </param>
    public MeetingSummary Import(
        AppSettings settings,
        string sourcePath,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.SaveRoot))
        {
            throw new InvalidOperationException("保存先が設定されていません。設定画面で保存先を指定してください。");
        }

        if (!_importService.CanImport(sourcePath, out var reason))
        {
            throw new NotSupportedException(reason ?? "この音声ファイルは読み込めません。");
        }

        var importedAt = DateTimeOffset.Now;
        var name = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : title;

        Directory.CreateDirectory(settings.SaveRoot!);
        var folder = MeetingFolder.Create(settings.SaveRoot!, importedAt, name);

        var warnings = new List<string>();
        AudioImportResult result;
        try
        {
            result = _importService.Import(
                sourcePath,
                Path.Combine(folder.Path, RecognitionAudioNames.Imported),
                settings.Processing,
                cancellationToken);
        }
        catch
        {
            // An import that failed leaves no half-built meeting behind. The
            // user's own file is untouched either way.
            TryRemoveEmptyFolder(folder);
            throw;
        }

        if (result.Scan.IsClipped)
        {
            warnings.Add(
                $"この音声は録音時点で既に歪んでいます（全体の {result.Scan.ClippedFraction * 100:F2}% が最大振幅）。"
                + "後処理では復元できないため、文字起こしの精度が落ちる可能性があります。");
        }

        var metadata = new MeetingMetadata
        {
            FolderName = folder.Name,
            Title = name,
            StartedAtLocal = importedAt,
            StoppedAtLocal = importedAt,
            DurationSeconds = result.DurationSeconds,
            AudioFileName = Path.GetFileName(sourcePath),
            Format = RecordingFormat.Wav,
            SampleRate = Stt.SpeechConstants.SampleRate,
            Channels = 1,
            IsImported = true,
            ImportedFromPath = Path.GetFullPath(sourcePath),
            SourceClipped = result.Scan.IsClipped,
            Warnings = warnings,
        };

        folder.WriteMetadata(metadata);

        _logger.Info(
            nameof(MeetingImporter),
            $"Imported '{sourcePath}' into '{folder.Name}' ({result.DurationSeconds:F1}s). The source file was not modified.");

        // AudioPath points at the user's own file: it is the audio of this
        // meeting, and it stays where they put it.
        return new MeetingSummary(folder, metadata, Path.GetFullPath(sourcePath), warnings, folder.Path);
    }

    /// <summary>
    /// Re-creates the working audio for an imported meeting whose copy was
    /// deleted, when the original file is still where it was.
    /// </summary>
    public bool TryReimport(MeetingSummary summary, AppSettings settings, out string? reason)
    {
        reason = null;

        if (!summary.IsImported || string.IsNullOrWhiteSpace(summary.Metadata.ImportedFromPath))
        {
            reason = "この会議はインポートされた音声ではありません。";
            return false;
        }

        var source = summary.Metadata.ImportedFromPath!;
        if (!File.Exists(source))
        {
            reason = $"元の音声ファイルが見つかりません（{source}）。移動または削除された可能性があります。";
            return false;
        }

        try
        {
            _importService.Import(
                source,
                Path.Combine(summary.Folder.Path, RecognitionAudioNames.Imported),
                settings.Processing);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(MeetingImporter), $"Re-importing '{source}' failed.", ex);
            reason = $"元のファイルから作業用音声を作り直せませんでした（{ex.Message}）。";
            return false;
        }
    }

    private void TryRemoveEmptyFolder(MeetingFolder folder)
    {
        try
        {
            if (Directory.Exists(folder.Path) && Directory.GetFileSystemEntries(folder.Path).Length == 0)
            {
                Directory.Delete(folder.Path);
            }
        }
        catch (IOException ex)
        {
            _logger.Warn(nameof(MeetingImporter), $"Could not remove the empty meeting folder: {ex.Message}");
        }
    }
}
