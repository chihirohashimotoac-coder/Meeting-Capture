namespace MeetingRecorder.Core.ModelManagement;

public enum ModelPurpose
{
    /// <summary>Speech to text (whisper.cpp ggml model).</summary>
    SpeechToText,

    /// <summary>Local text generation for the experimental minutes feature (GGUF).</summary>
    TextGeneration,
}

/// <summary>
/// Everything the user is shown before a single byte is downloaded.
/// </summary>
/// <remarks>
/// The application makes exactly one kind of outbound request: fetching one of
/// these files. Requirement 12 says the user must be told the name, purpose,
/// origin, size, destination and licence first, so all of that lives in the
/// descriptor rather than being buried in code.
/// </remarks>
public sealed record ModelDescriptor
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required ModelPurpose Purpose { get; init; }

    /// <summary>What the model is used for, in Japanese, for the download prompt.</summary>
    public required string PurposeDescription { get; init; }

    /// <summary>Direct download URL. HTTPS only - enforced by the downloader.</summary>
    public required string Url { get; init; }

    /// <summary>File name used on disk.</summary>
    public required string FileName { get; init; }

    /// <summary>Approximate size in bytes, shown before the download starts.</summary>
    public required long ApproximateSizeBytes { get; init; }

    /// <summary>Who publishes the file.</summary>
    public required string Publisher { get; init; }

    public required string License { get; init; }

    public required string LicenseUrl { get; init; }

    /// <summary>
    /// Expected SHA-256, when the project has pinned one. When null the
    /// downloader computes and records the hash instead (and says so) - it never
    /// silently pretends the file was verified.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>Rough resident memory needed to run the model.</summary>
    public required int RequiredRamMb { get; init; }

    /// <summary>
    /// Relative processing cost, with whisper small = 1.0 (higher = slower).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <b>estimate</b>, derived from encoder compute - layers x width squared
    /// - because that term dominates whisper.cpp on a CPU. It is used to pick a
    /// starting model and to show a rough waiting time in the settings window,
    /// and it is labelled there as an estimate. It is never presented as a
    /// measurement of the user's machine, and nothing depends on it being right:
    /// a bad estimate costs a worse default, which the user can change.
    /// </para>
    /// <para>
    /// The derivation deliberately ignores the decoder, which makes the number
    /// pessimistic for a distilled model like large-v3-turbo (a full-size
    /// encoder with only four decoder layers). Correcting for that would mean
    /// guessing at the token-to-frame ratio of a particular meeting, and a
    /// pessimistic estimate is the safer error: it makes the automatic choice
    /// conservative rather than making the user wait for something they did not
    /// expect.
    /// </para>
    /// </remarks>
    public double RelativeCost { get; init; } = 1.0;

    public string? Notes { get; init; }

    public string SizeDisplay => ApproximateSizeBytes >= 1024L * 1024 * 1024
        ? $"{ApproximateSizeBytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
        : $"{ApproximateSizeBytes / 1024.0 / 1024.0:F0} MB";
}
