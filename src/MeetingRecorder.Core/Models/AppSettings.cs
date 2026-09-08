using System.Text.Json.Serialization;

namespace MeetingRecorder.Core.Models;

/// <summary>
/// Everything the app remembers between runs. Stored as JSON next to the
/// executable (portable mode) or under %LOCALAPPDATA% when the install folder
/// is read-only. Contains no credentials - the app has no account of any kind.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Set once the first-run wizard has been completed.</summary>
    public bool SetupCompleted { get; set; }

    // ---- Devices ---------------------------------------------------------

    /// <summary>MMDevice id of the capture endpoint, or null for "system default".</summary>
    public string? MicrophoneDeviceId { get; set; }

    public string? MicrophoneDeviceName { get; set; }

    /// <summary>MMDevice id of the render endpoint that is loopback-captured, or null for "system default".</summary>
    public string? RenderDeviceId { get; set; }

    public string? RenderDeviceName { get; set; }

    /// <summary>Follow the Windows default device when the user switches it mid-meeting.</summary>
    public bool FollowDefaultDevices { get; set; } = true;

    /// <summary>
    /// Start recordings with the microphone silenced. Recording PC audio only.
    /// </summary>
    /// <remarks>
    /// Persisted deliberately: someone who records webinars never wants the room
    /// microphone, and having to remember the toggle every meeting is how a
    /// private conversation ends up in a file.
    /// </remarks>
    public bool MicrophoneMuted { get; set; }

    /// <summary>Start recordings with PC audio silenced. Recording the microphone only.</summary>
    public bool SystemAudioMuted { get; set; }

    // ---- Output ----------------------------------------------------------

    /// <summary>Root folder that receives one sub-folder per meeting.</summary>
    public string? SaveRoot { get; set; }

    public RecordingFormat Format { get; set; } = RecordingFormat.Wav;

    /// <summary>
    /// MP3 bitrate in kbps. 96 kbps mono/joint-stereo at 16 kHz-48 kHz is the
    /// documented default: speech stays clearly intelligible while an hour of
    /// meeting costs ~43 MB. See docs/ARCHITECTURE.md "Recording format".
    /// </summary>
    public int Mp3BitrateKbps { get; set; } = 96;

    // ---- Audio processing ------------------------------------------------

    public AudioProcessingSettings Processing { get; set; } = new();

    // ---- Speech to text --------------------------------------------------

    /// <summary>Catalog id of the Whisper model to use, or null to auto-select.</summary>
    public string? SttModelId { get; set; }

    /// <summary>BCP-47-ish language code handed to whisper.cpp.</summary>
    public string SttLanguage { get; set; } = "ja";

    public bool SttEnabled { get; set; } = true;

    /// <summary>Folder that stores downloaded AI models. Never inside the repo.</summary>
    public string? ModelDirectory { get; set; }

    /// <summary>Result of the last hardware benchmark; re-used until hardware changes.</summary>
    public PerformanceProfile? Profile { get; set; }

    /// <summary>
    /// After recording stops, transcribe the meeting again with the accurate
    /// model and replace the live transcript.
    /// </summary>
    /// <remarks>
    /// The live transcript is tuned for latency and the second pass for accuracy;
    /// this is what lets both be true. It needs the per-stream recognition audio,
    /// about 115 MB per hour per stream, kept in the meeting folder while the
    /// recording is in progress.
    /// </remarks>
    public bool RefineTranscriptAfterRecording { get; set; } = true;

    /// <summary>
    /// Catalog id of the model used by the second pass, or null to use the
    /// largest model the machine can hold.
    /// </summary>
    public string? RefinementModelId { get; set; }

    /// <summary>Delete the per-stream recognition audio once the second pass has finished.</summary>
    public bool DeleteRecognitionAudioAfterRefinement { get; set; } = true;

    // ---- Optional / experimental ----------------------------------------

    public bool DiarizationEnabled { get; set; } = true;

    public bool MinutesEnabled { get; set; } = true;

    /// <summary>Catalog id of the local GGUF model used for minutes, or null.</summary>
    public string? LlmModelId { get; set; }

    // ---- Behaviour -------------------------------------------------------

    public bool PreventSleepWhileRecording { get; set; } = true;

    /// <summary>
    /// When true a finished recording is kept in its meeting folder
    /// automatically. When false it stays in the working folder until the user
    /// exports it, and is offered for deletion when the app closes.
    /// </summary>
    /// <remarks>
    /// Turning this off never means "do not write to disk". Audio is always
    /// streamed to a working file, because holding an hour of a meeting in RAM
    /// would lose everything to one crash - the exact failure the recovery
    /// journal exists to prevent. What the switch controls is whether the result
    /// is filed away automatically or waits for the user to say where it goes.
    /// </remarks>
    public bool AutoSaveRecordings { get; set; } = true;

    /// <summary>How often the recovery journal is flushed to disk.</summary>
    public int AutoSaveIntervalSeconds { get; set; } = 15;

    [JsonIgnore]
    public bool HasUsableSaveRoot => !string.IsNullOrWhiteSpace(SaveRoot);

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
