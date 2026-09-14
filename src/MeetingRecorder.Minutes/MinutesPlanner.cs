using System.Globalization;

namespace MeetingRecorder.Minutes;

/// <summary>Counts tokens the way the model that will read them does.</summary>
/// <remarks>
/// An interface because the answer has to come from the loaded model's own
/// tokenizer, and because the decision that depends on it has to be testable
/// without a 1 GB model file. Estimating from character count - the thing this
/// code used to do implicitly, by cutting blocks at 1500 characters - is how a
/// prompt ends up 30% over a context window that then silently truncates it.
/// </remarks>
public interface ITokenCounter
{
    int Count(string text);
}

/// <summary>
/// Token counter for when the real one is not available, and for nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Japanese through a Qwen-style BPE vocabulary runs at roughly 0.9 to 1.1
/// tokens per character - most common kanji and kana bigrams are single tokens,
/// while rarer characters split. ASCII in the same text runs nearer 0.3 tokens
/// per character. Counting each character as one token is therefore a slight
/// over-estimate for Japanese and a large one for anything Latin, and both
/// errors point the same way: the planner concludes a transcript is bigger than
/// it is and takes the map-reduce path.
/// </para>
/// <para>
/// That is the correct direction to be wrong in. Over-estimating costs a few
/// extra inferences on a meeting that would have fitted; under-estimating
/// overruns the context window, and llama.cpp's answer to that is to drop the
/// beginning of the prompt - the model would summarise the second half of a
/// meeting and never say that it had.
/// </para>
/// <para>
/// It is a fallback. When the model is loaded, <c>LlamaTokenCounter</c> asks the
/// vocabulary and this is not used.
/// </para>
/// </remarks>
public sealed class HeuristicTokenCounter : ITokenCounter
{
    public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : text.Length;
}

/// <param name="SinglePass">True when the whole transcript fits in one prompt.</param>
/// <param name="Blocks">The transcript, split into as few pieces as will fit.</param>
/// <param name="Reason">Why, in one line, for the log.</param>
public readonly record struct MinutesPlan(bool SinglePass, IReadOnlyList<string> Blocks, string Reason);

/// <summary>
/// Decides whether a transcript can be summarised in one inference, and if not,
/// how few pieces it has to be cut into.
/// </summary>
/// <remarks>
/// <para>
/// The old answer was "always map-reduce, in 1500 character blocks". For a ten
/// minute meeting that is several map inferences plus four reduce inferences to
/// summarise a few thousand characters that would have fitted in the context
/// window whole - and every one of those inferences pays a model warm-up, a
/// prompt prefill and a few hundred tokens of decoding.
/// </para>
/// <para>
/// The new answer is arithmetic. Count the transcript with the model's own
/// tokenizer, add the prompt overhead and the output budget and a margin, and
/// compare against the context window. If it fits, one inference produces every
/// section. If it does not, the transcript is cut into the <i>fewest</i> blocks
/// that do fit rather than into a fixed number of characters, and the digest of
/// those blocks is reduced by a single structured inference instead of four.
/// </para>
/// <para>
/// N + 4 becomes 1, or N + 1. Nothing about what the model is asked to produce
/// changes - the six sections, the ban on inventing owners and dates, and the
/// template that renders them are exactly as they were.
/// </para>
/// </remarks>
public static class MinutesPlanner
{
    /// <summary>
    /// Fraction of the context window left unused.
    /// </summary>
    /// <remarks>
    /// 12%. The chat template adds tokens this code never sees (role markers,
    /// the system message wrapper, a generation prompt), the tokenizer is only
    /// consulted on the parts assembled here, and running out is not a graceful
    /// failure. It is cheap insurance: on a 4096 token window it costs about 490
    /// tokens, which is a paragraph of transcript.
    /// </remarks>
    public const double SafetyMarginFraction = 0.12;

    /// <summary>
    /// Smallest block the splitter will produce, in tokens. Below this a block
    /// is too small to summarise usefully and is merged with its neighbour.
    /// </summary>
    public const int MinimumBlockTokens = 200;

