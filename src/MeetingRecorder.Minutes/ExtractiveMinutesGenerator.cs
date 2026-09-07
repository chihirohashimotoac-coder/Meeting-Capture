using System.Text;
using System.Text.RegularExpressions;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Minutes;

/// <summary>
/// Builds minutes by selecting and grouping sentences that are actually in the
/// transcript. No language model, no invention, no network.
/// </summary>
/// <remarks>
/// <para>
/// This is the generator that is always available: it needs no download, runs in
/// milliseconds, and works on any machine. It is also the fallback when the
/// local LLM is disabled, missing, or fails - which matters, because the
/// requirements put minutes last in the priority order and forbid letting an
/// optional feature take anything else down with it.
/// </para>
/// <para>
/// The extraction is cue-phrase based. Japanese meeting speech marks decisions
/// and actions fairly reliably ("〜することにします", "〜でいきましょう",
/// "〜までに", "〜お願いします"), so matching those cues finds real sentences
/// rather than inventing summaries. Everything it emits is a verbatim (or
/// lightly trimmed) sentence from the transcript, with its timestamp, so a
/// reader can always check it against the recording.
/// </para>
/// </remarks>
public sealed class ExtractiveMinutesGenerator : IMinutesGenerator
{
    // Cue phrases. Deliberately conservative: a false positive puts a wrong line
    // in "決定事項", which is worse than an empty section the reader fills in.
    private static readonly Regex DecisionCue = new(
        "(決定|決まりました|決めました|承認|合意|了承|確定|ことにします|ことにしましょう|でいきましょう|で進めます|に決め)",
        RegexOptions.Compiled);

    private static readonly Regex ActionCue = new(
        "(お願いします|お願いいたします|対応します|やります|やっておきます|進めます|作成します|共有します|確認します|送ります|準備します|次回まで|までに)",
        RegexOptions.Compiled);

    private static readonly Regex OpenCue = new(
        "(検討|保留|持ち帰り|次回|要確認|確認が必要|未定|課題|懸念|リスク)",
        RegexOptions.Compiled);

    private static readonly Regex DueDateCue = new(
        @"((今日|本日|明日|明後日|今週|来週|再来週|今月|来月|月末|週末|次回)|(\d{1,2}\s*月\s*\d{1,2}\s*日)|(\d{1,2}/\d{1,2}))",
        RegexOptions.Compiled);

    /// <summary>Sentence terminators used to split Japanese utterances.</summary>
    private static readonly char[] SentenceEnd = { '。', '！', '？', '!', '?', '\n' };

    /// <summary>Topic blocks span this many minutes of meeting time.</summary>
    private const int TopicBlockMinutes = 10;

    public string Name => "抽出型（ローカル・AIモデル不使用）";

    public bool IsAvailable => true;

    public Task<MinutesDocument> GenerateAsync(
        MinutesRequest request,
        IProgress<MinutesProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new MinutesProgress("文字起こしを解析中", 0, 3));
        var sentences = ExtractSentences(request.Segments);

        var template = new MinutesTemplate
        {
            Overview = BuildOverview(request, sentences),
        };

        progress?.Report(new MinutesProgress("決定事項とアクションを抽出中", 1, 3));

