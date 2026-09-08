using System.Globalization;
using System.Text;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Benchmark;

/// <summary>
/// Turns a <see cref="MachineCapability"/> measurement into a concrete
/// <see cref="PerformanceProfile"/>, and adjusts it again at run time from the
/// real-time factor the recognizer actually achieves.
/// </summary>
public static class ProfileSelector
{
    /// <summary>
    /// Head-room target. An RTF of 0.6 means transcription consumes 60% of real
    /// time, so a burst of dense speech (which is slower than the average) still
    /// does not build a permanent backlog.
    /// </summary>
    public const double TargetRealTimeFactor = 0.6;

    /// <summary>Above this measured RTF the transcript falls behind for good; step down.</summary>
    public const double DegradeRealTimeFactor = 0.95;

    /// <summary>Below this the machine has room for a better model.</summary>
    public const double UpgradeRealTimeFactor = 0.35;

    /// <summary>
    /// Estimated RTF for whisper small-q5_1 at score 1.0 with the recommended
    /// thread count. Anchored to the reference machine in the requirements
    /// (Core i5-1335U); every other model scales from it via
    /// <see cref="ModelDescriptor.RelativeCost"/>. This is only the starting
    /// point - once a real inference has run, the measured value replaces it.
    /// </summary>
    private const double ReferenceRtfForSmall = 0.55;

    private static readonly string[] LadderIds =
    {
        ModelCatalog.WhisperTiny,
        ModelCatalog.WhisperBase,
        ModelCatalog.WhisperSmall,
        ModelCatalog.WhisperMedium,
    };

    public static double EstimateRealTimeFactor(ModelDescriptor model, MachineCapability capability)
    {
        var score = Math.Max(0.05, capability.MultiThreadScore);
        return ReferenceRtfForSmall * model.RelativeCost / score;
    }

    public static PerformanceProfile Select(MachineCapability capability)
    {
        var threads = CapabilityProbe.RecommendThreadCount(capability.LogicalCores);
        var reasons = new StringBuilder();
        reasons.Append(CultureInfo.InvariantCulture, $"CPU {capability.LogicalCores}コア / 実測スコア {capability.MultiThreadScore:F2} / RAM {capability.TotalRamGb:F1}GB。");

        ModelDescriptor chosen = ModelCatalog.RequireSpeechModel(ModelCatalog.WhisperTiny);
        foreach (var id in LadderIds)
        {
            var candidate = ModelCatalog.RequireSpeechModel(id);

            // Never pick a model that would not comfortably fit in RAM alongside
            // Windows, the browser and the conferencing client.
            if (capability.TotalRamGb * 1024 < candidate.RequiredRamMb * 3.0)
            {
                break;
            }

            var estimated = EstimateRealTimeFactor(candidate, capability);
            if (estimated > TargetRealTimeFactor)
            {
                break;
            }

            chosen = candidate;
        }

        var estimatedRtf = EstimateRealTimeFactor(chosen, capability);
        reasons.Append(CultureInfo.InvariantCulture, $" 推定RTF {estimatedRtf:F2} により「{chosen.DisplayName}」を選択。");

        // Diarization adds roughly 10-15% on top of STT; only enable it when the
        // machine has clear head-room, and never at the cost of the transcript.
        var diarization = estimatedRtf < 0.45 && capability.LogicalCores >= 4;
        reasons.Append(diarization ? " 話者分離を有効化。" : " 余裕がないため話者分離は無効（マイク/PC音声の2分類にフォールバック）。");

        // The local LLM only runs after recording stops, so it does not compete
        // with capture - but it is pointless on a machine that cannot hold it.
        var minutes = capability.TotalRamGb >= 8.0;
        reasons.Append(minutes ? " ローカル議事録生成を有効化。" : " RAM不足のためローカル議事録生成は無効。");

        // The live pass is judged on how quickly a sentence appears, and a chunk
        // cannot be transcribed before it has been closed. Six to ten seconds of
        // audio plus inference put the first words on screen around ten seconds
        // after they were spoken, which reads as broken. The second pass is what
        // makes short windows affordable: accuracy is recovered afterwards, so
        // the live pass can be tuned for latency alone.
        var refinement = SelectRefinementModel(capability, chosen);
        var chunkSeconds = refinement is null
            ? (estimatedRtf > 0.4 ? 6.0 : 10.0)
            : 4.0;

        if (refinement is not null)
        {
            reasons.Append(CultureInfo.InvariantCulture,
                $" 停止後の高精度パスに「{refinement.DisplayName}」を使用（速報は遅延優先の設定）。");
        }
        else
        {
            reasons.Append(" 高精度パス用の上位モデルを保持できないため、速報のみです。");
        }

        return new PerformanceProfile
        {
            SttModelId = chosen.Id,
            SttThreads = threads,
            ChunkSeconds = chunkSeconds,
            ChunkOverlapMs = 400,

            // 450 ms is around the length of a natural pause between clauses. It
            // ends a chunk a quarter-second sooner than the old 700 ms without
            // cutting into the gap a speaker leaves mid-sentence.
            SilenceFlushMs = refinement is null ? 700 : 450,
            VadEnabled = true,
            BeamSize = 1,
            RefinementModelId = refinement?.Id,
            DiarizationEnabled = diarization,
            MinutesEnabled = minutes,
            LogicalCores = capability.LogicalCores,
            TotalRamGb = capability.TotalRamGb,
            CpuScore = capability.MultiThreadScore,
            Rationale = reasons.ToString(),
        };
    }

