using System.Text.Json;
using System.Text.Json.Nodes;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Persistence;

/// <summary>Loads and saves <see cref="AppSettings"/>. No cloud sync, ever.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    public SettingsStore(string? path = null, ILogger? logger = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Path => _path;

    public AppSettings Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                _logger.Info(nameof(SettingsStore), "No settings file found; starting with defaults.");
                return CreateDefault();
            }

            string json;
            try
            {
                json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
            }
            catch (IOException ex)
            {
                _logger.Error(nameof(SettingsStore), "Settings could not be read; starting with defaults.", ex);
                return CreateDefault();
            }

            AppSettings? settings;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonStore.Options);
            }
            catch (JsonException ex)
            {
                // A settings file this build cannot parse is never a reason to
                // refuse to start: the user would lose the recorder over a
                // preference.
                _logger.Error(nameof(SettingsStore), "Settings could not be parsed; starting with defaults.", ex);
                return CreateDefault();
            }

            if (settings is null)
            {
                return CreateDefault();
            }

            if (settings.Version != AppSettings.CurrentVersion)
            {
                _logger.Warn(nameof(SettingsStore), $"Settings version {settings.Version} migrated to {AppSettings.CurrentVersion}.");
                Migrate(settings, json, _logger);
                settings.Version = AppSettings.CurrentVersion;
            }

            settings.Processing ??= new AudioProcessingSettings();
            return settings;
        }
    }

    /// <summary>
    /// Carries a settings file written before live transcription was removed
    /// into the shape this build expects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two settings changed meaning rather than disappearing, and both are worth
    /// moving rather than defaulting:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>refineTranscriptAfterRecording</c> used to mean "keep per-stream audio
    /// so the second pass can run". Keeping that audio is now what makes a
    /// recording transcribable at all, so it becomes
    /// <see cref="AppSettings.KeepRecognitionAudio"/>. Someone who switched it
    /// off to save disk keeps that choice.
    /// </description></item>
    /// <item><description>
    /// <c>refinementModelId</c> was the accurate model. It is now simply
    /// <i>the</i> model, so it wins over the old live model id, which was chosen
    /// for speed and is no longer what anybody wants.
    /// </description></item>
    /// </list>
    /// <para>
    /// Everything else that is gone - chunk lengths, beam sizes, gate and
    /// compressor constants - is left in the file and ignored. Unknown JSON
    /// properties are not an error, so an old file loads without complaint, and
    /// the next save writes the current shape.
    /// </para>
    /// </remarks>
    internal static void Migrate(AppSettings settings, string json, ILogger logger)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        if (node is not JsonObject root)
        {
            return;
        }

        if (TryGetBool(root, "refineTranscriptAfterRecording", out var keepAudio))
        {
            settings.KeepRecognitionAudio = keepAudio;
        }

        if (TryGetBool(root, "deleteRecognitionAudioAfterRefinement", out var deleteAfter))
        {
            settings.DeleteRecognitionAudioAfterTranscription = deleteAfter;
        }

        var refinementModelId = TryGetString(root, "refinementModelId");
        if (!string.IsNullOrWhiteSpace(refinementModelId))
        {
            settings.SttModelId = refinementModelId;
            logger.Info(
                nameof(SettingsStore),
                $"The former second-pass model '{refinementModelId}' is now the transcription model.");
        }

        if (settings.Profile is not null
            && root["profile"] is JsonObject profile
            && TryGetString(profile, "refinementModelId") is { Length: > 0 } profileModel)
        {
            settings.Profile.SttModelId = profileModel;
        }

        // The DSP settings from before this change describe stages that do not
        // exist any more (gate thresholds, compressor ratios, limiter ceilings).
        // Deserializing them would be harmless but meaningless, so the audio
        // settings are simply rebuilt from the current defaults.
        settings.Processing = new AudioProcessingSettings();
    }

    private static bool TryGetBool(JsonObject root, string name, out bool value)
    {
        value = false;
        return root[name] is JsonValue json && json.TryGetValue(out value);
    }

    private static string? TryGetString(JsonObject root, string name)
        => root[name] is JsonValue json && json.TryGetValue<string>(out var value) ? value : null;

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            try
            {
                JsonStore.WriteAtomic(_path, settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error(nameof(SettingsStore), "Failed to persist settings.", ex);
            }
        }
    }

    public static AppSettings CreateDefault() => new()
    {
        SaveRoot = AppPaths.DefaultSaveRoot(),
        ModelDirectory = AppPaths.ModelDirectory,
    };
}
