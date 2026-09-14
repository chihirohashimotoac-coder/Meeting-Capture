using System.Globalization;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Audio;

/// <summary>
/// What Windows actually handed over for one capture stream, and what this
/// application converted it into.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "the microphone sounds muffled" has several possible
/// causes and only one of them is fixable in software. If the endpoint hands
/// over 16 kHz audio, its Nyquist frequency is 8 kHz and everything above that
/// was gone before a single line of this application ran - no amount of
/// up-sampling puts it back, and claiming otherwise would be a lie told with
/// arithmetic. So the format is recorded verbatim, and the user is told when
/// the device itself is the limit.
/// </para>
/// <para>
/// It is a plain record in the platform-neutral project so the whole diagnosis
/// can be unit tested without an audio endpoint.
/// </para>
/// </remarks>
/// <param name="Kind">Which capture leg this describes.</param>
/// <param name="DeviceName">Friendly name of the endpoint Windows resolved.</param>
/// <param name="SourceSampleRate">Sample rate the endpoint delivers, in Hz.</param>
/// <param name="SourceChannels">Channel count the endpoint delivers.</param>
/// <param name="BitsPerSample">Bit depth the endpoint delivers.</param>
/// <param name="Encoding">Sample encoding, as WASAPI reports it (e.g. "IeeeFloat").</param>
/// <param name="MixFormat">
/// The endpoint's shared-mode mix format, formatted for a human. This is the
/// format configured in the Windows sound control panel, and on a capture
/// endpoint it is the one setting that decides the available bandwidth.
/// </param>
/// <param name="InternalSampleRate">Rate this application converts to (48 kHz).</param>
public readonly record struct CaptureFormatReport(
    AudioSourceKind Kind,
    string DeviceName,
    int SourceSampleRate,
    int SourceChannels,
    int BitsPerSample,
    string Encoding,
    string MixFormat,
    int InternalSampleRate)
{
    /// <summary>
    /// Highest frequency the endpoint can physically deliver, in Hz.
    /// </summary>
    public double SourceNyquistHz => SourceSampleRate / 2.0;

    /// <summary>
    /// True when the endpoint delivers less bandwidth than the internal rate
    /// can carry, so the recording is band-limited by the device and not by
    /// anything this application does.
    /// </summary>
    /// <remarks>
    /// The comparison is against the internal rate rather than a fixed number,
    /// so it stays correct if the internal rate ever changes. A 44.1 kHz
    /// endpoint trips it too, which is honest: 22.05 kHz of bandwidth really is
    /// less than 24 kHz. What matters for intelligibility is
    /// <see cref="LimitsTheSpeechBand"/>.
    /// </remarks>
    public bool IsBandLimited => SourceSampleRate < InternalSampleRate;

    /// <summary>
    /// True when the endpoint cannot deliver the whole speech band.
    /// </summary>
    /// <remarks>
    /// 16 kHz sampling gives 8 kHz of bandwidth. Japanese fricatives and
    /// sibilants (/s/, /ʃ/, /ts/) carry most of their energy between 4 and
    /// 10 kHz, so an endpoint at or below 16 kHz audibly dulls consonants -
    /// which is precisely what "muffled" describes. Above that the loss is real
    /// but not in the band that carries intelligibility.
    /// </remarks>
    public bool LimitsTheSpeechBand => SourceSampleRate <= 16000;

    /// <summary>True when no sample-rate conversion happens at all.</summary>
    public bool IsPassThrough => SourceSampleRate == InternalSampleRate;

    public string Label => Kind switch
    {
        AudioSourceKind.Microphone => "マイク",
        AudioSourceKind.SystemAudio => "PC内部音声",
        _ => Kind.ToString(),
    };

    /// <summary>
    /// The multi-line form written to the log, in the shape the user asked to
    /// be able to read back.
    /// </summary>
    public string ToLogBlock()
    {
        var lines = new List<string>
        {
            $"{Kind} capture format:",
            $"  Device           = {DeviceName}",
            $"  SourceFormat     = {SourceSampleRate} Hz / {SourceChannels} ch / {BitsPerSample}-bit {Encoding}",
            $"  WASAPI MixFormat = {MixFormat}",
            $"  InternalFormat   = {InternalSampleRate} Hz / mono",
            string.Create(
                CultureInfo.InvariantCulture,
                $"  Conversion       = {(IsPassThrough ? "pass-through (no resampling)" : $"resampled {SourceSampleRate} -> {InternalSampleRate} Hz")}"),
            string.Create(CultureInfo.InvariantCulture, $"  Source Nyquist   = {SourceNyquistHz:F0} Hz"),
        };

        if (LimitsTheSpeechBand)
        {
            lines.Add(
                "  NOTE             = the endpoint itself is band-limited; content above "
                + $"{SourceNyquistHz:F0} Hz never reached this application and cannot be restored.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The one-sentence Japanese warning shown to the user, or null when the
    /// device is not the limiting factor.
    /// </summary>
    public string? UserWarning => LimitsTheSpeechBand
        ? $"選択中の{Label}デバイス「{DeviceName}」は入力帯域が限定されています"
          + $"（{SourceSampleRate} Hz、上限 {SourceNyquistHz:F0} Hz）。"
          + "この帯域より上の音はアプリに届く前に失われているため、録音側では復元できません。"
          + "Windowsの「サウンドの詳細設定 > 入力デバイスのプロパティ > 詳細」で、"
          + "より高いサンプルレートを選べる場合は変更してください。"
        : null;
}
