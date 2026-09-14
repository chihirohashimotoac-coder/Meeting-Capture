namespace MeetingRecorder.Core.Models;

/// <summary>How the minutes document is produced.</summary>
/// <remarks>
/// Two entries, not three. Whether a language model summarises the whole
/// transcript in one pass or has to work through it block by block is decided
/// by arithmetic - does it fit in the context window - and not by the user; a
/// setting for that would be asking somebody to guess at a number the
/// tokenizer already knows.
/// </remarks>
public enum MinutesGenerationMode
{
    /// <summary>
    /// No language model at all: sentences are selected out of the transcript
    /// verbatim.
    /// </summary>
    /// <remarks>
    /// Seconds rather than minutes, and it cannot invent anything, because it
    /// writes nothing - every line in the document is a line somebody said. What
    /// it cannot do is condense or connect: it selects, it does not summarise.
    /// </remarks>
    Extractive = 0,

    /// <summary>
    /// The local language model, falling back to extraction if it cannot run.
    /// </summary>
    LocalLlm = 1,
}
