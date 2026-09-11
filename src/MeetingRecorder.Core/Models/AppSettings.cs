using System.Text.Json.Serialization;

namespace MeetingRecorder.Core.Models;

/// <summary>
/// Everything the app remembers between runs. Stored as JSON next to the
/// executable (portable mode) or under %LOCALAPPDATA% when the install folder
/// is read-only. Contains no credentials - the app has no account of any kind.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Bumped to 2 when live transcription was removed. A version 1 file is
    /// still read: <see cref="Persistence.SettingsStore"/> maps the settings
    /// that changed meaning and ignores the ones that no longer exist, so an
    /// upgrade keeps the user's choices instead of resetting them.
    /// </summary>
    public const int CurrentVersion = 3;

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

    /// <summary>
    /// Format the finished meeting audio is stored in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MP3 by default, at <see cref="Mp3BitrateKbps"/>. A meeting recorder that
    /// fills a drive quietly is a meeting recorder people stop using, and WAV
    /// costs about four times as much per hour for audio that is going to be
    /// listened back to and transcribed, not mastered.
    /// </para>
    /// <para>
    /// Nothing about transcription depends on this. Whisper reads the per-stream
    /// 16 kHz working audio, which is always PCM and is written straight from the
    /// capture; the meeting file is for people. Anyone who wants the untouched
    /// waveform can choose WAV, and an environment with no MP3 encoder falls back
    /// to it automatically and says so.
    /// </para>
    /// </remarks>
    public RecordingFormat Format { get; set; } = RecordingFormat.Mp3;

    /// <summary>
    /// MP3 bitrate in kbps. 96 kbps mono/joint-stereo at 16 kHz-48 kHz is the
    /// documented default: speech stays clearly intelligible while an hour of
    /// meeting costs ~43 MB. See docs/ARCHITECTURE.md "Recording format".
    /// </summary>
    /// <summary>
    /// Bitrate for the MP3 conversion, in kbps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 192 kbps for a mono meeting recording is generous - MPEG-1 Layer III at
    /// this rate on one channel is transparent for speech by a wide margin. It
    /// is the default because the file is an archive of something that happened
    /// once: 86 MB an hour instead of 43 is a trade almost every user would take
    /// to never wonder whether the codec ate a word.
    /// </para>
    /// <para>
    /// The encoder offers a fixed set of output formats and the nearest one to
    /// this value is used, so the bitrate actually achieved is recorded in the
    /// meeting's metadata rather than assumed from this setting.
    /// </para>
    /// </remarks>
    public int Mp3BitrateKbps { get; set; } = 192;

    /// <summary>
    /// The bitrates the settings window offers, in kbps.
    /// </summary>
    /// <remarks>
    /// MPEG-1 Layer III rates only, so nothing here has to be rounded to
    /// something else by the encoder. Below 128 the point of choosing MP3 at all
    /// is size, and above 256 the file stops being meaningfully smaller than the
    /// WAV it came from.
    /// </remarks>
    public static IReadOnlyList<int> Mp3Bitrates { get; } = new[] { 128, 160, 192, 256, 320 };

    // ---- Audio processing ------------------------------------------------

    public AudioProcessingSettings Processing { get; set; } = new();

    // ---- Speech to text --------------------------------------------------

    /// <summary>
    /// Catalog id of the Whisper model transcription uses, or null to
    /// auto-select.
    /// </summary>
    /// <remarks>
    /// There is one model setting because there is one transcription pass. The
    /// application used to keep two - a small one to keep up with the meeting
    /// and a larger one to redo the job afterwards - and that pair only existed
    /// to serve live captions, which no longer exist.
    /// </remarks>
    public string? SttModelId { get; set; }

    /// <summary>BCP-47-ish language code handed to whisper.cpp.</summary>
    public string SttLanguage { get; set; } = "ja";

    /// <summary>
    /// Whether the transcription feature is offered at all.
    /// </summary>
    /// <remarks>
    /// Turning it off does not change how a meeting is recorded. It stops the
    /// working audio from being kept and greys out the transcription button, for
    /// someone who only wants the recording.
    /// </remarks>
    public bool SttEnabled { get; set; } = true;

    /// <summary>Folder that stores downloaded AI models. Never inside the repo.</summary>
    public string? ModelDirectory { get; set; }

    /// <summary>Result of the last hardware benchmark; re-used until hardware changes.</summary>
    public PerformanceProfile? Profile { get; set; }

    /// <summary>
    /// Keep a per-stream 16 kHz copy of the meeting so it can be transcribed
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This has to be decided before the meeting, not after it: once the
    /// microphone and the loopback have been summed into one file, which words
    /// came from the room and which came out of the speakers is gone and cannot
    /// be worked out again. The cost is about
    /// <see cref="Pipeline.RecognitionAudioNames.MegabytesPerStreamPerHour"/> MB
    /// per hour per stream, in the meeting folder.
    /// </para>
    /// <para>
    /// Turning it off leaves an ordinary recording that can still be played and
    /// kept - it simply cannot be transcribed by this application afterwards.
    /// </para>
    /// </remarks>
    public bool KeepRecognitionAudio { get; set; } = true;

    /// <summary>
    /// Delete the working audio once a transcription has completed
    /// successfully.
    /// </summary>
    /// <remarks>
    /// Only after a success. A transcription that failed or that the user
    /// cancelled leaves the audio exactly where it was, because the obvious next
    /// thing to do is try again - possibly with a different model - and deleting
    /// the input would make that impossible.
    /// </remarks>
    public bool DeleteRecognitionAudioAfterTranscription { get; set; } = true;

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
