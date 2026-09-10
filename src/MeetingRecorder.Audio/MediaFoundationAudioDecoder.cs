using System.Runtime.Versioning;
using MeetingRecorder.Core.Audio;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Dsp;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MeetingRecorder.Audio;

/// <summary>
/// Decodes MP3, M4A/AAC (and WAV) using the codecs that ship with Windows.
/// </summary>
/// <remarks>
/// <para><b>Why the OS codecs.</b> The alternatives all break a promise the
/// product makes. FFmpeg would mean shipping (or requiring) a separate binary;
/// a managed MP3/AAC decoder would add a licence to clear and a native library
/// to the supply chain; Python is explicitly out. Windows 10 and 11 both carry
/// MP3 and AAC decoders in Media Foundation, reachable through NAudio with no
/// installation, no administrator rights and nothing added to the ZIP.</para>
///
/// <para><b>What that costs, said plainly.</b> Media Foundation is part of
/// Windows, but not of every Windows: the N and KN editions ship without the
/// media stack until the Media Feature Pack is installed. On such a machine
/// MP3 and M4A/AAC import genuinely does not work, and this class says so -
/// <see cref="CanDecode"/> probes by actually opening the file, so the answer
/// comes from this machine rather than from an assumption. WAV import is
/// unaffected: it is handled by <see cref="WavAudioDecoder"/>, which needs no
/// codec at all.</para>
///
/// <para><b>Raw .aac.</b> A bare ADTS stream is not a container, and whether
/// Media Foundation opens one depends on the byte stream handlers registered on
/// the machine. It is offered because it usually works and it is probed rather
/// than assumed, exactly like the others - a file it cannot open is refused
/// with a reason, never accepted and then silently mis-decoded.</para>
///
/// <para><b>Nothing is done to the samples</b> beyond the down-mix to mono and
/// the sample-rate conversion the engine requires - both linear, both
/// unavoidable. The rate conversion uses this project's own
/// <see cref="Resampler"/> rather than the Media Foundation one so that
/// imported audio and recorded audio reach the recognizer through the same
/// anti-alias filter.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationAudioDecoder : IAudioDecoder
{
    private readonly ILogger _logger;

    public MediaFoundationAudioDecoder(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<string> SupportedExtensions { get; } = new[] { ".mp3", ".m4a", ".aac", ".wav" };

    public bool CanDecode(string sourcePath, out string? reason)
    {
        reason = null;

        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            reason = "この形式はWindowsの音声デコーダーの対象外です。";
            return false;
        }

        try
        {
            MediaFoundationApi.Startup();
            try
            {
                // Opening the file is the probe. A codec that is not installed
                // fails here, on this machine, for this file - which is the only
                // answer worth reporting.
                using var reader = new MediaFoundationReader(sourcePath);
                if (reader.WaveFormat.SampleRate <= 0 || reader.WaveFormat.Channels <= 0)
                {
                    reason = "音声ストリームが見つかりませんでした。動画のみのファイルか、壊れている可能性があります。";
                    return false;
                }

                return true;
            }
            finally
            {
                MediaFoundationApi.Shutdown();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(MediaFoundationAudioDecoder), $"Cannot decode '{sourcePath}': {ex.Message}");
            reason =
                $"この形式をこのPCで再生（デコード）できません（{ex.Message}）。"
                + "Windows N/KN エディションでは Media Feature Pack が必要です。"
                + "WAVに変換してから読み込むこともできます。";
            return false;
        }
    }

    public void DecodeToMonoWav(string sourcePath, string destinationPath, int targetSampleRate)
    {
        MediaFoundationApi.Startup();
        try
        {
            using var reader = new MediaFoundationReader(sourcePath);
            var samples = reader.ToSampleProvider();
            var channels = samples.WaveFormat.Channels;
            var sourceRate = samples.WaveFormat.SampleRate;

            var resampler = new Resampler(sourceRate, targetSampleRate);
            using var writer = new Core.Audio.WavFileWriter(destinationPath, targetSampleRate);

            var interleaved = new float[1 << 16];
            var mono = new float[interleaved.Length / Math.Max(1, channels)];
            var resampled = new float[resampler.EstimateOutputLength(mono.Length)];

            while (true)
            {
                // Whole frames only: handing the down-mixer a partial frame
                // would rotate the channel order for the rest of the file.
                var wanted = interleaved.Length - (interleaved.Length % channels);
                var read = samples.Read(interleaved, 0, wanted);
                if (read <= 0)
                {
                    break;
                }

                read -= read % channels;
                if (read <= 0)
                {
                    break;
                }

                var frames = AudioMath.DownmixToMono(interleaved.AsSpan(0, read), channels, mono);
                var written = resampler.Process(mono.AsSpan(0, frames), resampled);
                writer.Write(resampled.AsSpan(0, written));
            }

            writer.Flush();
        }
        finally
        {
            MediaFoundationApi.Shutdown();
        }
    }
}
