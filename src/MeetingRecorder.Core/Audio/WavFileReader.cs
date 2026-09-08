using System.Text;

namespace MeetingRecorder.Core.Audio;

/// <summary>Reads 8/16/24/32-bit PCM and 32-bit float WAV files as mono float.</summary>
/// <remarks>
/// Used by the tests, by the MP3 transcode step and by crash recovery, which has
/// to re-read an interrupted recording to re-run transcription over it.
/// </remarks>
public sealed class WavFileReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private long _dataStart;
    private long _dataBytes;

    public WavFileReader(string path)
    {
        // FileShare.ReadWrite, not FileShare.Read: the file may still be open for
        // writing by an in-progress recording. On Windows a reader that does not
        // permit the existing writer's access gets a sharing violation, so a
        // recording in progress could not be inspected - and the crash-safety
        // guarantee ("the file on disk is playable at any moment") would only
        // hold once the process had exited.
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        _reader = new BinaryReader(_stream, Encoding.ASCII);
        ParseHeader();
    }

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public int BitsPerSample { get; private set; }

    public bool IsFloat { get; private set; }

    public long TotalFrames => _dataBytes / Math.Max(1, Channels * (BitsPerSample / 8));

    public double DurationSeconds => SampleRate == 0 ? 0 : TotalFrames / (double)SampleRate;

    private void ParseHeader()
    {
        if (Encoding.ASCII.GetString(_reader.ReadBytes(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        _reader.ReadUInt32();
        if (Encoding.ASCII.GetString(_reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        while (_stream.Position < _stream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(_reader.ReadBytes(4));
            var chunkSize = _reader.ReadUInt32();
            var chunkStart = _stream.Position;

            if (chunkId == "fmt ")
            {
                const int WaveFormatPcm = 1;
                const int WaveFormatIeeeFloat = 3;
                const int WaveFormatExtensible = 0xFFFE;

                var format = (ushort)_reader.ReadInt16();
                Channels = _reader.ReadInt16();
                SampleRate = (int)_reader.ReadUInt32();
                _reader.ReadUInt32();
                _reader.ReadInt16();
                BitsPerSample = _reader.ReadInt16();
                IsFloat = format == WaveFormatIeeeFloat;

                if (format == WaveFormatExtensible && chunkSize >= 40)
                {
                    // WAVEFORMATEXTENSIBLE: the real encoding lives in the first
                    // two bytes of the SubFormat GUID. WASAPI shared mode hands
                    // out float in this shape, so parsing it matters.
                    _reader.ReadInt16(); // cbSize
                    _reader.ReadInt16(); // wValidBitsPerSample
                    _reader.ReadUInt32(); // dwChannelMask
                    var subFormat = (ushort)_reader.ReadInt16();
                    IsFloat = subFormat == WaveFormatIeeeFloat;
                    if (subFormat is not (WaveFormatPcm or WaveFormatIeeeFloat))
                    {
                        throw new NotSupportedException($"Unsupported WAV sub-format {subFormat}.");
                    }
                }
                else if (format is not (WaveFormatPcm or WaveFormatIeeeFloat or WaveFormatExtensible))
                {
                    throw new NotSupportedException($"Unsupported WAV format tag {format}.");
                }
            }
            else if (chunkId == "data")
            {
                _dataStart = chunkStart;
                // A file left behind by a crash can declare more data than it
                // holds; trust the file length, not the header.
                _dataBytes = Math.Min(chunkSize, (uint)(_stream.Length - chunkStart));
                break;
            }

            _stream.Seek(chunkStart + chunkSize + (chunkSize % 2), SeekOrigin.Begin);
        }

        if (_dataStart == 0 || SampleRate == 0)
        {
            throw new InvalidDataException("WAV file has no usable fmt/data chunk.");
        }

        _stream.Seek(_dataStart, SeekOrigin.Begin);
    }

    /// <summary>
    /// Reads the next block of frames, down-mixed to mono. Returns how many
    /// frames were written; 0 at the end of the data chunk.
    /// </summary>
    /// <remarks>
    /// Sequential rather than whole-file because an hour of 16 kHz mono is
    /// 230 MB as floats, and the second transcription pass walks a whole meeting.
    /// </remarks>
    public int ReadMono(Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        var bytesPerSample = BitsPerSample / 8;
        var frameBytes = bytesPerSample * Channels;
        var dataEnd = _dataStart + (long)_dataBytes;
        var position = _stream.Position;

        if (position < _dataStart)
        {
            _stream.Seek(_dataStart, SeekOrigin.Begin);
            position = _dataStart;
        }

        var remainingFrames = (dataEnd - position) / frameBytes;
        if (remainingFrames <= 0)
        {
            return 0;
        }

        var wanted = (int)Math.Min(destination.Length, remainingFrames);
        var raw = new byte[wanted * frameBytes];
        var read = _stream.Read(raw, 0, raw.Length);
        var frames = read / frameBytes;

        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < Channels; c++)
            {
                sum += ReadSample(raw, ((f * Channels) + c) * bytesPerSample);
            }

            destination[f] = (float)(sum / Channels);
        }

        return frames;
    }

    /// <summary>Reads the whole file, down-mixed to mono float in [-1, 1].</summary>
    public float[] ReadAllMono()
    {
        var bytesPerSample = BitsPerSample / 8;
        var raw = new byte[_dataBytes];
        _stream.Seek(_dataStart, SeekOrigin.Begin);
        var read = _stream.Read(raw, 0, raw.Length);
        var frames = read / (bytesPerSample * Channels);
        var result = new float[frames];

        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < Channels; c++)
            {
                var offset = ((f * Channels) + c) * bytesPerSample;
                sum += ReadSample(raw, offset);
            }

            result[f] = (float)(sum / Channels);
        }

        return result;
    }

    private double ReadSample(byte[] raw, int offset) => BitsPerSample switch
    {
        8 => (raw[offset] - 128) / 128.0,
        16 => BitConverter.ToInt16(raw, offset) / 32768.0,
        24 => ((raw[offset] | (raw[offset + 1] << 8) | ((sbyte)raw[offset + 2] << 16)) / 8388608.0),
        32 => IsFloat ? BitConverter.ToSingle(raw, offset) : BitConverter.ToInt32(raw, offset) / 2147483648.0,
        _ => throw new NotSupportedException($"Unsupported bit depth {BitsPerSample}."),
    };

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
