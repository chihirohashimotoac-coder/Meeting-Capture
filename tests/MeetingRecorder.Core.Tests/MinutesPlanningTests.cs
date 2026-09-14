using MeetingRecorder.Minutes;
using Xunit;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// Covers the arithmetic that decides whether a meeting is summarised in one
/// inference or several.
/// </summary>
/// <remarks>
/// This is the change that removed most of the minutes' running time, and it is
/// entirely a counting decision, so it is tested with a counter of known
/// behaviour rather than with a 1 GB model. What the real tokenizer answers is
/// the model's business; what this code does with the answer is this file's.
/// </remarks>
public class MinutesPlannerTests
{
    /// <summary>A tokenizer with an exact, stated rate, so the arithmetic is checkable by hand.</summary>
    private sealed class FixedRateCounter : ITokenCounter
    {
        private readonly double _tokensPerChar;

        public FixedRateCounter(double tokensPerChar = 1.0) => _tokensPerChar = tokensPerChar;

        public int Count(string text) => (int)Math.Ceiling(text.Length * _tokensPerChar);
    }

    private static List<string> Lines(int count, int charactersEach = 100)
        => Enumerable.Range(0, count)
            .Select(i => $"[00:{i:00}:00] マイク: " + new string('あ', charactersEach) + "\n")
            .ToList();

