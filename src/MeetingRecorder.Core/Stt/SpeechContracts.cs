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

/// <summary>Creates a recognizer once a model file is present on disk.</summary>
public interface ISpeechRecognizerFactory
{
    ISpeechRecognizer Create(string modelPath, int threads, int beamSize);
}
