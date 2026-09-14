using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Persistence;
using Xunit;

namespace MeetingRecorder.Core.Tests;

/// <summary>
/// Reading a settings file written before live transcription was removed.
/// </summary>
/// <remarks>
/// Someone upgrading has a settings.json full of properties that no longer
/// exist - chunk lengths, gate thresholds, compressor ratios, a second model id.
/// None of that may stop the application from starting, and the two settings
/// that changed meaning rather than disappearing have to carry across, or the
/// upgrade silently undoes choices the user made.
/// </remarks>
public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A settings.json exactly as the previous version wrote it.</summary>
    private const string LegacySettings = """
    {
      "version": 1,
      "setupCompleted": true,
      "microphoneDeviceName": "Headset Microphone",
      "saveRoot": "C:\\Users\\test\\Documents\\MeetingRecorder",
      "format": "Wav",
      "mp3BitrateKbps": 96,
      "processing": {
        "dcBlockerEnabled": true,
        "gateEnabled": true,
        "gateThresholdDb": -52,
        "gateMaxAttenuationDb": 12,
        "agcEnabled": true,
        "targetRmsDb": -20,
        "compressorEnabled": true,
        "compressorRatio": 2.5,
        "limiterEnabled": true,
        "limiterCeilingDb": -1,
        "mixHeadroomDb": -3
      },
      "sttModelId": "whisper-base-q5_1",
      "sttLanguage": "ja",
      "sttEnabled": true,
      "profile": {
        "sttModelId": "whisper-base-q5_1",
        "sttThreads": 6,
        "chunkSeconds": 4,
        "chunkOverlapMs": 400,
        "vadEnabled": true,
        "beamSize": 1,
        "silenceFlushMs": 450,
        "refinementModelId": "whisper-medium-q5_0",
        "diarizationEnabled": true,
        "minutesEnabled": true,
        "logicalCores": 12,
        "totalRamGb": 16,
        "cpuScore": 1.1,
        "rationale": "..."
      },
      "refineTranscriptAfterRecording": true,
      "refinementModelId": "whisper-medium-q5_0",
      "deleteRecognitionAudioAfterRefinement": false,
      "diarizationEnabled": true,
      "minutesEnabled": true,
      "preventSleepWhileRecording": true,
      "autoSaveRecordings": true,
      "autoSaveIntervalSeconds": 15
    }
    """;

    private AppSettings LoadLegacy(string json = LegacySettings)
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, json, System.Text.Encoding.UTF8);
        return new SettingsStore(path).Load();
    }

    [Fact]
    public void AnOldSettingsFileLoadsWithoutThrowing()
    {
        var settings = LoadLegacy();

        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
        Assert.True(settings.SetupCompleted);
        Assert.Equal("Headset Microphone", settings.MicrophoneDeviceName);
        Assert.Equal(15, settings.AutoSaveIntervalSeconds);
    }

    [Fact]
    public void TheModelTheUserChoseForAccuracyBecomesTheTranscriptionModel()
    {
        // refinementModelId was the model they were prepared to wait for. That
        // is now simply the model, so it wins over the live one, which was
        // picked for speed and nobody wants any more.
        var settings = LoadLegacy();

        Assert.Equal(ModelCatalog.WhisperMedium, settings.SttModelId);
        Assert.Equal(ModelCatalog.WhisperMedium, settings.Profile!.SttModelId);
    }

    [Fact]
    public void KeepingThePerStreamAudioCarriesOverFromTheOldSecondPassSetting()
    {
        var settings = LoadLegacy();
        Assert.True(settings.KeepRecognitionAudio);

        var optedOut = LoadLegacy(LegacySettings.Replace(
            "\"refineTranscriptAfterRecording\": true", "\"refineTranscriptAfterRecording\": false"));
        Assert.False(optedOut.KeepRecognitionAudio);
    }

    [Fact]
    public void TheDeleteAfterSettingCarriesOverToItsNewName()
    {
        var settings = LoadLegacy();
        Assert.False(settings.DeleteRecognitionAudioAfterTranscription);
    }

    [Fact]
    public void TheOldDspConstantsAreDiscardedRatherThanRevived()
    {
        // The old file configures a gate, an AGC, a compressor and a limiter.
        // None of those exist, and the audio settings must come back as the
        // current defaults rather than as anything derived from them.
        var settings = LoadLegacy();
        var defaults = new AudioProcessingSettings();

        Assert.NotNull(settings.Processing);
        Assert.True(settings.Processing.DcBlockerEnabled);
        Assert.True(settings.Processing.PeakNormalizationEnabled);
        Assert.Equal(defaults.NormalizationTargetPeakDbFs, settings.Processing.NormalizationTargetPeakDbFs);

        // Compared against the defaults rather than a literal: what this test is
        // about is that the legacy block is discarded, not what any particular
        // constant happens to be this month.
        Assert.Equal(defaults.MixGainPerStreamDb, settings.Processing.MixGainPerStreamDb, 4);
    }

    /// <summary>
    /// 96 kbps was never something a user could pick - it was the hard-wired
    /// default - so a file carrying it is carrying a default, not a decision.
    /// </summary>
    [Fact]
    public void TheOldHardWiredMp3BitrateIsRaisedToTheNewDefault()
    {
        var settings = LoadLegacy();

        Assert.Equal(192, settings.Mp3BitrateKbps);
        Assert.Equal(192, new AppSettings().Mp3BitrateKbps);
    }

    /// <summary>A bitrate that was edited by hand is a decision, and it stands.</summary>
    [Fact]
    public void ABitrateTheUserChoseIsLeftAlone()
    {
        var settings = LoadLegacy(
            LegacySettings.Replace("\"mp3BitrateKbps\": 96", "\"mp3BitrateKbps\": 320"));

        Assert.Equal(320, settings.Mp3BitrateKbps);
    }

    [Fact]
    public void SavingAfterAMigrationWritesTheCurrentShapeAndReadsBackTheSame()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, LegacySettings, System.Text.Encoding.UTF8);

        var store = new SettingsStore(path);
        var migrated = store.Load();
        store.Save(migrated);

        var reloaded = store.Load();
        Assert.Equal(AppSettings.CurrentVersion, reloaded.Version);
        Assert.Equal(migrated.SttModelId, reloaded.SttModelId);
        Assert.Equal(migrated.KeepRecognitionAudio, reloaded.KeepRecognitionAudio);
        Assert.Equal(migrated.DeleteRecognitionAudioAfterTranscription, reloaded.DeleteRecognitionAudioAfterTranscription);

        // And the properties that are gone are no longer written out.
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("refine", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("compressor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("limiter", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chunkSeconds", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnreadableSettingsFileStartsTheApplicationRatherThanStoppingIt()
    {
        // Losing a preference is a nuisance. Refusing to start is losing the
        // recorder.
        var path = Path.Combine(_root, "broken.json");
        File.WriteAllText(path, "{ this is not json", System.Text.Encoding.UTF8);

        var settings = new SettingsStore(path).Load();

        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
        Assert.True(settings.SttEnabled);
    }

    /// <summary>A settings.json from the build immediately before the speed profiles.</summary>
    private const string VersionThreeSettings = """
    {
      "version": 3,
      "setupCompleted": true,
      "saveRoot": "C:\\Users\\test\\Documents\\MeetingRecorder",
      "format": "Mp3",
      "mp3BitrateKbps": 192,
      "sttModelId": "whisper-small-q5_1",
      "sttLanguage": "ja",
      "sttEnabled": true,
      "diarizationEnabled": true,
      "minutesEnabled": true,
      "keepRecognitionAudio": true
    }
    """;

    [Fact]
    public void AFileFromBeforeTheSpeedProfilesGetsTheBalancedDefault()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, VersionThreeSettings);

        var settings = new SettingsStore(path).Load();

        // The whole point of the change is to offer the choice, so an existing
        // user lands on the new default and sees it in the settings window -
        // rather than being left on the slowest setting where they would never
        // learn the other two exist.
        Assert.Equal(TranscriptionProfile.Balanced, settings.TranscriptionProfile);
        Assert.Equal(MinutesGenerationMode.LocalLlm, settings.MinutesMode);
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);

        // ...and nothing they did choose is lost on the way.
        Assert.Equal("whisper-small-q5_1", settings.SttModelId);
        Assert.Equal(RecordingFormat.Mp3, settings.Format);
        Assert.Equal(192, settings.Mp3BitrateKbps);
        Assert.True(settings.KeepRecognitionAudio);
    }

    [Fact]
    public void AVersionThreeFileKeepsTheAudioChoicesItsOwnerMade()
    {
        // A migration must only rewrite what changed meaning. Version 3 already
        // had this processing shape, so rebuilding it from defaults would throw
        // away settings the user chose, purely because a version number moved.
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(
            path,
            VersionThreeSettings.Replace(
                "\"keepRecognitionAudio\": true",
                "\"keepRecognitionAudio\": true,\n      \"processing\": { \"peakNormalizationEnabled\": false, \"normalizationTargetPeakDbFs\": -3.0 }",
                StringComparison.Ordinal));

        var settings = new SettingsStore(path).Load();

        Assert.False(settings.Processing.PeakNormalizationEnabled);
        Assert.Equal(-3.0, settings.Processing.NormalizationTargetPeakDbFs);

        // ...while a property that version 3 did not have takes this build's
        // default rather than tripping over its absence.
        Assert.False(settings.Processing.MicrophoneListeningEqEnabled);
        Assert.Equal(2500.0, settings.Processing.MicrophoneListeningEqCornerHz);
    }

    [Fact]
    public void AVersionOneFileStillHasItsRemovedStagesRebuiltFromDefaults()
    {
        // The other side of the same rule: a version 1 processing block
        // describes a gate, an AGC, a compressor and a limiter, none of which
        // exist. That one really does have to be rebuilt.
        var path = Path.Combine(_root, "legacy.json");
        File.WriteAllText(path, LegacySettings);

        var settings = new SettingsStore(path).Load();
        var defaults = new AudioProcessingSettings();

        Assert.Equal(defaults.MixGainPerStreamDb, settings.Processing.MixGainPerStreamDb);
        Assert.Equal(defaults.PeakNormalizationEnabled, settings.Processing.PeakNormalizationEnabled);
    }

    [Fact]
    public void TheMicrophoneListeningEqualizerStartsOffOnAnUpgrade()
    {
        // It corrects a spectral tilt, and whether this user's microphone has
        // one is a measurement nobody has taken yet. Switching it on for
        // everybody at upgrade time would be exactly the unfounded change the
        // brief rules out.
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, VersionThreeSettings);

        var settings = new SettingsStore(path).Load();

        Assert.False(settings.Processing.MicrophoneListeningEqEnabled);
    }

    [Fact]
    public void ARoundTripKeepsTheNewSettings()
    {
        var path = Path.Combine(_root, "roundtrip.json");
        var store = new SettingsStore(path);

        var written = SettingsStore.CreateDefault();
        written.TranscriptionProfile = TranscriptionProfile.Fast;
        written.MinutesMode = MinutesGenerationMode.Extractive;
        written.Processing.MicrophoneListeningEqEnabled = true;
        written.Processing.MicrophoneListeningEqGainDb = 3.0;
        store.Save(written);

        var read = store.Load();

        Assert.Equal(TranscriptionProfile.Fast, read.TranscriptionProfile);
        Assert.Equal(MinutesGenerationMode.Extractive, read.MinutesMode);
        Assert.True(read.Processing.MicrophoneListeningEqEnabled);
        Assert.Equal(3.0, read.Processing.MicrophoneListeningEqGainDb);
    }
}
