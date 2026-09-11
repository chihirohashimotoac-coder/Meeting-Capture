using System.Runtime.Versioning;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MeetingRecorder.Audio;

/// <summary>
/// Converts the finished WAV into MP3 using the MP3 encoder that ships with
/// Windows (Media Foundation).
/// </summary>
/// <remarks>
/// <para><b>Why the OS encoder.</b> The obvious alternative, LAME through
/// NAudio.Lame, would add an LGPL native DLL to the distribution and a licence
/// obligation to the ZIP. Windows 10 and 11 both include an MP3 encoder MFT, so
/// using it keeps the package smaller, keeps the third-party licence list
/// shorter, and removes one more native binary from the supply chain. If the MFT
/// is genuinely unavailable - N/KN editions without the Media Feature Pack - the
/// recording stays as WAV and the user is told, rather than the conversion
/// failing silently.</para>
///
/// <para><b>192 kbps, mono.</b> The rate is per channel, so mono at 192 kbps is
/// the allocation stereo gets at 384 - far past transparent for speech. It is the
/// default because the file is an archive of something that happened once, and
/// 86 MB an hour instead of 43 is a trade almost anybody would take to never
/// wonder whether the codec ate a word. Anyone who does not want a lossy file at
/// all has WAV, which is what the application records by default.</para>
///
/// <para><b>Why transcode after the fact instead of encoding live.</b> The
/// recording must survive a crash at any moment. A WAV with a self-healing
/// header does; a half-written MP3 frame stream is far more fragile. So the
/// meeting is always captured to WAV first, and only converted once the audio is
/// safely closed - and the WAV is deleted only after the MP3 exists and is
/// non-empty.</para>
///
/// <para><b>The bitrate is a request, not an instruction.</b> An MFT publishes a
/// fixed list of output media types and the encoder is given one of them; asking
/// for a rate that is not on the list gets the nearest one that is. So this class
/// picks the media type itself and reports the rate it actually got, and the
/// caller records that rather than what was asked for.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationTranscoder : IAudioTranscoder
{
    private readonly ILogger _logger;
    private readonly Lazy<bool> _mp3Available;

    public MediaFoundationTranscoder(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _mp3Available = new Lazy<bool>(ProbeMp3Encoder);
    }

    public bool IsFormatSupported(RecordingFormat format) => format switch
    {
        RecordingFormat.Wav => true,
        RecordingFormat.Mp3 => _mp3Available.Value,
        _ => false,
    };

    public TranscodeResult Transcode(string sourceWavPath, string destinationPath, RecordingFormat format, int bitrateKbps)
    {
        if (format == RecordingFormat.Wav)
        {
            return new TranscodeResult(sourceWavPath, 0);
        }

        if (format != RecordingFormat.Mp3)
        {
            throw new NotSupportedException($"Unsupported target format {format}.");
        }

        if (!_mp3Available.Value)
        {
            throw new NotSupportedException(
                "この環境ではWindowsのMP3エンコーダー（Media Foundation）が利用できません。"
                + "Windows N/KN エディションの場合は Media Feature Pack が必要です。");
        }

        MediaFoundationApi.Startup();
        try
        {
            using var reader = new AudioFileReader(sourceWavPath);

            // Choose the output type here rather than letting the convenience
            // overload do it, so the rate that was actually selected can be
            // reported instead of guessed.
            var mediaType = MediaFoundationEncoder.SelectMediaType(
                AudioSubtypes.MFAudioFormat_MP3, reader.WaveFormat, bitrateKbps * 1000);

            if (mediaType is null)
            {
                throw new NotSupportedException(
                    $"この環境のMP3エンコーダーは {reader.WaveFormat.SampleRate} Hz / "
                    + $"{reader.WaveFormat.Channels} ch を出力できません。");
            }

            var actualKbps = (int)Math.Round(mediaType.AverageBytesPerSecond * 8 / 1000.0);

            using (var encoder = new MediaFoundationEncoder(mediaType))
            {
                encoder.Encode(destinationPath, reader);
            }

            if (actualKbps != bitrateKbps)
            {
                _logger.Warn(
                    nameof(MediaFoundationTranscoder),
                    $"{bitrateKbps} kbps is not offered by this system's MP3 encoder; used {actualKbps} kbps instead.");
            }

            _logger.Info(
                nameof(MediaFoundationTranscoder),
                $"Encoded MP3 at {actualKbps} kbps ({reader.WaveFormat.SampleRate} Hz, "
                + $"{reader.WaveFormat.Channels} ch) -> {destinationPath}");

            return new TranscodeResult(destinationPath, actualKbps);
        }
        finally
        {
            MediaFoundationApi.Shutdown();
        }
    }

    private bool ProbeMp3Encoder()
    {
        try
        {
            MediaFoundationApi.Startup();
            try
            {
                var format = new WaveFormat(48000, 16, 1);
                var available = MediaFoundationEncoder
                    .GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_MP3)
                    .Any(mediaType => mediaType.SampleRate > 0);

                if (!available)
                {
                    _logger.Warn(nameof(MediaFoundationTranscoder), "No MP3 output media types are available on this system.");
                }

                GC.KeepAlive(format);
                return available;
            }
            finally
            {
                MediaFoundationApi.Shutdown();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(MediaFoundationTranscoder), $"MP3 encoder probe failed: {ex.Message}");
            return false;
        }
    }
}
