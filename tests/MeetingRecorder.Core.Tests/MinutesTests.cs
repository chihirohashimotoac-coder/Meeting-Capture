using MeetingRecorder.Core.Models;
using MeetingRecorder.Minutes;
using Xunit;

namespace MeetingRecorder.Core.Tests;

public class MinutesTests
{
    private static MinutesRequest BuildRequest()
    {
        var metadata = new MeetingMetadata
        {
            FolderName = "2026-09-07_1430_Meeting",
            Title = "月次定例",
            StartedAtLocal = new DateTimeOffset(2026, 9, 7, 14, 30, 0, TimeSpan.FromHours(9)),
            StoppedAtLocal = new DateTimeOffset(2026, 9, 7, 15, 20, 0, TimeSpan.FromHours(9)),
            DurationSeconds = 3000,
        };

        var segments = new List<TranscriptSegment>
        {
            new() { StartMs = 10_000, EndMs = 16_000, Source = AudioSourceKind.Microphone, Text = "今回のKPIについて確認します。" },
            new() { StartMs = 20_000, EndMs = 28_000, Source = AudioSourceKind.SystemAudio, SpeakerId = 0, Text = "前月実績は目標を下回りました。要因は納期遅延です。" },
            new() { StartMs = 40_000, EndMs = 48_000, Source = AudioSourceKind.Microphone, Text = "では来月から発注フローを変更することにします。" },
            new() { StartMs = 60_000, EndMs = 70_000, Source = AudioSourceKind.SystemAudio, SpeakerId = 1, Text = "資料は今週までに共有します。" },
            new() { StartMs = 90_000, EndMs = 99_000, Source = AudioSourceKind.Microphone, Text = "予算の扱いは持ち帰りで検討とします。" },
            new() { StartMs = 900_000, EndMs = 906_000, Source = AudioSourceKind.SystemAudio, SpeakerId = 0, Text = "次回は在庫の話をしましょう。" },
        };

        return new MinutesRequest(metadata, segments);
    }

    [Fact]
    public async Task ExtractiveGeneratorEmitsAllSixRequiredSections()
    {
        using var generator = new ExtractiveMinutesGenerator();
        var document = await generator.GenerateAsync(BuildRequest());

        foreach (var heading in new[]
                 {
                     "## 1. 概要",
                     "## 2. 決定事項",
                     "## 3. 議題別内容",
                     "## 4. ToDo・ネクストアクション",
                     "## 5. 継続検討事項",
                     "## 6. 補足",
                 })
        {
            Assert.Contains(heading, document.Markdown);
        }

        Assert.StartsWith("# 会議議事録", document.Markdown);
        Assert.False(document.UsedLocalLlm);
    }

    [Fact]
    public async Task DecisionsAndActionsAreTakenVerbatimFromTheTranscript()
    {
        using var generator = new ExtractiveMinutesGenerator();
        var document = await generator.GenerateAsync(BuildRequest());

        Assert.Contains("発注フローを変更することにします", document.Markdown);
        Assert.Contains("今週までに共有します", document.Markdown);
        Assert.Contains("持ち帰りで検討とします", document.Markdown);
    }

    [Fact]
    public async Task UnknownOwnersAndDueDatesAreMarkedForConfirmationRatherThanInvented()
    {
        using var generator = new ExtractiveMinutesGenerator();
        var document = await generator.GenerateAsync(BuildRequest());

        Assert.Contains(MinutesTemplate.Unknown, document.Markdown);
        // No name appears anywhere unless the transcript contained it.
        Assert.DoesNotContain("橋本", document.Markdown);
        Assert.DoesNotContain("大西", document.Markdown);
        Assert.DoesNotContain("田中", document.Markdown);
    }

    [Fact]
    public async Task AnEmptyTranscriptProducesAnHonestlyEmptyDocument()
    {
        using var generator = new ExtractiveMinutesGenerator();
        var request = new MinutesRequest(new MeetingMetadata { FolderName = "empty" }, Array.Empty<TranscriptSegment>());
        var document = await generator.GenerateAsync(request);

        Assert.Contains("## 2. 決定事項", document.Markdown);
        Assert.Contains(MinutesTemplate.Unknown, document.Markdown);
        Assert.DoesNotContain("KPI", document.Markdown);
    }

    [Fact]
    public async Task ProgressIsReportedInRealStepsRatherThanBeingFaked()
    {
        using var generator = new ExtractiveMinutesGenerator();
        var reports = new List<MinutesProgress>();
        var progress = new Progress<MinutesProgress>(reports.Add);

        await generator.GenerateAsync(BuildRequest(), progress);

        // Progress<T> marshals asynchronously; give it a moment to drain.
        for (var i = 0; i < 50 && reports.Count < 4; i++)
        {
            await Task.Delay(10);
        }

        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.True(report.Completed <= report.Total));
    }

    [Fact]
    public async Task PlainTextAndMarkdownCarryTheSameSections()
    {
        using var generator = new ExtractiveMinutesGenerator();
        var document = await generator.GenerateAsync(BuildRequest());

        Assert.Contains("1. 概要", document.PlainText);
        Assert.Contains("4. ToDo・ネクストアクション", document.PlainText);
        Assert.Contains("6. 補足", document.PlainText);
    }

    [Fact]
    public async Task TheDocumentStatesHowItWasGenerated()
    {
        using var generator = new ExtractiveMinutesGenerator();
        var document = await generator.GenerateAsync(BuildRequest());

        Assert.Contains(generator.Name, document.Markdown);
        Assert.Contains("文字起こしの内容のみから作成", document.Markdown);
    }

    [Fact]
    public async Task LocalLlmGeneratorFallsBackToExtractionWhenTheModelIsMissing()
    {
        using var generator = new LocalLlmMinutesGenerator(
            Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid().ToString("N") + ".gguf"),
            "テストモデル",
            threads: 2);

        Assert.False(generator.IsAvailable);

        var document = await generator.GenerateAsync(BuildRequest());

        Assert.False(document.UsedLocalLlm);
        Assert.Contains("## 2. 決定事項", document.Markdown);
        Assert.Contains(document.Warnings, w => w.Contains("抽出型"));
    }

    [Fact]
    public void TemplateAlwaysRendersEverySectionEvenWhenEmpty()
    {
        var template = new MinutesTemplate();
        var markdown = template.ToMarkdown(new MeetingMetadata { FolderName = "x" }, "テスト");

        Assert.Contains("## 1. 概要", markdown);
        Assert.Contains("## 5. 継続検討事項", markdown);
        Assert.Contains(MinutesTemplate.Unknown, markdown);
    }

    [Fact]
    public void SpeakerNamesInTheTemplateAreEscapedSoTheyCannotBreakTheTable()
    {
        var template = new MinutesTemplate();
        template.Actions.Add(new MinutesTemplate.ActionItem { Task = "a|b", Owner = "c|d", DueDate = "e|f" });

        var markdown = template.ToMarkdown(new MeetingMetadata { FolderName = "x" }, "テスト");
        Assert.Contains("a\\|b", markdown);
    }
}