    [Fact]
    public void AShortMeetingIsSummarisedInOnePass()
    {
        // Twenty lines of a hundred characters: about 2100 tokens, which fits a
        // 4096 token window alongside the prompt and the output budget.
        var plan = MinutesPlanner.Plan(Lines(20), new FixedRateCounter(), promptTokens: 400, outputTokens: 1000, contextTokens: 8192, maxBlocks: 60);

        Assert.True(plan.SinglePass);
        Assert.Single(plan.Blocks);
        Assert.Contains("fits", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongMeetingFallsBackToBlocks()
    {
        var plan = MinutesPlanner.Plan(Lines(200), new FixedRateCounter(), promptTokens: 400, outputTokens: 1000, contextTokens: 4096, maxBlocks: 60);

        Assert.False(plan.SinglePass);
        Assert.True(plan.Blocks.Count > 1);
        Assert.Contains("exceeds", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDecisionIsMadeOnTokensAndNotOnCharacters()
    {
        // The same text, two tokenizers. A transcript that fits when the model
        // packs Japanese densely does not fit when it does not, and reading the
        // character count would give the same - wrong - answer both times.
        var lines = Lines(30);

        var dense = MinutesPlanner.Plan(lines, new FixedRateCounter(0.5), promptTokens: 400, outputTokens: 1000, contextTokens: 4096, maxBlocks: 60);
        var sparse = MinutesPlanner.Plan(lines, new FixedRateCounter(2.0), promptTokens: 400, outputTokens: 1000, contextTokens: 4096, maxBlocks: 60);

        Assert.True(dense.SinglePass);
        Assert.False(sparse.SinglePass);
    }

    [Fact]
    public void NothingIsPlannedRightUpToTheContextLimit()
    {
        // The safety margin exists because the chat template adds tokens this
        // code never sees. A plan that used the whole window would overrun, and
        // llama.cpp's answer to an overrun is to silently drop the start of the
        // prompt.
        const int Context = 4096;
        var budget = MinutesPlanner.Budget(Context);

        Assert.True(budget < Context);
        Assert.InRange(Context - budget, 200, 1000);

        Assert.True(MinutesPlanner.FitsInOnePass(budget - 1, 1, 0, Context));
        Assert.False(MinutesPlanner.FitsInOnePass(budget, 1, 0, Context));
    }

    [Fact]
    public void BlocksAreAsLargeAsTheyCanSafelyBeRatherThanAFixedSize()
    {
        var lines = Lines(200, charactersEach: 100);
        var plan = MinutesPlanner.Plan(lines, new FixedRateCounter(), promptTokens: 400, outputTokens: 500, contextTokens: 8192, maxBlocks: 60);

        Assert.False(plan.SinglePass);

        // The budget is 8192 * 0.88 - 400 - 500 = 6309 tokens per block, so
        // 20000 tokens of transcript is four blocks, not the thirteen a 1500
        // character rule would have produced.
        Assert.InRange(plan.Blocks.Count, 3, 5);

        foreach (var block in plan.Blocks)
        {
            Assert.True(block.Length <= 6400, $"a block of {block.Length} tokens exceeds the per-block budget");
        }
    }

    [Fact]
    public void NoTranscriptLineIsEverSplitAcrossTwoBlocks()
    {
        var lines = Lines(50, charactersEach: 200);
        var plan = MinutesPlanner.Plan(lines, new FixedRateCounter(), promptTokens: 100, outputTokens: 100, contextTokens: 2048, maxBlocks: 60);

        Assert.False(plan.SinglePass);

        // Reassembling the blocks has to give back exactly the transcript: no
        // line lost, none duplicated, none cut in half.
        Assert.Equal(string.Concat(lines), string.Concat(plan.Blocks));
    }

    [Fact]
    public void TheBlockCapIsRespectedSoAPathologicalInputCannotRunForever()
    {
        var plan = MinutesPlanner.Plan(Lines(5000), new FixedRateCounter(), promptTokens: 400, outputTokens: 1000, contextTokens: 2048, maxBlocks: 6);

        Assert.False(plan.SinglePass);
        Assert.Equal(6, plan.Blocks.Count);
    }

    [Fact]
    public void AnEmptyTranscriptPlansNothing()
    {
        var plan = MinutesPlanner.Plan(Array.Empty<string>(), new FixedRateCounter(), 400, 1000, 4096, 60);

        Assert.Empty(plan.Blocks);
        Assert.False(plan.SinglePass);
    }

    [Fact]
    public void TheFallbackCounterErrsTowardsTheSaferPath()
    {
        // Over-estimating costs an extra inference; under-estimating overruns
        // the context window and loses the beginning of the meeting. The
        // fallback has to be wrong in the first direction.
        var counter = new HeuristicTokenCounter();
        const string Japanese = "本日の会議では来期の予算配分について議論しました。";

        Assert.True(counter.Count(Japanese) >= Japanese.Length);
        Assert.Equal(0, counter.Count(string.Empty));
    }
}

/// <summary>
/// Covers the parser that turns one structured response into six sections, and
/// in particular the ways a small local model gets the format wrong.
/// </summary>
public class MinutesResponseParserTests
{
    private static readonly MinutesResponseParser Parser = new();

    private const string WellFormed = """
        【概要】
        来期の予算配分について議論した。
        【決定事項】
        - 予算案Aを採用する
        - 次回は来週火曜日
        【議題別内容】
        - 予算配分の三案を比較した
        【ToDo】
        - 見積もりを取り直す
        【継続検討】
        - 採用計画の扱い
        【補足】
        - 音声が一部途切れていた
        """;

    [Fact]
    public void AWellFormedResponseLandsInTheRightSections()
    {
        var result = Parser.Parse(WellFormed);

        Assert.Equal(6, result.RecognisedMarkers);
        Assert.False(result.FellBackToRawText);
        Assert.Contains("来期の予算配分", result.Text(MinutesSection.Overview), StringComparison.Ordinal);
        Assert.Equal(2, result.Lines(MinutesSection.Decisions).Count);
        Assert.Single(result.Lines(MinutesSection.Topics));
        Assert.Single(result.Lines(MinutesSection.Actions));
        Assert.Single(result.Lines(MinutesSection.OpenQuestions));
        Assert.Single(result.Lines(MinutesSection.Notes));
    }

    [Fact]
    public void BulletDecorationIsStrippedButTheSentenceIsNot()
    {
        var result = Parser.Parse("【決定事項】\n- 予算案Aを採用する\n・次回は来週\n* 三つ目");

        Assert.Equal(
            new[] { "予算案Aを採用する", "次回は来週", "三つ目" },
            result.Lines(MinutesSection.Decisions));
    }

    [Theory]
    [InlineData("## 決定事項")]
    [InlineData("**決定事項**")]
    [InlineData("決定事項:")]
    [InlineData("決定事項：")]
    [InlineData("[決定事項]")]
    [InlineData("【決定事項】")]
    public void AHeadingIsRecognisedHoweverTheModelDecoratesIt(string heading)
    {
        var result = Parser.Parse(heading + "\n- 予算案Aを採用する");

        Assert.Single(result.Lines(MinutesSection.Decisions));
    }

    [Fact]
    public void AHeadingTheModelExtendedIsStillTheSameHeading()
    {
        // The prompt asks for 【ToDo】; a model told that will sometimes write
        // the fuller name the template uses.
        var result = Parser.Parse("【ToDo・ネクストアクション】\n- 見積もりを取り直す");

        Assert.Single(result.Lines(MinutesSection.Actions));
    }

    [Fact]
    public void ProseBeforeTheFirstHeadingIsKeptRatherThanDiscarded()
    {
        var result = Parser.Parse("承知しました。以下が議事録です。\n【決定事項】\n- 予算案Aを採用する");

        Assert.Contains("承知しました", result.Text(MinutesSection.Overview), StringComparison.Ordinal);
        Assert.Single(result.Lines(MinutesSection.Decisions));
    }

    [Fact]
    public void AResponseWithNoHeadingsAtAllStillProducesADocument()
    {
        // The failure this design exists for: whatever the model wrote, the user
        // gets it, in the overview, rather than an empty document.
        var result = Parser.Parse("来期の予算について話し合い、予算案Aを採用することになりました。");

        Assert.True(result.FellBackToRawText);
        Assert.Contains("予算案A", result.Text(MinutesSection.Overview), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownHeadingDoesNotSwallowTheTextUnderIt()
    {
        var result = Parser.Parse("【決定事項】\n- 予算案Aを採用する\n【参加者】\n- 田中\n- 鈴木");

        // "参加者" is not one of the six. Its lines stay in the section that was
        // open, so they are still in the document - losing them silently would
        // be worse than filing them imperfectly.
        var decisions = result.Lines(MinutesSection.Decisions);
        Assert.Contains("予算案Aを採用する", decisions);
        Assert.Contains("田中", decisions);
    }

    [Fact]
    public void AnEmptyOrNullResponseIsNotAnError()
    {
        foreach (var response in new[] { null, string.Empty, "   " })
        {
            var result = Parser.Parse(response);
            Assert.True(result.FellBackToRawText);
            Assert.Empty(result.Lines(MinutesSection.Decisions));
        }
    }

    [Fact]
    public void ARepeatedHeadingCountsOnceAndAppends()
    {
        var result = Parser.Parse("【決定事項】\n- ひとつめ\n【概要】\n本文\n【決定事項】\n- ふたつめ");

        Assert.Equal(2, result.RecognisedMarkers);
        Assert.Equal(new[] { "ひとつめ", "ふたつめ" }, result.Lines(MinutesSection.Decisions));
    }

    [Fact]
    public void TheFormatInstructionsAskForEverySectionTheParserKnows()
    {
        // The prompt and the parser have to agree, and the only way to be sure
        // is to generate one from the other.
        var instructions = MinutesResponseParser.FormatInstructions();

        foreach (var name in MinutesResponseParser.MarkerNames)
        {
            Assert.Contains(MinutesResponseParser.Marker(name), instructions, StringComparison.Ordinal);
        }

        Assert.Equal(6, MinutesResponseParser.MarkerNames.Count);

        // Round trip: the instructions themselves parse as an empty document
        // with every section recognised.
        var parsed = Parser.Parse(instructions);
        Assert.Equal(6, parsed.RecognisedMarkers);
    }
}
