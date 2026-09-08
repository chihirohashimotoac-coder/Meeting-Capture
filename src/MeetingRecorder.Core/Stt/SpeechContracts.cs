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
/// How a recognizer should decode. The two passes want opposite things, so the
/// choice is explicit rather than hidden in the engine.
/// </summary>
/// <param name="Threads">Worker threads handed to whisper.cpp.</param>
/// <param name="BeamSize">1 for greedy decoding; higher searches wider and costs proportionally more.</param>
/// <param name="InitialPrompt">
/// Text prepended as decoder context. A short Japanese sentence is what makes
/// whisper punctuate Japanese properly instead of emitting one unbroken run of
/// kana; it is style guidance, never content, and never appears in the output.
/// </param>
/// <param name="TemperatureIncrement">
/// Whisper's fallback: when a decode looks degenerate it retries at a higher
/// temperature. 0 disables the retries and is roughly twice as fast in the worst
/// case; 0.2 is whisper's own default and recovers passages the first attempt
/// mangles.
/// </param>
public readonly record struct SpeechRecognitionOptions(
    int Threads,
    int BeamSize = 1,
    string? InitialPrompt = null,
    float TemperatureIncrement = 0.0f)
{
    /// <summary>
    /// Japanese meeting speech, punctuated. Without a prompt whisper frequently
    /// returns Japanese with no punctuation at all, which is markedly harder to
    /// read back as a meeting record.
    /// </summary>
    public const string JapaneseMeetingPrompt = "以下は会議の日本語の音声です。句読点を付けて書き起こします。";

    /// <summary>Decoding for the live pass: as fast as possible, shown while the meeting runs.</summary>
    public static SpeechRecognitionOptions Live(int threads, string language) => new(
        threads,
        BeamSize: 1,
        InitialPrompt: PromptFor(language),
        TemperatureIncrement: 0.0f);

    /// <summary>Decoding for the second pass: accuracy, run after recording stops.</summary>
    public static SpeechRecognitionOptions Accurate(int threads, string language) => new(
        threads,
        BeamSize: 5,
        InitialPrompt: PromptFor(language),
        TemperatureIncrement: 0.2f);

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
