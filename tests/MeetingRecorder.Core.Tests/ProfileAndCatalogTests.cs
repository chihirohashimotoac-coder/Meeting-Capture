using MeetingRecorder.Core.Benchmark;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;
using Xunit;

namespace MeetingRecorder.Core.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void EveryModelIsDownloadedOverHttpsFromADeclaredPublisher()
    {
        Assert.NotEmpty(ModelCatalog.SpeechModels);

        foreach (var model in ModelCatalog.All)
        {
            Assert.StartsWith("https://", model.Url, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(model.Publisher), $"{model.Id} has no publisher");
            Assert.False(string.IsNullOrWhiteSpace(model.License), $"{model.Id} has no licence");
            Assert.StartsWith("https://", model.LicenseUrl, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(model.PurposeDescription), $"{model.Id} has no purpose text");
            Assert.True(model.ApproximateSizeBytes > 0, $"{model.Id} has no declared size");
            Assert.True(model.RequiredRamMb > 0, $"{model.Id} has no RAM estimate");
        }
    }

    [Fact]
    public void ModelIdsAreUnique()
    {
        var ids = ModelCatalog.All.Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryModelHasAPinnedSha256()
    {
        // Without a pin the downloader can only record what it received, not
        // verify it. Every catalog entry's hash was measured by downloading the
        // real file in CI, so a new entry without one is a mistake.
        foreach (var model in ModelCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Sha256), $"{model.Id} has no pinned SHA-256");
            Assert.Equal(64, model.Sha256!.Length);
            Assert.True(model.Sha256.All(Uri.IsHexDigit), $"{model.Id} has a non-hex SHA-256");
        }
    }

    [Fact]
    public void EveryUrlPointsAtAModelFileRatherThanAPage()
    {
        // Guards the mistake the checksum tool once made: matching LicenseUrl
        // instead of Url, and cheerfully "verifying" licence pages.
        foreach (var model in ModelCatalog.All)
        {
            Assert.True(
                model.Url.EndsWith(".bin", StringComparison.Ordinal) || model.Url.EndsWith(".gguf", StringComparison.Ordinal),
                $"{model.Id} does not download a model file: {model.Url}");
            Assert.EndsWith(model.FileName, model.Url, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCatalogOffersALargeV3TurboBuild()
    {
        // Added deliberately, with a size and a hash measured from the file the
        // publisher actually serves (.github/workflows/model-hashes.yml).
        var turbo = ModelCatalog.RequireSpeechModel(ModelCatalog.WhisperLargeV3Turbo);

        Assert.Equal(574_041_195, turbo.ApproximateSizeBytes);
        Assert.Equal("394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", turbo.Sha256);
        Assert.EndsWith("ggml-large-v3-turbo-q5_0.bin", turbo.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void NoModelIsDescribedAsBeingForRealTimeTranscription()
    {
        // The catalog text is what a user reads before downloading half a
        // gigabyte. It must not promise a feature that no longer exists.
        foreach (var model in ModelCatalog.SpeechModels)
        {
            Assert.DoesNotContain("リアルタイム", model.PurposeDescription, StringComparison.Ordinal);
            Assert.DoesNotContain("リアルタイム", model.Notes ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SizeDisplayIsHumanReadable()
    {
        Assert.EndsWith("MB", ModelCatalog.RequireSpeechModel(ModelCatalog.WhisperBase).SizeDisplay);
        Assert.EndsWith("GB", ModelCatalog.TextGenerationModels[0].SizeDisplay);
    }
}

public class ProfileSelectorTests
{
    private static MachineCapability Machine(double score, int cores = 8, double ramGb = 16)
        => new(cores, ramGb, score, score, 100);

    [Fact]
    public void AFastMachineGetsAMoreAccurateModelThanASlowOne()
    {
        var fast = ProfileSelector.Select(Machine(20.0));
        var slow = ProfileSelector.Select(Machine(0.05));

        var ladder = new[]
        {
            ModelCatalog.WhisperTiny,
            ModelCatalog.WhisperBase,
            ModelCatalog.WhisperSmall,
            ModelCatalog.WhisperMedium,
            ModelCatalog.WhisperLargeV3Turbo,
        };

        Assert.True(
            Array.IndexOf(ladder, fast.SttModelId) > Array.IndexOf(ladder, slow.SttModelId),
            $"fast machine chose {fast.SttModelId}, slow machine chose {slow.SttModelId}");
    }

    [Fact]
    public void TheSelectorClimbsTowardsAccuracyRatherThanTowardsSpeed()
    {
        // The old selector could only ever pick something small, because a live
        // transcript had to keep up. Given enough machine, this one has to reach
        // the top of the ladder.
        var profile = ProfileSelector.Select(Machine(20.0, cores: 16, ramGb: 32));
        Assert.Equal(ModelCatalog.WhisperLargeV3Turbo, profile.SttModelId);
    }

    [Fact]
    public void TheAutomaticChoiceStaysWithinTheWaitingBudget()
    {
        var capability = Machine(1.0, cores: 12, ramGb: 16);
        var profile = ProfileSelector.Select(capability);
        var model = ModelCatalog.RequireSpeechModel(profile.SttModelId);
        var estimated = ProfileSelector.EstimateProcessingFactor(model, capability);

        Assert.True(
            estimated <= ProfileSelector.AcceptableProcessingFactor,
            $"selected {profile.SttModelId} with an estimated factor of {estimated:F2}");
        Assert.Equal(estimated, profile.EstimatedProcessingFactor, 3);
    }

    [Fact]
    public void EveryModelInTheCatalogRemainsSelectableByHandHoweverSlow()
    {
        // The budget caps what is chosen automatically, not what is offered. A
        // user who is happy to wait overnight for the best transcript must be
        // able to say so.
        var capability = Machine(1.0, cores: 12, ramGb: 16);
        var turbo = ModelCatalog.RequireSpeechModel(ModelCatalog.WhisperLargeV3Turbo);

        Assert.True(ProfileSelector.EstimateProcessingFactor(turbo, capability) > 0);
        Assert.True(ProfileSelector.FitsInMemory(turbo, capability), "16 GB must be able to hold large-v3-turbo q5_0");
    }

    [Fact]
    public void AMachineWithLittleRamNeverGetsALargeModel()
    {
        var profile = ProfileSelector.Select(Machine(10.0, cores: 8, ramGb: 4));
        var model = ModelCatalog.RequireSpeechModel(profile.SttModelId);

        Assert.True(model.RequiredRamMb * ProfileSelector.RequiredRamHeadroom <= 4 * 1024);
        Assert.False(profile.MinutesEnabled, "an experimental LLM must not be enabled on a 4 GB machine");
    }

    [Fact]
    public void AVerySlowMachineStillGetsAModelRatherThanNone()
    {
        // Falling off the bottom of the ladder would leave the user unable to
        // transcribe at all, which is worse than a slow transcription they chose
        // to start.
        var profile = ProfileSelector.Select(Machine(0.01, cores: 2, ramGb: 4));
        Assert.Equal(ModelCatalog.WhisperTiny, profile.SttModelId);
    }

    [Fact]
    public void ThreadCountLeavesHeadroomForCaptureAndTheUi()
    {
        Assert.Equal(2, CapabilityProbe.RecommendThreadCount(2));
        Assert.Equal(4, CapabilityProbe.RecommendThreadCount(8));
        Assert.Equal(6, CapabilityProbe.RecommendThreadCount(12));
        Assert.Equal(6, CapabilityProbe.RecommendThreadCount(32));
    }

    [Fact]
    public void NothingInTheProfileTunesALiveTranscript()
    {
        // Chunk lengths, overlaps, silence thresholds and a second model id were
        // all there to make a live caption keep up. There is no live caption.
        var forbidden = new[] { "Chunk", "Overlap", "SilenceFlush", "Beam", "Vad", "Refinement", "RealTime" };

        var offending = typeof(PerformanceProfile)
            .GetProperties()
            .Where(p => forbidden.Any(f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(offending.Count == 0, $"PerformanceProfile still exposes: {string.Join(", ", offending)}");
    }

    [Fact]
    public void OnlyPermissivelyLicensedLlmsAreSelectedAutomatically()
    {
        var model = ProfileSelector.SelectTextGenerationModel(Machine(2.0, 12, 16));
        Assert.NotNull(model);
        Assert.StartsWith("Apache", model!.License, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheProbeMeasuresTheMachineRatherThanReadingItsName()
    {
        var capability = CapabilityProbe.Measure();

        Assert.True(capability.LogicalCores >= 1);
        Assert.True(capability.TotalRamGb > 0);
        Assert.True(capability.SingleThreadScore > 0, "the probe must return a measured score");
        Assert.True(capability.ElapsedMs > 0);

        // The probe has to be quick enough to run during start-up.
        Assert.True(capability.ElapsedMs < 15000, $"probe took {capability.ElapsedMs:F0} ms");
    }

    [Fact]
    public void ProfileRationaleExplainsTheDecisionToTheUser()
    {
        var profile = ProfileSelector.Select(Machine(1.0, 12, 16));
        Assert.False(string.IsNullOrWhiteSpace(profile.Rationale));
        Assert.Contains("推定処理時間", profile.Rationale);
    }
}