        foreach (var sentence in sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DecisionCue.IsMatch(sentence.Text) && template.Decisions.Count < 20)
            {
                template.Decisions.Add($"[{sentence.Timestamp}] {sentence.Text}");
            }
            else if (ActionCue.IsMatch(sentence.Text) && template.Actions.Count < 20)
            {
                var due = DueDateCue.Match(sentence.Text);
                template.Actions.Add(new MinutesTemplate.ActionItem
                {
                    Task = sentence.Text,
                    // The transcript rarely states an owner explicitly, and
                    // guessing one from a speaker label would attribute work to
                    // the wrong person. "要確認" is the honest answer.
                    Owner = MinutesTemplate.Unknown,
                    DueDate = due.Success ? due.Value : MinutesTemplate.Unknown,
                    SourceTimestamp = sentence.Timestamp,
                });
            }
            else if (OpenCue.IsMatch(sentence.Text) && template.OpenQuestions.Count < 20)
            {
                template.OpenQuestions.Add($"[{sentence.Timestamp}] {sentence.Text}");
            }
        }

        progress?.Report(new MinutesProgress("議題を整理中", 2, 3));
        BuildTopics(template, sentences);

        template.Notes.Add("この議事録は文字起こしからキーフレーズを抽出して作成した機械的な要約です。内容の正確性は必ず原文（transcript.md）で確認してください。");
        template.Notes.Add($"対象発言数: {request.Segments.Count} / 抽出対象文数: {sentences.Count}");
        if (request.Metadata.Warnings.Count > 0)
        {
            foreach (var warning in request.Metadata.Warnings)
            {
                template.Notes.Add($"録音時の警告: {warning}");
            }
        }

        progress?.Report(new MinutesProgress("完了", 3, 3));

        var document = new MinutesDocument(
            template.ToMarkdown(request.Metadata, Name),
            template.ToPlainText(request.Metadata, Name),
            Name,
            UsedLocalLlm: false,
            Array.Empty<string>());

        return Task.FromResult(document);
    }

    private static string BuildOverview(MinutesRequest request, IReadOnlyList<Sentence> sentences)
    {
        var sb = new StringBuilder();
        var micCount = request.Segments.Count(s => s.Source == AudioSourceKind.Microphone);
        var systemCount = request.Segments.Count(s => s.Source == AudioSourceKind.SystemAudio);
        var speakers = request.Segments.Where(s => s.SpeakerId.HasValue)
            .Select(s => $"{s.Source}:{s.SpeakerId}")
            .Distinct()
            .Count();

        sb.Append("録音時間 ")
          .Append(MinutesTemplate.FormatDuration(request.Metadata.DurationSeconds))
          .Append("、発言 ")
          .Append(request.Segments.Count)
          .Append(" 件（マイク ")
          .Append(micCount)
          .Append(" 件 / PC音声 ")
          .Append(systemCount)
          .Append(" 件）");

        if (speakers > 0)
        {
            sb.Append("、話者分離クラスタ ").Append(speakers).Append(" 件");
        }

        sb.AppendLine("。");

        // A short lead-in taken verbatim from the opening of the meeting, which
        // is usually where the purpose is stated.
        var opening = sentences.Take(3).Select(s => s.Text).Where(t => t.Length > 8).Take(2).ToList();
        if (opening.Count > 0)
        {
            sb.Append("冒頭の発言: ").Append(string.Join(" / ", opening));
        }

        return sb.ToString();
    }

    private static void BuildTopics(MinutesTemplate template, IReadOnlyList<Sentence> sentences)
    {
        foreach (var group in sentences.GroupBy(s => s.StartMs / (TopicBlockMinutes * 60_000L)).OrderBy(g => g.Key))
        {
            var blockStart = TimeSpan.FromMilliseconds(group.Key * TopicBlockMinutes * 60_000L);
            var blockEnd = blockStart + TimeSpan.FromMinutes(TopicBlockMinutes);

            var topic = new MinutesTemplate.TopicSection
            {
                Title = $"時間帯 {blockStart:hh\\:mm} - {blockEnd:hh\\:mm}",
                TimeRange = $"{blockStart:hh\\:mm\\:ss} 〜 {blockEnd:hh\\:mm\\:ss}",
            };

            // Longest utterances in the block are the ones that carry content;
            // back-channel responses are short by nature.
            foreach (var sentence in group.OrderByDescending(s => s.Text.Length).Take(5).OrderBy(s => s.StartMs))
            {
                topic.Points.Add($"[{sentence.Timestamp}] {sentence.SourceLabel}: {sentence.Text}");
            }

            if (topic.Points.Count > 0)
            {
                template.Topics.Add(topic);
            }
        }
    }

    private static List<Sentence> ExtractSentences(IReadOnlyList<TranscriptSegment> segments)
    {
        var result = new List<Sentence>();
        foreach (var segment in segments.OrderBy(s => s.StartMs))
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            foreach (var raw in segment.Text.Split(SentenceEnd, StringSplitOptions.RemoveEmptyEntries))
            {
                var text = raw.Trim();
                if (text.Length < 4)
                {
                    continue;
                }

                result.Add(new Sentence(segment.StartMs, TranscriptSegment.FormatTimestamp(segment.StartMs), segment.SourceLabel(), text));
            }
        }

        return result;
    }

    public void Dispose()
    {
    }

    private sealed record Sentence(long StartMs, string Timestamp, string SourceLabel, string Text);
}
