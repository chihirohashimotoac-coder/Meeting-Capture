using System.Text.Json.Serialization;

namespace MeetingRecorder.Core.Models;

/// <summary>Written to <c>metadata.json</c> inside every meeting folder.</summary>
public sealed class MeetingMetadata
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string MeetingId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Folder name, e.g. <c>2026-09-07_1430_Meeting</c>.</summary>
    public string FolderName { get; set; } = string.Empty;

    public string? Title { get; set; }

    public DateTimeOffset StartedAtLocal { get; set; }

    public DateTimeOffset? StoppedAtLocal { get; set; }

    public double DurationSeconds { get; set; }

    public string AudioFileName { get; set; } = "meeting.wav";

    public RecordingFormat Format { get; set; } = RecordingFormat.Wav;

    public int SampleRate { get; set; } = 48000;

    public int Channels { get; set; } = 1;

    public string? MicrophoneDeviceName { get; set; }

    public string? RenderDeviceName { get; set; }

    public string? SttModelId { get; set; }

    public string? SttModelSha256 { get; set; }

    public bool DiarizationUsed { get; set; }

    public bool MinutesGenerated { get; set; }

    /// <summary>Named speakers as assigned by the user, keyed by "source:clusterId".</summary>
    public Dictionary<string, string> SpeakerNames { get; set; } = new();

    /// <summary>Set when the meeting was reconstructed by crash recovery.</summary>
    public bool RecoveredFromCrash { get; set; }

    /// <summary>Non-fatal problems worth surfacing to the user afterwards.</summary>
    public List<string> Warnings { get; set; } = new();

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
}