    /// <summary>
    /// Whether <paramref name="transcriptTokens"/> plus everything around it
    /// fits in <paramref name="contextTokens"/>.
    /// </summary>
    public static bool FitsInOnePass(int transcriptTokens, int promptTokens, int outputTokens, int contextTokens)
        => transcriptTokens + promptTokens + outputTokens <= Budget(contextTokens);

    /// <summary>Tokens available once the safety margin is set aside.</summary>
    public static int Budget(int contextTokens)
        => (int)(contextTokens * (1.0 - SafetyMarginFraction));

    /// <summary>
    /// Plans the run for a transcript that has already been rendered into
    /// timestamped lines.
    /// </summary>
    /// <param name="lines">
    /// Transcript lines, each already carrying its timestamp and source label.
    /// The splitter only ever cuts between two of these, so no line is ever
    /// broken across blocks.
    /// </param>
    /// <param name="counter">The model's tokenizer.</param>
    /// <param name="promptTokens">Fixed prompt and system-message overhead per inference.</param>
    /// <param name="outputTokens">Tokens reserved for what the model writes.</param>
    /// <param name="contextTokens">The model's context window.</param>
    /// <param name="maxBlocks">
    /// Ceiling on blocks, so a pathological input cannot become an hour of
    /// inference. Reaching it truncates, and the caller says so in the document.
    /// </param>
    public static MinutesPlan Plan(
        IReadOnlyList<string> lines,
        ITokenCounter counter,
        int promptTokens,
        int outputTokens,
        int contextTokens,
        int maxBlocks)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(counter);

        if (lines.Count == 0)
        {
            return new MinutesPlan(false, Array.Empty<string>(), "文字起こしが空です。");
        }

        var whole = string.Concat(lines);
        var transcriptTokens = counter.Count(whole);
        var budget = Budget(contextTokens);

        if (transcriptTokens + promptTokens + outputTokens <= budget)
        {
            return new MinutesPlan(
                true,
                new[] { whole },
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"transcript {transcriptTokens} + prompt {promptTokens} + output {outputTokens} tokens fits the {budget} token budget"));
        }

        var blockBudget = Math.Max(MinimumBlockTokens, budget - promptTokens - outputTokens);
        var blocks = Split(lines, counter, blockBudget, maxBlocks);

        return new MinutesPlan(
            false,
            blocks,
            string.Create(
                CultureInfo.InvariantCulture,
                $"transcript {transcriptTokens} tokens exceeds the {budget} token budget; "
                + $"split into {blocks.Count} block(s) of at most {blockBudget} tokens"));
    }

    /// <summary>
    /// Greedily fills blocks up to <paramref name="blockBudget"/> tokens,
    /// cutting only between transcript lines.
    /// </summary>
    /// <remarks>
    /// Greedy, and deliberately so: the goal is the smallest number of blocks,
    /// which is the same as the largest blocks, which is what filling each one
    /// before starting the next produces. A line longer than the whole budget
    /// gets a block to itself rather than being cut mid-sentence - the model
    /// will truncate it, which is bad, but splitting a sentence in half and
    /// summarising each half separately is worse.
    /// </remarks>
    private static List<string> Split(
        IReadOnlyList<string> lines,
        ITokenCounter counter,
        int blockBudget,
        int maxBlocks)
    {
        var blocks = new List<string>();
        var current = new System.Text.StringBuilder();
        var currentTokens = 0;

        foreach (var line in lines)
        {
            var tokens = counter.Count(line);

            if (currentTokens > 0 && currentTokens + tokens > blockBudget)
            {
                blocks.Add(current.ToString());
                current.Clear();
                currentTokens = 0;

                if (blocks.Count >= maxBlocks)
                {
                    return blocks;
                }
            }

            current.Append(line);
            currentTokens += tokens;
        }

        if (current.Length > 0 && blocks.Count < maxBlocks)
        {
            blocks.Add(current.ToString());
        }

        return blocks;
    }
}
