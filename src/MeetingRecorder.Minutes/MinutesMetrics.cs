using System.Globalization;
using System.Text;

namespace MeetingRecorder.Minutes;

/// <summary>How the minutes were produced, and where the time went.</summary>
/// <remarks>
/// <para>
/// The counterpart to <c>TranscriptionMetrics</c>, and it exists for the same
/// reason. The cost of local generation is dominated by tokens the model has to
/// <i>write</i>, not by the transcript it reads, and that is not obvious from
/// watching a progress bar: a run that emits four section prompts of three
/// hundred tokens each has committed to twelve hundred tokens of decoding
/// before it starts, whatever the meeting was about.
/// </para>
/// <para>
/// Counting inferences separately from tokens is what makes the difference
/// between "the model is slow" and "we are asking it too many times" visible.
/// </para>
/// </remarks>
public sealed class MinutesMetrics
{
    /// <summary>Tokens in the transcript as the model's own tokenizer counts them.</summary>
    public int TranscriptTokens { get; set; }

    /// <summary>Context window the model was loaded with.</summary>
    public int ContextTokens { get; set; }

    /// <summary>Blocks the transcript was split into. 1 for a single pass.</summary>
    public int Blocks { get; set; }

    /// <summary>True when the whole transcript went to the model in one prompt.</summary>
    public bool SinglePass { get; set; }

    /// <summary>Why the strategy is what it is, for the log.</summary>
    public string Strategy { get; set; } = string.Empty;

    public TimeSpan ModelLoad { get; set; }

    public int MapInferences { get; set; }

    public TimeSpan MapTime { get; set; }

    public int ReduceInferences { get; set; }

    public TimeSpan ReduceTime { get; set; }

    /// <summary>Tokens the model produced, as its own tokenizer counts them.</summary>
    public int OutputTokens { get; set; }

    /// <summary>Tokens the model was given to read, summed over every inference.</summary>
    public int PromptTokens { get; set; }

    public TimeSpan Total { get; set; }

    public int Inferences => MapInferences + ReduceInferences;

    public TimeSpan InferenceTime => MapTime + ReduceTime;

    /// <summary>Decoding throughput, in tokens per second. Zero when nothing ran.</summary>
    public double TokensPerSecond => InferenceTime.TotalSeconds <= 0
        ? 0
        : OutputTokens / InferenceTime.TotalSeconds;

    public string ToLogBlock()
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;

        sb.AppendLine("Minutes performance:");
        sb.AppendLine(c, $"  Transcript tokens     = {TranscriptTokens}");
        sb.AppendLine(c, $"  Context window        = {ContextTokens}");
        sb.AppendLine(c, $"  Strategy              = {(SinglePass ? "single pass" : "map-reduce")} - {Strategy}");
        sb.AppendLine(c, $"  Blocks                = {Blocks}");
        sb.AppendLine(c, $"  Model load            = {ModelLoad.TotalSeconds:F2} s");
        sb.AppendLine(c, $"  Map inference count   = {MapInferences}");
        sb.AppendLine(c, $"  Map time              = {MapTime.TotalSeconds:F2} s");
        sb.AppendLine(c, $"  Reduce inference count= {ReduceInferences}");
        sb.AppendLine(c, $"  Reduce time           = {ReduceTime.TotalSeconds:F2} s");
        sb.AppendLine(c, $"  Prompt tokens         = {PromptTokens}");
        sb.AppendLine(c, $"  Output tokens         = {OutputTokens}");
        sb.AppendLine(c, $"  Tokens/sec            = {TokensPerSecond:F1}");
        sb.Append(c, $"  Total                 = {Total.TotalSeconds:F2} s");

        return sb.ToString();
    }
}
