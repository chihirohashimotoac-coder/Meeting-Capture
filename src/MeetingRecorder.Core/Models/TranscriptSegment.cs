using System.Text.Json.Serialization;

namespace MeetingRecorder.Core.Models;

/// <summary>
/// One recognized utterance. Timestamps are relative to the start of the
/// recording so that they line up with the saved audio file.
/// </summary>
public sealed class TranscriptSegment
{
    /// <summary>Stable identifier, used for edit / speaker-rename operations.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Offset from recording start, in milliseconds.</summary>
    public long StartMs { get; set; }

    /// <summary>Offset from recording start, in milliseconds.</summary>
    public long EndMs { get; set; }

    /// <summary>Ground-truth capture stream (never guessed).</summary>
    public AudioSourceKind Source { get; set; }

    /// <summary>
    /// Diarization cluster id within <see cref="Source"/>, or <c>null</c> when
    /// diarization is disabled or degraded away.
    /// </summary>
    public int? SpeakerId { get; set; }

    /// <summary>
    /// User assigned display name for <see cref="SpeakerId"/>. Never inferred by
    /// the model - only a human may put a real person's name here.
    /// </summary>
    public string? SpeakerName { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>True once a human edited the text.</summary>
    public bool IsEdited { get; set; }

    /// <summary>Average token probability reported by the STT engine (0..1), if available.</summary>
    public float? Confidence { get; set; }

    [JsonIgnore]
    public TimeSpan Start => TimeSpan.FromMilliseconds(StartMs);

    [JsonIgnore]
    public TimeSpan End => TimeSpan.FromMilliseconds(EndMs);

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(0, EndMs - StartMs));

    /// <summary>
    /// "マイク" or "PC音声 / Speaker 2" - the source is authoritative, the
    /// speaker suffix is only added when diarization produced a cluster.
    /// </summary>
    public string SourceLabel()
    {
        var baseLabel = Source.ToDisplayLabel();
        if (!string.IsNullOrWhiteSpace(SpeakerName))
        {
            return $"{baseLabel} / {SpeakerName}";
        }

        return SpeakerId.HasValue ? $"{baseLabel} / Speaker {SpeakerId.Value + 1}" : baseLabel;
    }

    public static string FormatTimestamp(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    public string TimestampLabel() => FormatTimestamp(StartMs);
}
