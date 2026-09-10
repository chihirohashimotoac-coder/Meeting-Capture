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
/// <c>WithNoContext</c> because windows from the microphone file and from the
/// loopback file are handed to one engine instance. Letting whisper condition
/// the remote participant's sentence on what somebody in the room just said
/// produces confident, fluent, wrong text - the worst possible failure mode for
/// a meeting record. Context <i>within</i> a window is untouched, and windows
/// are cut at silences close to whisper's full 30-second receptive field, so
/// this costs nothing that a sentence actually needs.</para>
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
    private readonly SpeechRecognitionOptions _options;

    private WhisperProcessor? _processor;
    private string? _processorLanguage;
    private bool _disposed;

    public WhisperSpeechRecognizer(string modelPath, string modelId, SpeechRecognitionOptions options, ILogger? logger = null)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("音声認識モデルが見つかりません。", modelPath);
        }

        _logger = logger ?? NullLogger.Instance;
        _options = options with { Threads = Math.Max(1, options.Threads), BeamSize = Math.Max(1, options.BeamSize) };
        ModelId = modelId;
        ModelPath = modelPath;

        _factory = WhisperFactory.FromPath(modelPath);
        IsReady = true;
        _logger.Info(
            nameof(WhisperSpeechRecognizer),
            $"Loaded '{modelId}' from '{modelPath}' (threads={_options.Threads}, beam={_options.BeamSize}, "
            + $"temperatureInc={_options.TemperatureIncrement:F2}, prompt={(_options.InitialPrompt is null ? "none" : "set")}).");
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
            .WithThreads(_options.Threads)
            // Chunks from two independent streams share this engine; carrying
            // decoder context between them fabricates plausible-sounding text.
            .WithNoContext()
            .WithProbabilities()
            // The caller copies every string it keeps, so pooling would only add
            // a lifetime hazard for no benefit.
            .WithoutStringPool()
            .WithSegmentEventHandler(segment => _segmentHandler?.Invoke(segment));

        // A short prompt is not context from another speaker - it is style
        // guidance, and it is what makes Japanese come back punctuated rather
        // than as one unbroken run of kana.
        if (!string.IsNullOrWhiteSpace(_options.InitialPrompt))
        {
            builder = builder.WithPrompt(_options.InitialPrompt);
        }

        if (_options.TemperatureIncrement > 0f)
        {
            builder = builder.WithTemperatureInc(_options.TemperatureIncrement);
        }

        if (_options.BeamSize > 1)
        {
            builder = ((BeamSearchSamplingStrategyBuilder)builder.WithBeamSearchSamplingStrategy())
                .WithBeamSize(_options.BeamSize)
                .ParentBuilder;
        }
        else
        {
            // Only reachable if a caller explicitly asks for beam size 1.
            // Transcription does not: nothing is waiting for it, so it pays for
            // the beam search.
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

    public ISpeechRecognizer Create(string modelPath, SpeechRecognitionOptions options)
        => new WhisperSpeechRecognizer(modelPath, Path.GetFileNameWithoutExtension(modelPath), options, _logger);
}
