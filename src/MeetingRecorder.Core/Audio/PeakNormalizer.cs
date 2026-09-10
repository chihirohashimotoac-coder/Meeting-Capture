using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Audio;

/// <summary>What a scan of a finished recording found.</summary>
/// <param name="Peak">Largest absolute sample value in the file (0..1+).</param>
/// <param name="SampleCount">Samples inspected.</param>
/// <param name="ClippedSamples">Samples at or beyond the clip threshold.</param>
/// <param name="IsClipped">
/// True when enough samples sit at full scale that the input itself was
/// over-driven. Reported, never "repaired": the flattened peaks are gone.
/// </param>
public readonly record struct AudioPeakScan(double Peak, long SampleCount, long ClippedSamples, bool IsClipped)
{
    public double PeakDbFs => AudioMath.LinearToDb(Peak);

    public double ClippedFraction => SampleCount <= 0 ? 0 : ClippedSamples / (double)SampleCount;
}

/// <param name="Applied">True when the file was rewritten.</param>
/// <param name="Gain">The constant every sample was multiplied by (1.0 when nothing was applied).</param>
/// <param name="Scan">What the pre-gain scan found.</param>
/// <param name="Reason">Why the gain is what it is, in Japanese, for the log and the UI.</param>
public readonly record struct NormalizationResult(bool Applied, double Gain, AudioPeakScan Scan, string Reason)
{
    public double GainDb => AudioMath.LinearToDb(Gain);
}

/// <summary>
/// Raises a finished recording to a comfortable listening level using one
/// constant gain for the whole file.
/// </summary>
/// <remarks>
/// <para><b>The entire algorithm.</b></para>
/// <code>
/// peak = max |input[n]|                     over the whole file
/// gain = min(10^(target/20) / peak, 10^(max/20))
/// output[n] = input[n] * gain               for every n, same gain
/// </code>
/// <para>
/// Because <c>gain</c> does not depend on <c>n</c>, the operation is linear and
/// time invariant. The ratio between any two samples is unchanged, so the onset
/// of a word, an unvoiced consonant, a quiet aside, a laugh and the silence
/// between them all keep exactly the relationship the microphone captured. That
/// is the property a compressor, a limiter or an AGC destroys, and it is why
/// none of them is used here.
/// </para>
/// <para><b>Clipping is prevented by choosing the gain, never by clamping.</b>
/// The gain is derived from the measured peak, so the loudest sample in the
/// file lands on <see cref="AudioProcessingSettings.NormalizationTargetPeakDbFs"/>
/// and nothing can land above it. There is no threshold test in the sample
/// loop, and no stage that flattens a value that came out too large - if the
/// result would be too hot, the gain itself was already reduced.
/// </para>
/// <para><b>Audio that arrived clipped stays clipped.</b> If the capture was
/// already over-driven the scan says so and the caller warns the user. Nothing
/// here pretends the lost peaks can be reconstructed.
/// </para>
/// </remarks>
public static class PeakNormalizer
{
    private const int ScanBlockSamples = 1 << 16;

    /// <summary>Measures the peak (and any clipping) of a mono WAV file.</summary>
    public static AudioPeakScan Scan(string path, AudioProcessingSettings settings)
    {
        using var reader = new WavFileReader(path);
        return Scan(reader, settings);
    }

    /// <summary>Measures the peak (and any clipping) of an already-open reader.</summary>
    public static AudioPeakScan Scan(WavFileReader reader, AudioProcessingSettings settings)
    {
        var buffer = new float[ScanBlockSamples];
        var threshold = settings.ClipDetectionThreshold;

        double peak = 0;
        long total = 0;
        long clipped = 0;

        int read;
        while ((read = reader.ReadMono(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var magnitude = Math.Abs((double)buffer[i]);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }

                if (magnitude >= threshold)
                {
                    clipped++;
                }
            }

            total += read;
        }

        var isClipped = total > 0
                        && clipped > 0
                        && clipped / (double)total >= settings.ClipDetectionSampleFraction;

        return new AudioPeakScan(peak, total, clipped, isClipped);
    }

