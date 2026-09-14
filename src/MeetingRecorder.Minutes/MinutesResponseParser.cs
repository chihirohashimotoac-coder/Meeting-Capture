using System.Text;

namespace MeetingRecorder.Minutes;

/// <summary>Which part of the document a block of model output belongs to.</summary>
public enum MinutesSection
{
    Overview,
    Decisions,
    Topics,
    Actions,
    OpenQuestions,
    Notes,
}

/// <summary>
/// Turns one structured model response into the template's sections.
/// </summary>
/// <remarks>
/// <para><b>Why one response and not six.</b> Asking a local model for the
/// overview, then the decisions, then the actions, then the open questions is
/// four decodes over the same material, and on a CPU it is the decoding that
/// costs. One prompt that asks for all of it, with a marker in front of each
/// part, is a single decode of roughly the same total length - and the model
/// has all six sections in view at once, so a decision does not also get listed
/// as an open question.
/// </para>
/// <para><b>Why parsing this cannot lose the document.</b> A local 1.5B model
/// will sometimes drop a marker, invent a seventh section, or start talking
/// before the first one. None of that is allowed to cost the user their
/// minutes, so:</para>
/// <list type="bullet">
/// <item><description>text before the first recognised marker goes into the
/// overview, which is where prose belongs;</description></item>
/// <item><description>an unrecognised heading is not a section boundary - its
/// text stays in whichever section was open, so it is still in the
/// document;</description></item>
/// <item><description>a response with no markers at all is not an error. The
/// whole thing becomes the overview and the caller is told, which is a rough
/// document rather than no document.</description></item>
/// </list>
/// <para>
/// The parser never writes text of its own. Every line it emits came out of the
/// model, which came from the transcript - it re-files, it does not compose.
/// </para>
/// </remarks>
public sealed class MinutesResponseParser
{
    /// <summary>
    /// The markers the prompt asks for, and the section each one opens.
    /// </summary>
    /// <remarks>
    /// Bracketed Japanese headings rather than JSON. A 1.5B instruct model
    /// asked for JSON on a CPU spends output tokens on punctuation and still
    /// produces a trailing comma often enough to matter, and one bad brace
    /// would cost the whole response. A line that starts with 【決定事項】 is
    /// unambiguous, costs three tokens, and a missing one loses one section
    /// rather than six.
    /// </remarks>
    private static readonly (string Marker, MinutesSection Section)[] Markers =
    {
        ("概要", MinutesSection.Overview),
        ("決定事項", MinutesSection.Decisions),
        ("議題別内容", MinutesSection.Topics),
        ("ToDo", MinutesSection.Actions),
        ("継続検討", MinutesSection.OpenQuestions),
        ("補足", MinutesSection.Notes),
    };

    /// <summary>The section list, in the order the prompt asks for them.</summary>
    public static IReadOnlyList<string> MarkerNames { get; } = Markers.Select(m => m.Marker).ToArray();

    /// <summary>Renders the marker exactly as the prompt asks the model to write it.</summary>
    public static string Marker(string name) => $"【{name}】";

    /// <param name="Sections">Text found under each marker, in order of appearance.</param>
    /// <param name="RecognisedMarkers">How many of the expected markers the model actually wrote.</param>
    public readonly record struct Result(
        IReadOnlyDictionary<MinutesSection, List<string>> Sections,
        int RecognisedMarkers)
    {
        /// <summary>
        /// True when nothing recognisable came back and the whole response was
        /// filed as the overview.
        /// </summary>
        public bool FellBackToRawText => RecognisedMarkers == 0;

        public IReadOnlyList<string> Lines(MinutesSection section)
            => Sections.TryGetValue(section, out var lines) ? lines : Array.Empty<string>();

        public string Text(MinutesSection section) => string.Join(Environment.NewLine, Lines(section));
    }

    public Result Parse(string? response)
    {
        var sections = new Dictionary<MinutesSection, List<string>>();
        foreach (var (_, section) in Markers)
        {
            sections[section] = new List<string>();
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            return new Result(sections, 0);
        }

        var current = MinutesSection.Overview;
        var recognised = 0;
        var seen = new HashSet<MinutesSection>();

        foreach (var raw in response.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryMatchMarker(line, out var section))
            {
                current = section;
                if (seen.Add(section))
                {
                    recognised++;
                }

                continue;
            }

            var cleaned = CleanLine(line);
            if (cleaned.Length > 0)
            {
                sections[current].Add(cleaned);
            }
        }

        return new Result(sections, recognised);
    }

    /// <summary>
    /// Matches a heading line, tolerating the ways a small model decorates one:
    /// markdown hashes, bold asterisks, a trailing colon, or the brackets left
    /// off entirely.
    /// </summary>
    private static bool TryMatchMarker(string line, out MinutesSection section)
    {
        section = MinutesSection.Overview;

        var candidate = line.Trim('#', '*', ' ', '　', ':', '：', '-').Trim();
        candidate = candidate.Trim('【', '】', '[', ']', '「', '」').Trim();
        if (candidate.Length == 0 || candidate.Length > 24)
        {
            return false;
        }

        foreach (var (marker, value) in Markers)
        {
            // Prefix rather than equality: "ToDo・ネクストアクション" is the same
            // heading as "ToDo", and a model told to write one will sometimes
            // write the other.
            if (candidate.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                section = value;
                return true;
            }
        }

        return false;
    }

    /// <summary>Strips bullet decoration without touching the sentence.</summary>
    private static string CleanLine(string line)
    {
        var trimmed = line.TrimStart('-', '*', '・', '　', ' ', '#').Trim();

        // A fenced code block is formatting the model added, not content.
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return trimmed;
    }

    /// <summary>
    /// The instruction block that asks for this format. Kept next to the parser
    /// so the two cannot drift apart.
    /// </summary>
    public static string FormatInstructions()
    {
        var sb = new StringBuilder();
        sb.AppendLine("出力は次の6つの見出しを、この順序で、必ずすべて書いてください。");
        sb.AppendLine("各見出しは行頭に単独で書き、その下に内容を「- 」で始まる箇条書きで書いてください。");
        sb.AppendLine("該当する内容が無い見出しの下には「なし」とだけ書いてください。");
        sb.AppendLine();

        foreach (var name in MarkerNames)
        {
            sb.AppendLine(Marker(name));
        }

        return sb.ToString();
    }
}
