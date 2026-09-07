using System.Globalization;
using System.Text;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Minutes;

/// <summary>
/// The fixed document skeleton required by the specification, plus the helpers
/// that render it.
/// </summary>
/// <remarks>
/// The template is the contract with the reader: the same six sections in the
/// same order every time, so a reader can find "決定事項" without reading the
/// whole document. Anything the transcript does not state is written as
/// <c>要確認</c> rather than filled in with a plausible guess.
/// </remarks>
public sealed class MinutesTemplate
{
    public const string Unknown = "要確認";

    public string Overview { get; set; } = string.Empty;

    public List<string> Decisions { get; } = new();

    public List<TopicSection> Topics { get; } = new();

    public List<ActionItem> Actions { get; } = new();

    public List<string> OpenQuestions { get; } = new();

    public List<string> Notes { get; } = new();

    public sealed class TopicSection
    {
        public string Title { get; set; } = Unknown;

        public string TimeRange { get; set; } = string.Empty;

        public List<string> Points { get; } = new();
    }

    public sealed class ActionItem
    {
        public string Task { get; set; } = string.Empty;

        public string Owner { get; set; } = Unknown;

        public string DueDate { get; set; } = Unknown;

        public string? SourceTimestamp { get; set; }
    }

    public string ToMarkdown(MeetingMetadata metadata, string generatorName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 会議議事録");
        sb.AppendLine();
        sb.AppendLine("| 項目 | 内容 |");
        sb.AppendLine("| --- | --- |");
        sb.Append("| 会議名 | ").Append(metadata.Title ?? metadata.FolderName).AppendLine(" |");
        sb.Append("| 日時 | ")
          .Append(metadata.StartedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
          .Append(" - ")
          .Append((metadata.StoppedAtLocal ?? metadata.StartedAtLocal).ToString("HH:mm", CultureInfo.InvariantCulture))
          .AppendLine(" |");
        sb.Append("| 録音時間 | ").Append(FormatDuration(metadata.DurationSeconds)).AppendLine(" |");
        sb.Append("| 生成方法 | ").Append(generatorName).AppendLine(" |");
        sb.AppendLine();
        sb.AppendLine("> この議事録は文字起こしの内容のみから作成されています。");
        sb.AppendLine("> 文字起こしに存在しない情報（担当者・期限など）は「要確認」と記載しています。");
        sb.AppendLine();

        sb.AppendLine("## 1. 概要");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(Overview) ? Unknown : Overview.Trim());
        sb.AppendLine();

        sb.AppendLine("## 2. 決定事項");
        sb.AppendLine();
        AppendBullets(sb, Decisions, "この会議で明確に決定された事項は文字起こしからは特定できませんでした。（" + Unknown + "）");

        sb.AppendLine("## 3. 議題別内容");
        sb.AppendLine();
        if (Topics.Count == 0)
        {
            sb.AppendLine("- " + Unknown);
            sb.AppendLine();
        }
        else
        {
            foreach (var topic in Topics)
            {
                sb.Append("### ").Append(topic.Title);
                if (!string.IsNullOrWhiteSpace(topic.TimeRange))
                {
                    sb.Append(" （").Append(topic.TimeRange).Append('）');
                }

                sb.AppendLine();
                sb.AppendLine();
                AppendBullets(sb, topic.Points, Unknown);
            }
        }

        sb.AppendLine("## 4. ToDo・ネクストアクション");
        sb.AppendLine();
        if (Actions.Count == 0)
        {
            sb.AppendLine("| 内容 | 担当 | 期限 |");
            sb.AppendLine("| --- | --- | --- |");
            sb.AppendLine($"| 特定できませんでした | {Unknown} | {Unknown} |");
        }
        else
        {
            sb.AppendLine("| 内容 | 担当 | 期限 | 該当時刻 |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var action in Actions)
            {
                sb.Append("| ").Append(Escape(action.Task))
                  .Append(" | ").Append(Escape(action.Owner))
                  .Append(" | ").Append(Escape(action.DueDate))
                  .Append(" | ").Append(action.SourceTimestamp ?? "-")
                  .AppendLine(" |");
            }
        }

        sb.AppendLine();

        sb.AppendLine("## 5. 継続検討事項");
        sb.AppendLine();
        AppendBullets(sb, OpenQuestions, "継続検討事項は文字起こしからは特定できませんでした。（" + Unknown + "）");

        sb.AppendLine("## 6. 補足");
        sb.AppendLine();
        AppendBullets(sb, Notes, "なし");

        return sb.ToString();
    }

    public string ToPlainText(MeetingMetadata metadata, string generatorName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("会議議事録");
        sb.AppendLine("========================================");
        sb.Append("会議名   : ").AppendLine(metadata.Title ?? metadata.FolderName);
        sb.Append("日時     : ")
          .AppendLine(metadata.StartedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        sb.Append("録音時間 : ").AppendLine(FormatDuration(metadata.DurationSeconds));
        sb.Append("生成方法 : ").AppendLine(generatorName);
        sb.AppendLine();
        sb.AppendLine("※ 文字起こしに存在しない情報は「要確認」と記載しています。");
        sb.AppendLine();

        sb.AppendLine("1. 概要");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine(string.IsNullOrWhiteSpace(Overview) ? Unknown : Overview.Trim());
        sb.AppendLine();

        sb.AppendLine("2. 決定事項");
        sb.AppendLine("----------------------------------------");
        AppendPlainList(sb, Decisions, Unknown);

        sb.AppendLine("3. 議題別内容");
        sb.AppendLine("----------------------------------------");
        if (Topics.Count == 0)
        {
            sb.AppendLine("  " + Unknown);
            sb.AppendLine();
        }
        else
        {
            foreach (var topic in Topics)
            {
                sb.Append("  ").Append(topic.Title);
                if (!string.IsNullOrWhiteSpace(topic.TimeRange))
                {
                    sb.Append(" (").Append(topic.TimeRange).Append(')');
                }

                sb.AppendLine();
                foreach (var point in topic.Points)
                {
                    sb.Append("    - ").AppendLine(point);
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine("4. ToDo・ネクストアクション");
        sb.AppendLine("----------------------------------------");
        if (Actions.Count == 0)
        {
            sb.AppendLine("  " + Unknown);
        }
        else
        {
            foreach (var action in Actions)
            {
                sb.Append("  - ").Append(action.Task)
                  .Append(" / 担当: ").Append(action.Owner)
                  .Append(" / 期限: ").AppendLine(action.DueDate);
            }
        }

        sb.AppendLine();
        sb.AppendLine("5. 継続検討事項");
        sb.AppendLine("----------------------------------------");
        AppendPlainList(sb, OpenQuestions, Unknown);

        sb.AppendLine("6. 補足");
        sb.AppendLine("----------------------------------------");
        AppendPlainList(sb, Notes, "なし");

        return sb.ToString();
    }

    private static void AppendBullets(StringBuilder sb, IReadOnlyList<string> items, string emptyText)
    {
        if (items.Count == 0)
        {
            sb.Append("- ").AppendLine(emptyText);
        }
        else
        {
            foreach (var item in items)
            {
                sb.Append("- ").AppendLine(item.Trim());
            }
        }

        sb.AppendLine();
    }

    private static void AppendPlainList(StringBuilder sb, IReadOnlyList<string> items, string emptyText)
    {
        if (items.Count == 0)
        {
            sb.Append("  ").AppendLine(emptyText);
        }
        else
        {
            foreach (var item in items)
            {
                sb.Append("  - ").AppendLine(item.Trim());
            }
        }

        sb.AppendLine();
    }

    private static string Escape(string value) => value.Replace("|", "\\|").Replace("\n", " ");

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
