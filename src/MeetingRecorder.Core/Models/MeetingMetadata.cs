using System.Text.Json.Serialization;

namespace MeetingRecorder.Core.Models;

/// <summary>Written to <c>metadata.json</c> inside every meeting folder.</summary>
public sealed class MeetingMetadata
{
    public const int CurrentVersion = 2;

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

    /// <summary>
    /// True when the audio came from a file the user imported rather than from
    /// this application's own capture.
    /// </summary>
    /// <remarks>
    /// It decides what may be claimed about the recording. An imported file is
    /// a single anonymous source: there is no microphone leg, no loopback leg
    /// and no evidence of who was speaking, so speaker naming is not offered
    /// for it rather than being offered and guessed at.
    /// </remarks>
    public bool IsImported { get; set; }

    /// <summary>
    /// Full path of the file that was imported, so it can be converted again if
    /// the working audio is deleted. The file itself is never modified.
    /// </summary>
    public string? ImportedFromPath { get; set; }

    /// <summary>
    /// The single constant gain applied to the whole audio file after recording,
    /// in dB, or null when none was applied. Recorded so that a measurement
    /// taken from the file can be related back to what the microphone produced.
    /// </summary>
    public double? AppliedGainDb { get; set; }

    /// <summary>
    /// True when the captured or imported audio was already clipped when this
    /// application received it. Never repaired, only reported.
    /// </summary>
    public bool SourceClipped { get; set; }

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