    /// <summary>
    /// Picks the model for the second pass: the largest one the machine can hold,
    /// and strictly better than the live model or there is no point running it.
    /// </summary>
    /// <remarks>
    /// Speed is deliberately not a criterion. This pass runs after recording has
    /// stopped, so it competes with nothing; what it costs is the user's patience
    /// once, and what it buys is a transcript that is worth reading.
    /// </remarks>
    public static ModelDescriptor? SelectRefinementModel(MachineCapability capability, ModelDescriptor liveModel)
    {
        var liveIndex = Array.IndexOf(LadderIds, liveModel.Id);

        for (var i = LadderIds.Length - 1; i > liveIndex; i--)
        {
            var candidate = ModelCatalog.RequireSpeechModel(LadderIds[i]);
            if (capability.TotalRamGb * 1024 >= candidate.RequiredRamMb * 3.0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the graceful-degradation ladder from the requirements when the
    /// measured RTF says the machine cannot keep up:
    /// speaker diarization off -> lighter STT model -> local LLM off.
    /// Recording itself is never touched.
    /// </summary>
    public static PerformanceProfile? Degrade(PerformanceProfile current, double measuredRtf)
    {
        if (measuredRtf < DegradeRealTimeFactor)
        {
            return null;
        }

        var next = current.Clone();
        next.MeasuredRealTimeFactor = measuredRtf;

        if (next.DiarizationEnabled)
        {
            next.DiarizationEnabled = false;
            next.Rationale = $"実測RTF {measuredRtf:F2} のため話者分離を停止しました（録音と文字起こしは継続）。";
            return next;
        }

        var index = Array.IndexOf(LadderIds, current.SttModelId);
        if (index > 0)
        {
            var lighter = ModelCatalog.RequireSpeechModel(LadderIds[index - 1]);
            next.SttModelId = lighter.Id;
            next.Rationale = $"実測RTF {measuredRtf:F2} のため音声認識モデルを「{lighter.DisplayName}」へ切り替えました。";
            return next;
        }

        if (next.MinutesEnabled)
        {
            next.MinutesEnabled = false;
            next.Rationale = $"実測RTF {measuredRtf:F2} のためローカル議事録生成を無効化しました。";
            return next;
        }

        return null;
    }

    /// <summary>Chooses the LLM for the minutes feature, or null when the machine is too small.</summary>
    public static ModelDescriptor? SelectTextGenerationModel(MachineCapability capability)
    {
        // Only Apache-2.0 licensed models are auto-selected; anything with a
        // restrictive licence stays an explicit, informed user choice.
        var candidate = ModelCatalog.TextGenerationModels
            .Where(m => m.License.StartsWith("Apache", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.RequiredRamMb)
            .FirstOrDefault(m => capability.TotalRamGb * 1024 >= m.RequiredRamMb * 3.0);

        return candidate;
    }
}