    /// <summary>
    /// The constant gain a file with this peak should be multiplied by.
    /// </summary>
    /// <remarks>
    /// Returns 1.0 for silence: there is no peak to aim at, and multiplying
    /// zeros by anything is still zeros - but returning the cap instead would
    /// make the "silence stays silence" property depend on floating point luck.
    /// </remarks>
    public static double ComputeGain(double peak, AudioProcessingSettings settings)
    {
        if (peak <= 0 || double.IsNaN(peak) || double.IsInfinity(peak))
        {
            return 1.0;
        }

        var target = AudioMath.DbToLinear(settings.NormalizationTargetPeakDbFs);
        var maximum = AudioMath.DbToLinear(settings.MaxNormalizationGainDb);

        // Reducing is always allowed (it is what keeps an already-hot file under
        // the ceiling); only boosting is capped.
        return Math.Min(target / peak, maximum);
    }

    /// <summary>
    /// Scans <paramref name="path"/>, works out the one gain that whole file
    /// should have, and rewrites it with that gain applied.
    /// </summary>
    /// <remarks>
    /// The rewrite goes to a sibling temporary file and only replaces the
    /// original once it is complete, so a crash - or a full disk - during
    /// normalization leaves the recording exactly as the pump wrote it. Losing
    /// the volume adjustment is a disappointment; losing the meeting is not
    /// acceptable.
    /// </remarks>
    public static NormalizationResult Normalize(
        string path,
        AudioProcessingSettings settings,
        ILogger? logger = null,
        Func<string, int, IAudioFileWriter>? writerFactory = null)
    {
        var log = logger ?? NullLogger.Instance;

        AudioPeakScan scan;
        int sampleRate;
        using (var reader = new WavFileReader(path))
        {
            sampleRate = reader.SampleRate;
            scan = Scan(reader, settings);
        }

        if (!settings.PeakNormalizationEnabled)
        {
            return new NormalizationResult(false, 1.0, scan, "音量調整は設定で無効になっています。");
        }

        if (scan.SampleCount == 0 || scan.Peak <= 0)
        {
            return new NormalizationResult(false, 1.0, scan, "無音のため音量調整は行いませんでした。");
        }

        var gain = ComputeGain(scan.Peak, settings);
        var gainDb = AudioMath.LinearToDb(gain);

        if (Math.Abs(gainDb) < settings.NormalizationSkipThresholdDb)
        {
            return new NormalizationResult(
                false, 1.0, scan, $"すでに適切な音量です（ピーク {scan.PeakDbFs:F1} dBFS）。");
        }

        var temporary = path + ".normalizing.tmp";
        try
        {
            RewriteWithGain(path, temporary, sampleRate, (float)gain, writerFactory);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            log.Error(nameof(PeakNormalizer), $"Normalizing '{path}' failed; the original recording is untouched.", ex);
            TryDelete(temporary, log);
            return new NormalizationResult(
                false, 1.0, scan, $"音量調整に失敗したため、録音した音量のまま保存しました（{ex.Message}）。");
        }

        var reason = $"ファイル全体に一定の {gainDb:+0.0;-0.0;0.0} dB を適用しました"
                     + $"（ピーク {scan.PeakDbFs:F1} → {settings.NormalizationTargetPeakDbFs:F1} dBFS）。";

        log.Info(
            nameof(PeakNormalizer),
            $"Applied a single constant gain of {gainDb:F2} dB to '{Path.GetFileName(path)}' "
            + $"(peak {scan.PeakDbFs:F2} dBFS over {scan.SampleCount} samples).");

        return new NormalizationResult(true, gain, scan, reason);
    }

    private static void RewriteWithGain(
        string source,
        string destination,
        int sampleRate,
        float gain,
        Func<string, int, IAudioFileWriter>? writerFactory)
    {
        var buffer = new float[ScanBlockSamples];

        using var reader = new WavFileReader(source);
        var writer = writerFactory is null
            ? new WavFileWriter(destination, sampleRate)
            : writerFactory(destination, sampleRate);

        using (writer)
        {
            int read;
            while ((read = reader.ReadMono(buffer)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    // One multiply, the same factor for every sample in the file.
                    // No branch, no threshold, no envelope: that is the whole point.
                    buffer[i] *= gain;
                }

                writer.Write(buffer.AsSpan(0, read));
            }

            writer.Flush();
        }
    }

    private static void TryDelete(string path, ILogger logger)
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
            logger.Warn(nameof(PeakNormalizer), $"Could not remove '{path}': {ex.Message}");
        }
    }
}
