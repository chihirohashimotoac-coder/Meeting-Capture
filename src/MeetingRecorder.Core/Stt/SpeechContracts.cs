using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Stt;

/// <summary>Sample rate every speech engine in this application consumes.</summary>
public static class SpeechConstants
{
    /// <summary>whisper.cpp works internally at 16 kHz mono; feeding anything else just costs a resample.</summary>
    public const int SampleRate = 16000;
}

/// <param name="StartMs">Offset of the first sample from the start of the recording.</param>
/// <param name="Samples">16 kHz mono audio.</param>
/// <param name="Source">Ground-truth capture stream this audio came from.</param>
/// <param name="SequenceNumber">Monotonic per-source counter, for ordering and logs.</param>
public sealed record SpeechChunk(long StartMs, float[] Samples, AudioSourceKind Source, long SequenceNumber)
{
    public double DurationSeconds => Samples.Length / (double)SpeechConstants.SampleRate;

    public long EndMs => StartMs + (long)(DurationSeconds * 1000);
}

/// <param name="StartMs">Offset from the start of the chunk, in milliseconds.</param>
/// <param name="EndMs">Offset from the start of the chunk, in milliseconds.</param>
/// <param name="Text">Recognized text.</param>
/// <param name="Confidence">Average token probability, when the engine reports one.</param>
public sealed record RecognizedSpan(long StartMs, long EndMs, string Text, float? Confidence);

/// <summary>
/// A local speech recognition engine.
/// </summary>
/// <remarks>
/// This interface exists so the pipeline can be tested without a 500 MB model,
/// and so that a future engine swap does not touch the recording path. It is
/// deliberately synchronous per chunk: the scheduler owns the threading.
/// <para>
/// Implementations must be local. Sending meeting audio to a network service
/// from an implementation of this interface would violate the product's core
/// privacy guarantee.
/// </para>
/// </remarks>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>Identifier of the loaded model, for metadata and the UI.</summary>
    string ModelId { get; }

    /// <summary>True once the model is loaded and the engine can accept work.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Transcribes one chunk of 16 kHz mono audio.
    /// Returns spans relative to the start of the chunk.
    /// </summary>
    IReadOnlyList<RecognizedSpan> Transcribe(ReadOnlySpan<float> samples, string language, CancellationToken cancellationToken);
}

/// <summary>
/// Decoding constants, in their own type only because a record's primary
/// constructor cannot use its own members as default values.
/// </summary>
public static class SpeechDecodingDefaults
{
    /// <summary>
    /// Beam width. 5 is whisper's own reference value; a beam search costs
    /// roughly twice a greedy decode and is the single cheapest accuracy
    /// improvement available once nothing is waiting for the result.
    /// </summary>
    public const int BeamSize = 5;

    /// <summary>
    /// Whisper's own default temperature-fallback step. A decode that comes out
    /// degenerate is retried warmer, which costs time on the passages that need
    /// it and nothing on the rest.
    /// </summary>
    public const float TemperatureIncrement = 0.2f;
}

/// <summary>
/// How a recognizer should decode.
/// </summary>
/// <remarks>
/// There is one pass in this application and it runs when a recording is
/// already finished, so these values are chosen for accuracy without a latency
/// budget to trade against. The type is kept explicit rather than hidden inside
/// the engine so that the choice is reviewable, and so a test can assert it.
/// </remarks>
/// <param name="Threads">Worker threads handed to whisper.cpp.</param>
/// <param name="BeamSize">1 for greedy decoding; higher searches wider and costs proportionally more.</param>
/// <param name="InitialPrompt">
/// Text prepended as decoder context. A short Japanese sentence is what makes
/// whisper punctuate Japanese properly instead of emitting one unbroken run of
/// kana; it is style guidance, never content, and never appears in the output.
/// </param>
/// <param name="TemperatureIncrement">
/// Whisper's fallback: when a decode looks degenerate it retries at a higher
/// temperature. 0.2 is whisper's own default and recovers passages the first
/// attempt mangles, at the cost of time on those passages only.
/// </param>
public readonly record struct SpeechRecognitionOptions(
    int Threads,
    int BeamSize = SpeechDecodingDefaults.BeamSize,
    string? InitialPrompt = null,
    float TemperatureIncrement = SpeechDecodingDefaults.TemperatureIncrement)
{
    /// <summary>
    /// Japanese meeting speech, punctuated. Without a prompt whisper frequently
    /// returns Japanese with no punctuation at all, which is markedly harder to
    /// read back as a meeting record.
    /// </summary>
    public const string JapaneseMeetingPrompt = "以下は会議の日本語の音声です。句読点を付けて書き起こします。";

    /// <summary>Decoding for the offline pass: the only pass there is.</summary>
    public static SpeechRecognitionOptions Offline(int threads, string language) => new(
        threads,
        BeamSize: SpeechDecodingDefaults.BeamSize,
        InitialPrompt: PromptFor(language),
        TemperatureIncrement: SpeechDecodingDefaults.TemperatureIncrement);

    private static string? PromptFor(string language)
        => string.IsNullOrWhiteSpace(language) || language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            ? JapaneseMeetingPrompt
            : null;
}

/// <summary>Creates a recognizer once a model file is present on disk.</summary>
public interface ISpeechRecognizerFactory
{
    ISpeechRecognizer Create(string modelPath, SpeechRecognitionOptions options);
}
