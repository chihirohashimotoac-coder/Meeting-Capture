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
/// <para><b>Why transcode after the fact instead of encoding live.</b> The
/// recording must survive a crash at any moment. A WAV with a self-healing
/// header does; a half-written MP3 frame stream is far more fragile. So the
/// meeting is always captured to WAV first, and only converted once the audio is
/// safely closed - and the WAV is deleted only after the MP3 exists and is
/// non-empty.</para>
///
/// <para><b>96 kbps.</b> Mono speech at 96 kbps is transparent for meeting
/// purposes, costs ~43 MB per hour against ~338 MB for 48 kHz 16-bit WAV, and
/// stays well above the bitrate where MP3 pre-echo starts to smear consonants
/// in a way that hurts later re-transcription.</para>
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

    public string Transcode(string sourceWavPath, string destinationPath, RecordingFormat format, int bitrateKbps)
    {
        if (format == RecordingFormat.Wav)
        {
            return sourceWavPath;
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
            MediaFoundationEncoder.EncodeToMp3(reader, destinationPath, bitrateKbps * 1000);
            _logger.Info(nameof(MediaFoundationTranscoder), $"Encoded MP3 at {bitrateKbps} kbps -> {destinationPath}");
            return destinationPath;
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
