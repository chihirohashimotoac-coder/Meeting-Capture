using System.Globalization;
using MeetingRecorder.Core.Dsp;

namespace MeetingRecorder.Core.Audio;

/// <param name="Name">Human label for the band.</param>
/// <param name="LowHz">Lower edge, in Hz.</param>
/// <param name="HighHz">Upper edge, in Hz.</param>
/// <param name="Rms">Root-mean-square level inside the band, linear.</param>
public readonly record struct SpectralBand(string Name, double LowHz, double HighHz, double Rms)
{
    public double Db => AudioMath.LinearToDb(Rms);
}

/// <param name="Bands">One entry per analysis band, low to high.</param>
/// <param name="AnalysedSeconds">Audio that carried enough signal to be measured.</param>
/// <param name="TotalSeconds">Audio inspected in total.</param>
public readonly record struct SpectralBalanceReport(
    IReadOnlyList<SpectralBand> Bands,
    double AnalysedSeconds,
    double TotalSeconds)
{
    public bool HasSignal => AnalysedSeconds > 0.5 && Bands.Count > 0 && Bands[0].Rms > 0;

    /// <summary>Level of one band relative to the reference (lowest) band, in dB.</summary>
    public double RelativeDb(int index)
    {
        if (!HasSignal || index < 0 || index >= Bands.Count)
        {
            return AudioMath.MinDb;
        }

        return AudioMath.LinearToDb(Bands[index].Rms / Bands[0].Rms);
    }

    /// <summary>
    /// How much high-frequency energy this stream has, relative to its own
    /// low-mid energy, in dB. The single number that says "dull" or "bright".
    /// </summary>
    /// <remarks>
    /// The top band against the reference band. It is deliberately a ratio
    /// within one stream, so it does not move when somebody turns a volume
    /// knob, and two streams recorded at different levels can be compared
    /// directly.
    /// </remarks>
    public double HighBandTiltDb => Bands.Count == 0 ? AudioMath.MinDb : RelativeDb(Bands.Count - 1);

    public string ToLogBlock(string label)
    {
        if (!HasSignal)
        {
            return $"{label} spectral balance: not enough signal to measure "
                   + string.Create(CultureInfo.InvariantCulture, $"({AnalysedSeconds:F1}s of {TotalSeconds:F1}s above the floor).");
        }

        var lines = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"{label} spectral balance ({AnalysedSeconds:F0}s of {TotalSeconds:F0}s measured):"),
        };

        for (var i = 0; i < Bands.Count; i++)
        {
            var band = Bands[i];
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  {band.Name,-12} {band.LowHz,5:F0}-{band.HighHz,5:F0} Hz = {band.Db,7:F1} dBFS ({RelativeDb(i),+6:F1} dB rel.)"));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Measures how a recorded stream's energy is distributed across the speech
/// band.
/// </summary>
/// <remarks>
/// <para>
/// This exists to answer one question with a number instead of an adjective:
/// "the microphone sounds muffled compared to the PC audio" - is it, and by how
/// much? Both working-audio files are recorded through the same pipeline at the
/// same rate, so running the identical measurement over each turns a listening
/// impression into a comparison that can be put in a log and acted on.
/// </para>
/// <para>
/// It runs after a recording has stopped, over files that are already closed.
/// Nothing here executes while audio is being captured.
/// </para>
/// <para><b>How.</b> Four fourth-order band-passes (two cascaded biquads each),
/// RMS accumulated only over frames whose broadband level clears
/// <see cref="ActivityFloorDb"/> so that silence between sentences does not
/// dilute the result. Band levels are reported relative to the lowest band, so
/// the numbers do not move with recording level and two streams can be compared
/// directly.
/// </para>
/// <para>
/// It is a measurement and only a measurement: it changes no audio and no
/// setting. What it finds is written to the log and offered to the user, who
/// decides.
/// </para>
/// </remarks>
public static class SpectralBalance
{
    /// <summary>Frames quieter than this are treated as silence and skipped.</summary>
    public const double ActivityFloorDb = -50.0;

    private const double FrameMs = 20.0;

    /// <summary>
    /// The bands, low to high. The first is the reference every other band is
    /// quoted against.
    /// </summary>
    /// <remarks>
    /// The split is by what each region does for intelligibility: the first
    /// formant region carries the vowels and most of the loudness, 1-2 kHz
    /// carries the second formant, 2-4 kHz is where consonant place-of-
    /// articulation lives, and 4-7 kHz is the fricative and sibilant energy
    /// that goes missing first when a microphone or a codec is narrow. It is the
    /// top band that a listener hears as "muffled" when it is not there.
    /// </remarks>
    public static IReadOnlyList<(string Name, double Low, double High)> Bands { get; } = new[]
    {
        ("low-mid", 300.0, 1000.0),
        ("mid", 1000.0, 2000.0),
        ("consonant", 2000.0, 4000.0),
        ("sibilance", 4000.0, 7000.0),
    };

    public static SpectralBalanceReport Measure(string wavPath)
    {
        using var reader = new WavFileReader(wavPath);
        return Measure(reader);
    }

    public static SpectralBalanceReport Measure(WavFileReader reader)
    {
        var rate = reader.SampleRate;
        var nyquist = rate / 2.0;

        var usable = Bands.Where(b => b.Low < nyquist * 0.95).ToList();
        var filters = usable
            .Select(b =>
            {
                var high = Math.Min(b.High, nyquist * 0.95);
                var centre = Math.Sqrt(b.Low * high);
                var q = centre / Math.Max(1.0, high - b.Low);
                return new[] { Biquad.BandPass(rate, centre, q), Biquad.BandPass(rate, centre, q) };
            })
            .ToList();

        var frameSamples = Math.Max(1, (int)(rate * FrameMs / 1000.0));
        var frame = new float[frameSamples];
        var scratch = new float[frameSamples];

        var energy = new double[usable.Count];
        long analysedSamples = 0;
        long totalSamples = 0;

        while (true)
        {
            var read = reader.ReadMono(frame);
            if (read <= 0)
            {
                break;
            }

            totalSamples += read;
            var span = frame.AsSpan(0, read);
            var active = AudioMath.RmsDb(span) >= ActivityFloorDb;

            for (var b = 0; b < filters.Count; b++)
            {
                var target = scratch.AsSpan(0, read);
                span.CopyTo(target);

                // The filters run over every frame, active or not: they are
                // recursive, and skipping input would leave them in a state that
                // does not correspond to the audio that follows.
                filters[b][0].Process(target);
                filters[b][1].Process(target);

                if (active)
                {
                    for (var i = 0; i < read; i++)
                    {
                        double s = target[i];
                        energy[b] += s * s;
                    }
                }
            }

            if (active)
            {
                analysedSamples += read;
            }
        }

        var bands = new List<SpectralBand>(usable.Count);
        for (var b = 0; b < usable.Count; b++)
        {
            var rms = analysedSamples > 0 ? Math.Sqrt(energy[b] / analysedSamples) : 0.0;
            bands.Add(new SpectralBand(usable[b].Name, usable[b].Low, Math.Min(usable[b].High, nyquist), rms));
        }

        return new SpectralBalanceReport(bands, analysedSamples / (double)rate, totalSamples / (double)rate);
    }

    /// <summary>
    /// Compares two streams and says, in Japanese, whether one of them really is
    /// duller than the other - or says nothing, when they are comparable.
    /// </summary>
    /// <param name="differenceDb">
    /// How much less high-band tilt the first stream has than the second, in dB.
    /// Positive means the first stream is duller.
    /// </param>
    /// <returns>
    /// A sentence for the user, or null when the difference is not large enough
    /// to be worth a claim. 6 dB is the threshold: it is roughly where a
    /// spectral tilt stops being arguable and starts being obvious, and staying
    /// quiet below it is what keeps this measurement from crying wolf.
    /// </returns>
    public static string? DescribeDifference(
        SpectralBalanceReport microphone,
        SpectralBalanceReport systemAudio,
        out double differenceDb)
    {
        differenceDb = 0.0;
        if (!microphone.HasSignal || !systemAudio.HasSignal)
        {
            return null;
        }

        differenceDb = systemAudio.HighBandTiltDb - microphone.HighBandTiltDb;
        if (differenceDb < 6.0)
        {
            return null;
        }

        return $"マイク音声の高域（4〜7 kHz）がPC内部音声より {differenceDb.ToString("F0", CultureInfo.InvariantCulture)} dB 少なく測定されました。"
               + "マイク側だけがこもって聞こえる場合、原因は録音デバイスまたはWindows側の設定である可能性が高いです。"
               + "ログの「Microphone capture format」の行で、マイクのサンプルレートを確認してください。";
    }
}
