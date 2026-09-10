using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Stt;

namespace MeetingRecorder.Core.Audio;

/// <summary>The audio containers the import feature accepts.</summary>
/// <remarks>
/// The list is short on purpose: these are the formats a meeting arrives in
/// (a recorder app, a conferencing client's own export, a voice memo). Anything
/// else is refused by name rather than attempted and failed halfway through a
/// decode.
/// </remarks>
public static class AudioImportFormats
{
    public static IReadOnlyList<string> Extensions { get; } = new[] { ".wav", ".mp3", ".m4a", ".aac" };

    /// <summary>Filter string for the Windows file dialog.</summary>
    public static string FileDialogFilter =>
        "音声ファイル (*.wav;*.mp3;*.m4a;*.aac)|*.wav;*.mp3;*.m4a;*.aac"
        + "|WAV (*.wav)|*.wav|MP3 (*.mp3)|*.mp3|MPEG-4 音声 (*.m4a;*.aac)|*.m4a;*.aac"
        + "|すべてのファイル (*.*)|*.*";

    public static bool IsSupportedExtension(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Decodes an audio file into the 16 kHz mono the speech engine consumes.
/// </summary>
/// <remarks>
/// Kept behind an interface for the same reason capture is: the decoder that
/// actually ships is a Windows one, and the import pipeline still has to be
/// testable on a runner that has no Media Foundation at all.
/// </remarks>
public interface IAudioDecoder
{
    /// <summary>Extensions this decoder is willing to open, lower case, with the dot.</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Whether this decoder can open the file, and if not, why not - in
    /// Japanese, for the user. A decoder must answer honestly here rather than
    /// claim support and fail during the decode.
    /// </summary>
    bool CanDecode(string sourcePath, out string? reason);

    /// <summary>
    /// Decodes <paramref name="sourcePath"/> into a mono
    /// <paramref name="targetSampleRate"/> WAV at <paramref name="destinationPath"/>.
    /// </summary>
    /// <remarks>
    /// Implementations must open the source read-only and must never write to
    /// it, move it or delete it.
    /// </remarks>
    void DecodeToMonoWav(string sourcePath, string destinationPath, int targetSampleRate);
}

/// <summary>
/// The always-available decoder: PCM and IEEE-float WAV, handled by this
/// project's own reader.
/// </summary>
/// <remarks>
/// It needs no operating system codec, so a WAV import works on any machine and
/// in the test suite. Sample-rate conversion and the down-mix to mono are the
/// only things done to the samples - there is no gain stage here, because the
/// output of this class is what the recognizer reads.
/// </remarks>
public sealed class WavAudioDecoder : IAudioDecoder
{
    public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".wav" };

    public bool CanDecode(string sourcePath, out string? reason)
    {
        reason = null;

        if (!string.Equals(Path.GetExtension(sourcePath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            reason = "この形式はWAVデコーダーでは扱えません。";
            return false;
        }

        try
        {
            using var probe = new WavFileReader(sourcePath);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"WAVファイルを読み取れません（{ex.Message}）。ファイルが壊れている可能性があります。";
            return false;
        }
    }

    public void DecodeToMonoWav(string sourcePath, string destinationPath, int targetSampleRate)
    {
        using var reader = new WavFileReader(sourcePath);
        var resampler = new Resampler(reader.SampleRate, targetSampleRate);

        using var writer = new WavFileWriter(destinationPath, targetSampleRate);
        var input = new float[1 << 16];
        var output = new float[resampler.EstimateOutputLength(input.Length)];

        int read;
        while ((read = reader.ReadMono(input)) > 0)
        {
            var written = resampler.Process(input.AsSpan(0, read), output);
            writer.Write(output.AsSpan(0, written));
        }

        writer.Flush();
    }
}

/// <summary>Tries each decoder in turn, so a platform decoder can extend a portable one.</summary>
public sealed class CompositeAudioDecoder : IAudioDecoder
{
    private readonly IReadOnlyList<IAudioDecoder> _decoders;

    public CompositeAudioDecoder(params IAudioDecoder[] decoders)
    {
        _decoders = decoders;
        SupportedExtensions = decoders
            .SelectMany(d => d.SupportedExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> SupportedExtensions { get; }

    public bool CanDecode(string sourcePath, out string? reason)
    {
        string? firstReason = null;
        foreach (var decoder in _decoders)
        {
            if (decoder.CanDecode(sourcePath, out var decoderReason))
            {
                reason = null;
                return true;
            }

            firstReason ??= decoderReason;
        }

        reason = firstReason ?? "この形式に対応するデコーダーがありません。";
        return false;
    }

    public void DecodeToMonoWav(string sourcePath, string destinationPath, int targetSampleRate)
    {
        Exception? last = null;
        foreach (var decoder in _decoders)
        {
            if (!decoder.CanDecode(sourcePath, out _))
            {
                continue;
            }

            try
            {
                decoder.DecodeToMonoWav(sourcePath, destinationPath, targetSampleRate);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new NotSupportedException($"'{Path.GetFileName(sourcePath)}' を読み込めるデコーダーがありません。");
    }
}

/// <param name="Path">Where the decoded 16 kHz mono working audio was written.</param>
/// <param name="DurationSeconds">Length of the decoded audio.</param>
/// <param name="Scan">Peak and clipping measured on the decoded audio.</param>
public readonly record struct AudioImportResult(string Path, double DurationSeconds, AudioPeakScan Scan);

/// <summary>
/// Turns a file the user already has into something this application can
/// transcribe.
/// </summary>
/// <remarks>
/// <para>
/// The imported file is input, and input is read-only. It is opened for
/// reading, decoded into a new working file inside a new meeting folder, and
/// then left exactly where and as it was - not moved, not re-encoded in place,
/// not deleted afterwards. If the import goes wrong the user still has the only
/// copy that mattered.
/// </para>
/// <para>
/// The working file is 16 kHz mono PCM because that is what whisper.cpp
/// consumes; converting once here is cheaper and more predictable than
/// converting the same audio again on every re-run. Nothing else is done to it:
/// no gain, no filtering, no gating. What the decoder produced is what the
/// recognizer sees.
/// </para>
/// </remarks>
public sealed class AudioImportService
{
    private readonly IAudioDecoder _decoder;
    private readonly ILogger _logger;

    public AudioImportService(IAudioDecoder decoder, ILogger? logger = null)
    {
        _decoder = decoder;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Checks a candidate file without touching it, so the UI can refuse early
    /// and say why.
    /// </summary>
    public bool CanImport(string sourcePath, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            reason = "ファイルが見つかりません。移動または削除された可能性があります。";
            return false;
        }

        if (!AudioImportFormats.IsSupportedExtension(sourcePath))
        {
            reason = $"対応していない形式です。{string.Join(" / ", AudioImportFormats.Extensions)} を選んでください。";
            return false;
        }

        try
        {
            using var probe = File.OpenRead(sourcePath);
            if (probe.Length == 0)
            {
                reason = "ファイルが空です。";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"ファイルを開けません（{ex.Message}）。別のアプリが使用中でないか確認してください。";
            return false;
        }

        return _decoder.CanDecode(sourcePath, out reason);
    }

    /// <summary>
    /// Decodes <paramref name="sourcePath"/> into <paramref name="destinationPath"/>
    /// as 16 kHz mono PCM.
    /// </summary>
    /// <exception cref="NotSupportedException">The format cannot be decoded on this machine.</exception>
    public AudioImportResult Import(
        string sourcePath,
        string destinationPath,
        Models.AudioProcessingSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!CanImport(sourcePath, out var reason))
        {
            throw new NotSupportedException(reason ?? "この音声ファイルは読み込めません。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            _decoder.DecodeToMonoWav(sourcePath, destinationPath, SpeechConstants.SampleRate);
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath);
            throw new InvalidOperationException(
                $"音声の変換に失敗しました（{ex.Message}）。"
                + "ファイルが壊れているか、この環境にコーデックがない可能性があります。元のファイルは変更していません。",
                ex);
        }

        using var reader = new WavFileReader(destinationPath);
        var duration = reader.DurationSeconds;
        var scan = PeakNormalizer.Scan(reader, settings);

        if (duration <= 0)
        {
            TryDelete(destinationPath);
            throw new InvalidOperationException(
                "変換結果に音声が含まれていませんでした。ファイルが壊れている可能性があります。元のファイルは変更していません。");
        }

        _logger.Info(
            nameof(AudioImportService),
            $"Imported '{Path.GetFileName(sourcePath)}' as {duration:F1}s of 16 kHz mono "
            + $"(peak {scan.PeakDbFs:F1} dBFS, clipped={scan.IsClipped}).");

        return new AudioImportResult(destinationPath, duration, scan);
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
            _logger.Warn(nameof(AudioImportService), $"Could not remove the partial working file: {ex.Message}");
        }
    }
}
