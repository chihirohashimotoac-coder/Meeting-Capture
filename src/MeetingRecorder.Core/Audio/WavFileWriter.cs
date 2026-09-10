using System.Text;

namespace MeetingRecorder.Core.Audio;

/// <summary>
/// Minimal 16-bit PCM RIFF writer with a self-healing header.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand rather than taken from an audio library for one reason: the
/// header is rewritten on every flush, so a process that is killed - power loss,
/// a forced reboot, a crash - leaves behind a WAV whose declared length matches
/// what actually reached the disk. Recovering a meeting then needs no repair
/// step at all; the file simply opens.
/// </para>
/// <para>
/// 16-bit PCM at the engine's 48 kHz mono costs 96 kB/s (~338 MB/hour). That is
/// the format every transcription tool and every player accepts without
/// question, and the MP3 option exists for users who care about the size.
/// </para>
/// </remarks>
public sealed class WavFileWriter : IAudioFileWriter
{
    private const int HeaderSize = 44;

    private readonly FileStream _stream;
    private readonly byte[] _scratch = new byte[8192];
    private bool _disposed;

    public WavFileWriter(string filePath, int sampleRate, int channels = 1)
    {
        FilePath = filePath;
        SampleRate = sampleRate;
        Channels = channels;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        WriteHeader(0);
    }

    public string FilePath { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public long SamplesWritten { get; private set; }

    public double DurationSeconds => SamplesWritten / (double)(SampleRate * Channels);

    public void Write(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var offset = 0;
        while (offset < samples.Length)
        {
            var take = Math.Min((_scratch.Length / 2), samples.Length - offset);
            var byteCount = 0;
            for (var i = 0; i < take; i++)
            {
                // Nothing that reaches this writer should ever be out of range:
                // the mixer's constant -6 dB makes a full-scale sum impossible
                // and the peak normalizer picks a gain that lands under the
                // target. This is the arithmetic backstop for a NaN or a stray
                // float, not a clipping strategy - a wrapped sample is an
                // audible click in an archived meeting and there is no second
                // chance to fix it. The DSP tests assert it never engages.
                var value = samples[offset + i];
                if (value > 1f)
                {
                    value = 1f;
                }
                else if (value < -1f)
                {
                    value = -1f;
                }

                var pcm = (short)Math.Round(value * short.MaxValue);
                _scratch[byteCount++] = (byte)(pcm & 0xFF);
                _scratch[byteCount++] = (byte)((pcm >> 8) & 0xFF);
            }

            _stream.Write(_scratch, 0, byteCount);
            offset += take;
            SamplesWritten += take;
        }
    }

    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        _stream.Flush();
        var position = _stream.Position;
        WriteHeader(SamplesWritten * 2);
        _stream.Seek(position, SeekOrigin.Begin);
        _stream.Flush(flushToDisk: true);
    }

    private void WriteHeader(long dataBytes)
    {
        var byteRate = SampleRate * Channels * 2;
        var blockAlign = (short)(Channels * 2);

        _stream.Seek(0, SeekOrigin.Begin);
        using var writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8.ToArray());
        writer.Write((uint)(HeaderSize - 8 + dataBytes));
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16u);
        writer.Write((short)1); // PCM
        writer.Write((short)Channels);
        writer.Write((uint)SampleRate);
        writer.Write((uint)byteRate);
        writer.Write(blockAlign);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write((uint)dataBytes);
        writer.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Flush();
        }
        catch (IOException)
        {
            // Nothing useful to do while tearing down; the header may stay stale
            // but the audio bytes are already on disk.
        }
        finally
        {
            _disposed = true;
            _stream.Dispose();
        }
    }
}
