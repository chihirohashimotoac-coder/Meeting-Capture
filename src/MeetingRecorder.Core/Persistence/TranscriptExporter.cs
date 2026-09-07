using System.Globalization;
using System.Text;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Persistence;

/// <summary>Renders the transcript to the plain text and Markdown deliverables.</summary>
public static class TranscriptExporter
{
    public static string BuildText(MeetingMetadata metadata, IReadOnlyList<TranscriptSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("文字起こし");
        sb.AppendLine("========================================");
        AppendHeaderLines(sb, metadata, "  ");
        sb.AppendLine();

        if (segments.Count == 0)
        {
            sb.AppendLine("(認識された発言はありません)");
            return sb.ToString();
        }

        foreach (var segment in segments.OrderBy(s => s.StartMs))
        {
            sb.Append('[').Append(segment.TimestampLabel()).Append("] [")
              .Append(segment.SourceLabel()).AppendLine("]");
            sb.AppendLine(segment.Text.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string BuildMarkdown(MeetingMetadata metadata, IReadOnlyList<TranscriptSegment> segments)
    {
        var sb = new StringBuilder();
        sb.Append("# 文字起こし — ")
          .AppendLine(metadata.StartedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("| 項目 | 内容 |");
        sb.AppendLine("| --- | --- |");
        sb.Append("| 会議名 | ").Append(Escape(metadata.Title ?? metadata.FolderName)).AppendLine(" |");
        sb.Append("| 開始日時 | ")
          .Append(metadata.StartedAtLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))
          .AppendLine(" |");
        sb.Append("| 録音時間 | ").Append(FormatDuration(metadata.DurationSeconds)).AppendLine(" |");
        sb.Append("| 音声ファイル | ").Append(Escape(metadata.AudioFileName)).AppendLine(" |");
        sb.Append("| マイク | ").Append(Escape(metadata.MicrophoneDeviceName ?? "未設定")).AppendLine(" |");
        sb.Append("| PC音声 | ").Append(Escape(metadata.RenderDeviceName ?? "未設定")).AppendLine(" |");
        sb.Append("| 音声認識モデル | ").Append(Escape(metadata.SttModelId ?? "なし")).AppendLine(" |");
        sb.Append("| 話者分離 | ").Append(metadata.DiarizationUsed ? "あり" : "なし").AppendLine(" |");
        if (metadata.RecoveredFromCrash)
        {
            sb.AppendLine("| 復旧 | 異常終了から復旧したデータです |");
        }

        sb.AppendLine();
        sb.AppendLine("## 本文");
        sb.AppendLine();

        if (segments.Count == 0)
        {
            sb.AppendLine("_認識された発言はありません。_");
            return sb.ToString();
        }

        foreach (var segment in segments.OrderBy(s => s.StartMs))
        {
            sb.Append("**[").Append(segment.TimestampLabel()).Append("] ")
              .Append(Escape(segment.SourceLabel())).AppendLine("**");
            sb.AppendLine();
            sb.AppendLine(segment.Text.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static void Write(MeetingFolder folder, MeetingMetadata metadata, IReadOnlyList<TranscriptSegment> segments)
    {
        File.WriteAllText(folder.TranscriptTextPath, BuildText(metadata, segments), new UTF8Encoding(true));
        File.WriteAllText(folder.TranscriptMarkdownPath, BuildMarkdown(metadata, segments), new UTF8Encoding(true));
    }

    internal static void AppendHeaderLines(StringBuilder sb, MeetingMetadata metadata, string indent)
    {
        sb.Append(indent).Append("会議名      : ").AppendLine(metadata.Title ?? metadata.FolderName);
        sb.Append(indent).Append("開始日時    : ")
          .AppendLine(metadata.StartedAtLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        sb.Append(indent).Append("録音時間    : ").AppendLine(FormatDuration(metadata.DurationSeconds));
        sb.Append(indent).Append("音声ファイル: ").AppendLine(metadata.AudioFileName);
        sb.Append(indent).Append("マイク      : ").AppendLine(metadata.MicrophoneDeviceName ?? "未設定");
        sb.Append(indent).Append("PC音声      : ").AppendLine(metadata.RenderDeviceName ?? "未設定");
        sb.Append(indent).Append("認識モデル  : ").AppendLine(metadata.SttModelId ?? "なし");
        if (metadata.RecoveredFromCrash)
        {
            sb.Append(indent).AppendLine("備考        : 異常終了から復旧したデータです");
        }
    }

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>Escapes the pipe character so a speaker name cannot break the table.</summary>
    private static string Escape(string value) => value.Replace("|", "\\|");
}
