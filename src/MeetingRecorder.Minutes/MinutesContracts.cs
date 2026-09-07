using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Minutes;

/// <param name="Metadata">Meeting metadata for the header.</param>
/// <param name="Segments">The transcript the minutes must be derived from.</param>
public sealed record MinutesRequest(MeetingMetadata Metadata, IReadOnlyList<TranscriptSegment> Segments);

/// <param name="Stage">What the generator is doing, for the UI.</param>
/// <param name="Completed">Units of work finished.</param>
/// <param name="Total">Total units of work, when known.</param>
public readonly record struct MinutesProgress(string Stage, int Completed, int Total)
{
    public double? Fraction => Total > 0 ? Math.Clamp(Completed / (double)Total, 0, 1) : null;
}

/// <param name="Markdown">minutes.md content.</param>
/// <param name="PlainText">minutes.txt content.</param>
/// <param name="GeneratorName">Which generator produced this, shown in the document itself.</param>
/// <param name="UsedLocalLlm">True when a local language model was actually run.</param>
/// <param name="Warnings">Anything the user should know, e.g. a fallback that happened.</param>
public sealed record MinutesDocument(
    string Markdown,
    string PlainText,
    string GeneratorName,
    bool UsedLocalLlm,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Produces meeting minutes from a transcript.
/// </summary>
/// <remarks>
/// Every implementation is strictly local. Sending a transcript to a hosted
/// model would defeat the entire point of the product, so there is no
/// implementation - and no configuration hook - that could reach a network
/// service.
/// </remarks>
public interface IMinutesGenerator : IDisposable
{
    string Name { get; }

    /// <summary>False when the generator cannot run right now (e.g. no model downloaded).</summary>
    bool IsAvailable { get; }

    Task<MinutesDocument> GenerateAsync(
        MinutesRequest request,
        IProgress<MinutesProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
