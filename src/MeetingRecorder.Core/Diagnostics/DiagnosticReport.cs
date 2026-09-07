using System.Globalization;
using System.Text;

namespace MeetingRecorder.Core.Diagnostics;

public enum DiagnosticStatus
{
    Ok,
    Warning,
    Error,

    /// <summary>The check could not be performed here, and says so rather than guessing.</summary>
    NotTested,
}

/// <param name="Id">Stable identifier, matching the ids in docs/WINDOWS_E2E_TEST.md where one applies.</param>
/// <param name="Title">Short label.</param>
/// <param name="Status">Outcome.</param>
/// <param name="Detail">What was actually observed.</param>
/// <param name="Remedy">What to do about it. Required for anything that is not Ok.</param>
public sealed record DiagnosticCheck(
    string Id,
    string Title,
    DiagnosticStatus Status,
    string Detail,
    string Remedy = "")
{
    public static DiagnosticCheck Ok(string id, string title, string detail)
        => new(id, title, DiagnosticStatus.Ok, detail);

    public static DiagnosticCheck NotTested(string id, string title, string reason)
        => new(id, title, DiagnosticStatus.NotTested, reason);
}

/// <summary>
/// The result of <c>MeetingRecorder.exe --diagnose</c>.
/// </summary>
/// <remarks>
/// This exists because the most important properties of this product - that
/// WASAPI loopback really captures the PC's audio, and that the process opens no
/// outbound connections - cannot be established on a CI runner. They can only be
/// established on the user's own machine. Rather than leaving that as a manual
/// 47-step document, the application can check most of it itself and print a
/// report the user can read or hand to their IT department.
/// </remarks>
public sealed class DiagnosticReport
{
    public DateTimeOffset StartedAtLocal { get; set; } = DateTimeOffset.Now;

    public string AppVersion { get; set; } = "0.0.0";

    public string OperatingSystem { get; set; } = string.Empty;

    public string ProcessArchitecture { get; set; } = string.Empty;

    public bool IsElevated { get; set; }

    public int LogicalCores { get; set; }

    public double TotalRamGb { get; set; }

    public string DataRoot { get; set; } = string.Empty;

    public bool PortableMode { get; set; }

    public List<DiagnosticCheck> Checks { get; } = new();

    public DiagnosticStatus Overall => Checks.Count == 0
        ? DiagnosticStatus.NotTested
        : Checks.Any(c => c.Status == DiagnosticStatus.Error) ? DiagnosticStatus.Error
        : Checks.Any(c => c.Status == DiagnosticStatus.Warning) ? DiagnosticStatus.Warning
        : DiagnosticStatus.Ok;

    /// <summary>0 when nothing failed, 1 when at least one check is an error.</summary>
    public int ExitCode => Overall == DiagnosticStatus.Error ? 1 : 0;

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================================================");
        sb.AppendLine(" MeetingRecorder 診断レポート (--diagnose)");
        sb.AppendLine("========================================================");
        sb.Append("実行日時      : ").AppendLine(StartedAtLocal.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        sb.Append("バージョン    : ").AppendLine(AppVersion);
        sb.Append("OS            : ").AppendLine(OperatingSystem);
        sb.Append("プロセス      : ").Append(ProcessArchitecture)
          .Append(IsElevated ? "（管理者権限で実行中）" : "（一般ユーザー権限）").AppendLine();
        sb.Append("CPU / RAM     : ").Append(LogicalCores).Append(" 論理コア / ")
          .Append(TotalRamGb.ToString("F1", CultureInfo.InvariantCulture)).AppendLine(" GB");
        sb.Append("データ保存先  : ").Append(DataRoot)
          .AppendLine(PortableMode ? "（ポータブルモード）" : "（ユーザープロファイル）");
        sb.AppendLine();

        foreach (var check in Checks)
        {
            sb.Append('[').Append(Marker(check.Status)).Append("] ")
              .Append(check.Id).Append("  ").AppendLine(check.Title);
            sb.Append("      ").AppendLine(check.Detail);
            if (!string.IsNullOrWhiteSpace(check.Remedy))
            {
                sb.Append("      対処: ").AppendLine(check.Remedy);
            }

            sb.AppendLine();
        }

        sb.AppendLine("--------------------------------------------------------");
        sb.Append("総合判定: ").AppendLine(Overall switch
        {
            DiagnosticStatus.Ok => "OK — 検査した範囲では問題は見つかりませんでした。",
            DiagnosticStatus.Warning => "警告あり — 録音は可能ですが、上記を確認してください。",
            DiagnosticStatus.Error => "エラーあり — 上記の対処を行ってから録音してください。",
            _ => "判定できませんでした。",
        });
        sb.AppendLine();
        sb.AppendLine("※ この診断は実機の状態を実測したものです。ただし Zoom / Teams / Meet の");
        sb.AppendLine("   実音声、長時間録音の同期精度、スリープ抑制などは docs/WINDOWS_E2E_TEST.md");
        sb.AppendLine("   の手順で別途確認してください。");

        return sb.ToString();
    }

    private static string Marker(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Ok => " OK ",
        DiagnosticStatus.Warning => "警告",
        DiagnosticStatus.Error => "ｴﾗｰ",
        _ => "未実施",
    };
}
