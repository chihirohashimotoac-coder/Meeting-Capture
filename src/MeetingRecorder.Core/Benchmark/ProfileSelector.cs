using System.Globalization;
using System.Text;
using MeetingRecorder.Core.ModelManagement;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Core.Benchmark;

/// <summary>
/// Turns a <see cref="MachineCapability"/> measurement into a concrete
/// <see cref="PerformanceProfile"/>.
/// </summary>
/// <remarks>
/// <para>
/// The question this used to answer was "what can keep up with a live
/// meeting?", and the answer was always a small model. Nothing has to keep up
/// any more: transcription runs on a finished file, when the user asks for it,
/// and the only cost of a bigger model is that they wait longer for a better
/// transcript. So the question is now "what is the most accurate model this
/// machine can hold and finish in a reasonable time?" - and the ladder is
/// climbed from the top rather than the bottom.
/// </para>
/// <para>
/// Whatever this picks, the user can override it in the settings window, and
/// the estimate is shown next to the choice so they can decide for themselves
/// what "reasonable" means for their meetings.
/// </para>
/// </remarks>
public static class ProfileSelector
{
    /// <summary>
    /// Longest transcription time, as a multiple of the recording's length,
    /// that is chosen automatically.
    /// </summary>
    /// <remarks>
    /// 1.5 means a one-hour meeting is expected to take about an hour and a
    /// half. That is a wait somebody will start and come back to, which is the
    /// behaviour this application is built around; beyond it, a default that
    /// occupies the machine for most of an afternoon stops being a sensible
    /// thing to choose on the user's behalf. It is a ceiling on the automatic
    /// choice only - the settings window offers every model in the catalog.
    /// </remarks>
    public const double AcceptableProcessingFactor = 1.5;

    /// <summary>
    /// Working memory a model needs, as a multiple of its own footprint, before
    /// it is chosen automatically.
    /// </summary>
    /// <remarks>
    /// 3x. The machine this targets is also running Windows, a browser and a
    /// conferencing client, and a transcription that triggers paging is far
    /// slower than the estimate here assumes.
    /// </remarks>
    public const double RequiredRamHeadroom = 3.0;

    /// <summary>
    /// Estimated processing factor for whisper small-q5_1 at CPU score 1.0 with
    /// the recommended thread count, decoding the way
    /// <see cref="Stt.SpeechRecognitionOptions.Offline"/> does.
    /// </summary>
    /// <remarks>
    /// Every other model scales from it through
    /// <see cref="ModelDescriptor.RelativeCost"/>. It is an estimate used to
    /// pick a starting point and to warn about a slow choice - it is not
    /// presented anywhere as a measurement of the user's machine.
    /// </remarks>
    private const double ReferenceFactorForSmall = 1.1;

    /// <summary>
    /// The catalog's speech models, least to most demanding. The selector walks
    /// it from the far end.
    /// </summary>
    private static readonly string[] LadderIds =
    {
        ModelCatalog.WhisperTiny,
        ModelCatalog.WhisperBase,
        ModelCatalog.WhisperSmall,
        ModelCatalog.WhisperMedium,
        ModelCatalog.WhisperLargeV3Turbo,
    };

    /// <summary>
    /// Estimated transcription time as a fraction of audio length: 1.0 means an
    /// hour of meeting takes an hour to transcribe.
    /// </summary>
    public static double EstimateProcessingFactor(ModelDescriptor model, MachineCapability capability)
    {
        var score = Math.Max(0.05, capability.MultiThreadScore);
        return ReferenceFactorForSmall * model.RelativeCost / score;
    }

    public static bool FitsInMemory(ModelDescriptor model, MachineCapability capability)
        => capability.TotalRamGb * 1024 >= model.RequiredRamMb * RequiredRamHeadroom;

    public static PerformanceProfile Select(MachineCapability capability)
    {
        var threads = CapabilityProbe.RecommendThreadCount(capability.LogicalCores);
        var reasons = new StringBuilder();
        reasons.Append(CultureInfo.InvariantCulture, $"CPU {capability.LogicalCores}コア / 実測スコア {capability.MultiThreadScore:F2} / RAM {capability.TotalRamGb:F1}GB。");

        // Start from the most accurate model and come down only as far as this
        // machine forces. The old selector did the opposite, because it was
        // protecting a live transcript; there is no live transcript to protect.
        var chosen = ModelCatalog.RequireSpeechModel(ModelCatalog.WhisperTiny);
        for (var i = LadderIds.Length - 1; i >= 0; i--)
        {
            var candidate = ModelCatalog.RequireSpeechModel(LadderIds[i]);
            if (!FitsInMemory(candidate, capability))
            {
                continue;
            }

            if (EstimateProcessingFactor(candidate, capability) > AcceptableProcessingFactor && i > 0)
            {
                continue;
            }

            chosen = candidate;
            break;
        }

        var estimated = EstimateProcessingFactor(chosen, capability);
        reasons.Append(CultureInfo.InvariantCulture,
            $" 推定処理時間（音声長比）{estimated:F1}倍により「{chosen.DisplayName}」を選択。");
        reasons.Append(" より高精度なモデルを設定画面で選ぶこともできます（処理時間は長くなります）。");

        // Speaker clustering runs after the recognizer, on the same audio, once
        // recording has finished - so it costs a little more waiting and nothing
        // else. It is enabled whenever the machine is not tiny.
        var diarization = capability.LogicalCores >= 4;
        reasons.Append(diarization
            ? " 話者分離を有効化（文字起こし時に実行）。"
            : " コア数が少ないため話者分離は無効（マイク/PC音声の2分類のみ）。");

        var minutes = capability.TotalRamGb >= 8.0;
        reasons.Append(minutes ? " ローカル議事録生成を有効化。" : " RAM不足のためローカル議事録生成は無効。");

        return new PerformanceProfile
        {
            SttModelId = chosen.Id,
            SttThreads = threads,
            DiarizationEnabled = diarization,
            MinutesEnabled = minutes,
            LogicalCores = capability.LogicalCores,
            TotalRamGb = capability.TotalRamGb,
            CpuScore = capability.MultiThreadScore,
            EstimatedProcessingFactor = estimated,
            Rationale = reasons.ToString(),
        };
    }

    /// <summary>Chooses the LLM for the minutes feature, or null when the machine is too small.</summary>
    public static ModelDescriptor? SelectTextGenerationModel(MachineCapability capability)
    {
        // Only Apache-2.0 licensed models are auto-selected; anything with a
        // restrictive licence stays an explicit, informed user choice.
        var candidate = ModelCatalog.TextGenerationModels
            .Where(m => m.License.StartsWith("Apache", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.RequiredRamMb)
            .FirstOrDefault(m => capability.TotalRamGb * 1024 >= m.RequiredRamMb * RequiredRamHeadroom);

        return candidate;
    }
}
