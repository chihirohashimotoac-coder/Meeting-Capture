using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Stt;
using Whisper.net;

namespace MeetingRecorder.Stt;

/// <summary>
/// <see cref="ISpeechRecognizer"/> backed by whisper.cpp through Whisper.net.
/// </summary>
/// <remarks>
/// <para><b>Why whisper.cpp</b> (recorded in full in docs/ARCHITECTURE.md): it
/// runs entirely on the CPU with no Python and no service, its ggml models are
/// MIT licensed, the quantized builds fit the memory budget of a 16 GB office
/// laptop, Japanese is among its stronger languages, and Whisper.net ships the
/// native binaries as an ordinary NuGet package so a self-contained publish
/// needs no extra build tooling.</para>
///
/// <para><b>No context carry-over.</b> The processor is configured with
/// <c>WithNoContext</c> because chunks from the microphone and from system audio
/// are interleaved on one engine instance. Letting whisper condition the remote
/// participant's sentence on what somebody in the room just said produces
/// confident, fluent, wrong text - the worst possible failure mode for a
/// meeting record.</para>
///
/// <para><b>No fabricated output.</b> Whisper is known to emit boiler-plate
/// ("ご視聴ありがとうございました") over near-silence. Rather than inventing a
/// text blacklist, segments the model itself flags with a high no-speech
/// probability are dropped; that is the engine's own measurement, not a guess.</para>
/// </remarks>
public sealed class WhisperSpeechRecognizer : ISpeechRecognizer
{
    /// <summary>
    /// whisper.cpp expects at least one second of audio; anything shorter is
    /// padded with silence rather than rejected, so a one-word reply still gets
    /// transcribed.
    /// </summary>
    private const int MinimumSamples = SpeechConstants.SampleRate;

    /// <summary>
    /// Above this the model is telling us the audio was not speech. Whisper's
    /// own hallucinated filler sits well above 0.8 while genuine quiet speech
    /// stays far below it.
    /// </summary>
    private const float NoSpeechRejectThreshold = 0.85f;

    private readonly object _sync = new();
    private readonly WhisperFactory _factory;
    private readonly ILogger _logger;
    private readonly int _threads;
    private readonly int _beamSize;

    private WhisperProcessor? _processor;
    private string? _processorLanguage;
    private bool _disposed;

    public WhisperSpeechRecognizer(string modelPath, string modelId, int threads, int beamSize = 1, ILogger? logger = null)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("音声認識モデルが見つかりません。", modelPath);
        }

        _logger = logger ?? NullLogger.Instance;
        _threads = Math.Max(1, threads);
        _beamSize = Math.Max(1, beamSize);
        ModelId = modelId;
        ModelPath = modelPath;

        _factory = WhisperFactory.FromPath(modelPath);
        IsReady = true;
        _logger.Info(nameof(WhisperSpeechRecognizer), $"Loaded '{modelId}' from '{modelPath}' (threads={_threads}, beam={_beamSize}).");
    }

    public string ModelId { get; }

    public string ModelPath { get; }

    public bool IsReady { get; private set; }

    public IReadOnlyList<RecognizedSpan> Transcribe(ReadOnlySpan<float> samples, string language, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (samples.Length == 0)
        {
            return Array.Empty<RecognizedSpan>();
        }

        float[] buffer;
        if (samples.Length < MinimumSamples)
        {
            buffer = new float[MinimumSamples];
            samples.CopyTo(buffer);
        }
        else
        {
            buffer = samples.ToArray();
        }

        var results = new List<RecognizedSpan>();
        var rejected = 0;

        lock (_sync)
        {
            var processor = GetProcessor(language);

            void OnSegment(SegmentData segment)
            {
                if (segment.NoSpeechProbability > NoSpeechRejectThreshold)
                {
                    rejected++;
                    return;
                }

                var text = segment.Text?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                results.Add(new RecognizedSpan(
                    (long)segment.Start.TotalMilliseconds,
                    (long)segment.End.TotalMilliseconds,
                    text,
                    segment.Probability));
            }

            _segmentHandler = OnSegment;
            try
            {
                processor.Process(buffer.AsSpan());
            }
            finally
            {
                _segmentHandler = null;
            }
        }

        if (rejected > 0)
        {
            _logger.Debug(nameof(WhisperSpeechRecognizer), $"Dropped {rejected} segment(s) flagged as non-speech by the model.");
        }

        return results;
    }

    private Action<SegmentData>? _segmentHandler;

    private WhisperProcessor GetProcessor(string language)
    {
        if (_processor is not null && string.Equals(_processorLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            return _processor;
        }

        _processor?.Dispose();

        var builder = _factory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(language) ? "ja" : language)
            .WithThreads(_threads)
            // Chunks from two independent streams share this engine; carrying
            // decoder context between them fabricates plausible-sounding text.
            .WithNoContext()
            .WithProbabilities()
            // The caller copies every string it keeps, so pooling would only add
            // a lifetime hazard for no benefit.
            .WithoutStringPool()
            .WithSegmentEventHandler(segment => _segmentHandler?.Invoke(segment));

        if (_beamSize > 1)
        {
            builder = ((BeamSearchSamplingStrategyBuilder)builder.WithBeamSearchSamplingStrategy())
                .WithBeamSize(_beamSize)
                .ParentBuilder;
        }
        else
        {
            // Greedy decoding is roughly twice as fast as a beam search and the
            // difference is marginal for meeting speech; the profile selector
            // only raises the beam size on machines with measured head-room.
            builder = builder.WithGreedySamplingStrategy().ParentBuilder;
        }

        _processor = builder.Build();
        _processorLanguage = language;
        return _processor;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsReady = false;

        lock (_sync)
        {
            _processor?.Dispose();
            _processor = null;
            _factory.Dispose();
        }
    }
}

/// <summary>Creates <see cref="WhisperSpeechRecognizer"/> instances.</summary>
public sealed class WhisperSpeechRecognizerFactory : ISpeechRecognizerFactory
{
    private readonly ILogger _logger;

    public WhisperSpeechRecognizerFactory(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public ISpeechRecognizer Create(string modelPath, int threads, int beamSize)
        => new WhisperSpeechRecognizer(modelPath, Path.GetFileNameWithoutExtension(modelPath), threads, beamSize, _logger);
}
