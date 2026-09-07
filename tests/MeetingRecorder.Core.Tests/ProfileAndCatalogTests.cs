using MeetingRecorder.Core.Benchmark;
using MeetingRecorder.Core.ModelManagement;
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
    public void PinnedHashesAreWellFormedWhenPresent()
    {
        foreach (var model in ModelCatalog.All.Where(m => m.Sha256 is not null))
        {
            Assert.Equal(64, model.Sha256!.Length);
            Assert.True(model.Sha256.All(Uri.IsHexDigit), $"{model.Id} has a non-hex SHA-256");
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
    public void AFastMachineGetsABetterModelThanASlowOne()
    {
        var fast = ProfileSelector.Select(Machine(4.0));
        var slow = ProfileSelector.Select(Machine(0.2));

        var ladder = new[]
        {
            ModelCatalog.WhisperTiny,
            ModelCatalog.WhisperBase,
            ModelCatalog.WhisperSmall,
            ModelCatalog.WhisperMedium,
        };

        Assert.True(
            Array.IndexOf(ladder, fast.SttModelId) > Array.IndexOf(ladder, slow.SttModelId),
            $"fast machine chose {fast.SttModelId}, slow machine chose {slow.SttModelId}");
    }

    [Fact]
    public void TheReferenceMachineStaysWithinItsRealTimeBudget()
    {
        // Core i5-1335U class: the probe is normalised so this scores about 1.0.
        var profile = ProfileSelector.Select(Machine(1.0, cores: 12, ramGb: 16));
        var model = ModelCatalog.RequireSpeechModel(profile.SttModelId);
        var estimated = ProfileSelector.EstimateRealTimeFactor(model, Machine(1.0, 12, 16));

        Assert.True(estimated <= ProfileSelector.TargetRealTimeFactor,
            $"selected {profile.SttModelId} with estimated RTF {estimated:F2}");
        Assert.NotEqual(ModelCatalog.WhisperMedium, profile.SttModelId);
    }

    [Fact]
    public void AMachineWithLittleRamNeverGetsALargeModel()
    {
        var profile = ProfileSelector.Select(Machine(10.0, cores: 8, ramGb: 4));
        var model = ModelCatalog.RequireSpeechModel(profile.SttModelId);

        Assert.True(model.RequiredRamMb * 3.0 <= 4 * 1024);
        Assert.False(profile.MinutesEnabled, "an experimental LLM must not be enabled on a 4 GB machine");
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
    public void DegradationFollowsTheSpecifiedLadderAndNeverTouchesRecording()
    {
        var profile = ProfileSelector.Select(Machine(2.0));
        profile.DiarizationEnabled = true;
        profile.MinutesEnabled = true;
        profile.SttModelId = ModelCatalog.WhisperSmall;

        // 1. Speaker diarization goes first.
        var step1 = ProfileSelector.Degrade(profile, 1.2);
        Assert.NotNull(step1);
        Assert.False(step1!.DiarizationEnabled);
        Assert.Equal(ModelCatalog.WhisperSmall, step1.SttModelId);

        // 2. Then a lighter recognition model.
        var step2 = ProfileSelector.Degrade(step1, 1.2);
        Assert.NotNull(step2);
        Assert.Equal(ModelCatalog.WhisperBase, step2!.SttModelId);

        var step3 = ProfileSelector.Degrade(step2, 1.2);
        Assert.Equal(ModelCatalog.WhisperTiny, step3!.SttModelId);

        // 3. Finally the local LLM.
        var step4 = ProfileSelector.Degrade(step3, 1.2);
        Assert.False(step4!.MinutesEnabled);

        // Nothing left to give up: recording and transcription are never dropped.
        Assert.Null(ProfileSelector.Degrade(step4, 1.2));
    }

    [Fact]
    public void NoDegradationHappensWhileTheMachineKeepsUp()
    {
        var profile = ProfileSelector.Select(Machine(2.0));
        Assert.Null(ProfileSelector.Degrade(profile, 0.4));
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
        Assert.Contains("RTF", profile.Rationale);
    }
}
