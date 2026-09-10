namespace MeetingRecorder.Core.ModelManagement;

/// <summary>
/// The fixed list of models the application will ever download.
/// </summary>
/// <remarks>
/// <para>
/// The list is a hard-coded allow-list on purpose: there is no "enter a URL"
/// field anywhere in the product, so a malicious settings file cannot turn the
/// recorder into a downloader for arbitrary content.
/// </para>
/// <para><b>Pinned hashes.</b> Every SHA-256 below was measured by downloading
/// the file in CI (<c>.github/workflows/model-hashes.yml</c>), not copied from a
/// web page. The downloader rejects and deletes any file that does not match, so
/// if a publisher re-uploads a model the download fails loudly rather than
/// installing something unverified - re-run that workflow to refresh the pins.
/// Sizes are the exact byte counts from the same run.
/// </para>
/// <para><b>Why these models</b></para>
/// <list type="bullet">
/// <item><description>
/// <b>Whisper (ggml, MIT).</b> whisper.cpp runs well on a 15 W mobile CPU, the
/// quantized builds cut both file size and RAM roughly in half for a small
/// accuracy cost, and Japanese is one of the languages the model handles best.
/// Quantized variants (q5_1 / q5_0) are preferred over the f16 originals
/// because on an i5-1335U memory bandwidth, not arithmetic, is the limit.
/// </description></item>
/// <item><description>
/// <b>large-v3-turbo.</b> Added once transcription stopped having to keep up
/// with the meeting. It pairs large-v3's full 32-layer encoder with a 4-layer
/// decoder, so it is the only way to run a large-family encoder on this class
/// of machine in a time anybody will wait for. The q8_0 (874,188,075 B) and
/// f16 (1,624,555,275 B) builds were measured alongside q5_0 and are not
/// offered: at 16 GB they leave too little room beside Windows, a browser and
/// a conferencing client, which is the situation this application is used in.
/// </description></item>
/// <item><description>
/// <b>Qwen2.5 Instruct (Apache-2.0).</b> Picked for the experimental minutes
/// generator because it is genuinely Apache-2.0 - usable inside a company
/// without a bespoke licence review - has solid Japanese for its size, and ships
/// official GGUF builds. The 1.5B build is the default; 3B and larger models
/// under research-only licences were rejected despite better output.
/// </description></item>
/// </list>
/// </remarks>
public static class ModelCatalog
{
    public const string WhisperTiny = "whisper-tiny-q5_1";
    public const string WhisperBase = "whisper-base-q5_1";
    public const string WhisperSmall = "whisper-small-q5_1";
    public const string WhisperMedium = "whisper-medium-q5_0";
    public const string WhisperLargeV3Turbo = "whisper-large-v3-turbo-q5_0";

    public const string LlmQwen25_1_5B = "qwen2.5-1.5b-instruct-q4_k_m";
    public const string LlmQwen25_3B = "qwen2.5-3b-instruct-q4_k_m";

    private const string WhisperBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static IReadOnlyList<ModelDescriptor> SpeechModels { get; } = new[]
    {
        new ModelDescriptor
        {
            Id = WhisperTiny,
            DisplayName = "Whisper tiny (量子化 q5_1)",
            Purpose = ModelPurpose.SpeechToText,
            PurposeDescription = "日本語の文字起こし（最軽量・処理は最速・精度は低め）",
            Url = WhisperBaseUrl + "ggml-tiny-q5_1.bin",
            FileName = "ggml-tiny-q5_1.bin",
            ApproximateSizeBytes = 32_152_673,
            Sha256 = "818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7",
            Publisher = "ggerganov / whisper.cpp (Hugging Face)",
            License = "MIT",
            LicenseUrl = "https://github.com/ggerganov/whisper.cpp/blob/master/LICENSE",
            RequiredRamMb = 300,
            RelativeCost = 0.25,
            Notes = "非力なPC向けのフォールバック。短時間で結果が欲しい場合に。",
        },
        new ModelDescriptor
        {
            Id = WhisperBase,
            DisplayName = "Whisper base (量子化 q5_1)",
            Purpose = ModelPurpose.SpeechToText,
            PurposeDescription = "日本語の文字起こし（軽量・高速）",
            Url = WhisperBaseUrl + "ggml-base-q5_1.bin",
            FileName = "ggml-base-q5_1.bin",
            ApproximateSizeBytes = 59_707_625,
            Sha256 = "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898",
            Publisher = "ggerganov / whisper.cpp (Hugging Face)",
            License = "MIT",
            LicenseUrl = "https://github.com/ggerganov/whisper.cpp/blob/master/LICENSE",
            RequiredRamMb = 500,
            RelativeCost = 0.5,
            Notes = "処理時間を最優先する場合、または低速なPC向け。",
        },
        new ModelDescriptor
        {
            Id = WhisperSmall,
            DisplayName = "Whisper small (量子化 q5_1)",
            Purpose = ModelPurpose.SpeechToText,
            PurposeDescription = "日本語の文字起こし（精度と処理時間のバランス）",
            Url = WhisperBaseUrl + "ggml-small-q5_1.bin",
            FileName = "ggml-small-q5_1.bin",
            ApproximateSizeBytes = 190_085_487,
            Sha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb",
            Publisher = "ggerganov / whisper.cpp (Hugging Face)",
            License = "MIT",
            LicenseUrl = "https://github.com/ggerganov/whisper.cpp/blob/master/LICENSE",
            RequiredRamMb = 1024,
            RelativeCost = 1.0,
            Notes = "処理時間を短く保ちたい場合の既定候補。",
        },
        new ModelDescriptor
        {
            Id = WhisperMedium,
            DisplayName = "Whisper medium (量子化 q5_0)",
            Purpose = ModelPurpose.SpeechToText,
            PurposeDescription = "日本語の文字起こし（高精度・高負荷）",
            Url = WhisperBaseUrl + "ggml-medium-q5_0.bin",
            FileName = "ggml-medium-q5_0.bin",
            ApproximateSizeBytes = 539_212_467,
            Sha256 = "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f",
            Publisher = "ggerganov / whisper.cpp (Hugging Face)",
            License = "MIT",
            LicenseUrl = "https://github.com/ggerganov/whisper.cpp/blob/master/LICENSE",
            RequiredRamMb = 2200,
            RelativeCost = 3.6,
            Notes = "large-v3-turbo が利用できない場合の上位候補。",
        },
        new ModelDescriptor
        {
            Id = WhisperLargeV3Turbo,
            DisplayName = "Whisper large-v3-turbo (量子化 q5_0)",
            Purpose = ModelPurpose.SpeechToText,
            PurposeDescription = "日本語の文字起こし（最高精度・処理時間は長い）",
            Url = WhisperBaseUrl + "ggml-large-v3-turbo-q5_0.bin",
            FileName = "ggml-large-v3-turbo-q5_0.bin",
            ApproximateSizeBytes = 574_041_195,
            Sha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
            Publisher = "ggerganov / whisper.cpp (Hugging Face)",
            License = "MIT",
            LicenseUrl = "https://github.com/ggerganov/whisper.cpp/blob/master/LICENSE",
            RequiredRamMb = 2400,
            RelativeCost = 6.6,
            Notes = "large-v3 のエンコーダーをそのまま持ち、デコーダーは4層。録音後にまとめて処理する用途向け。",
        },
    };

    public static IReadOnlyList<ModelDescriptor> TextGenerationModels { get; } = new[]
    {
        new ModelDescriptor
        {
            Id = LlmQwen25_1_5B,
            DisplayName = "Qwen2.5 1.5B Instruct (GGUF Q4_K_M)",
            Purpose = ModelPurpose.TextGeneration,
            PurposeDescription = "録音停止後の議事録生成（実験的機能・完全ローカル）",
            Url = "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf",
            FileName = "qwen2.5-1.5b-instruct-q4_k_m.gguf",
            ApproximateSizeBytes = 1_117_320_736,
            Sha256 = "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e",
            Publisher = "Qwen (Alibaba Cloud) via Hugging Face",
            License = "Apache-2.0",
            LicenseUrl = "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/blob/main/LICENSE",
            RequiredRamMb = 2048,
            RelativeCost = 1.0,
            Notes = "Apache-2.0 のため社内商用利用の可否が明確。基準PCの既定。",
        },
        new ModelDescriptor
        {
            Id = LlmQwen25_3B,
            DisplayName = "Qwen2.5 3B Instruct (GGUF Q4_K_M)",
            Purpose = ModelPurpose.TextGeneration,
            PurposeDescription = "録音停止後の議事録生成（高品質・高負荷）",
            Url = "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf",
            FileName = "qwen2.5-3b-instruct-q4_k_m.gguf",
            ApproximateSizeBytes = 2_104_932_768,
            Sha256 = "626b4a6678b86442240e33df819e00132d3ba7dddfe1cdc4fbb18e0a9615c62d",
            Publisher = "Qwen (Alibaba Cloud) via Hugging Face",
            License = "Qwen Research License",
            LicenseUrl = "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct/blob/main/LICENSE",
            RequiredRamMb = 4096,
            RelativeCost = 2.0,
            Notes = "ライセンスが Apache-2.0 ではないため、自動選択されません。社内利用可否を確認のうえ手動で選択してください。",
        },
    };

    public static IEnumerable<ModelDescriptor> All => SpeechModels.Concat(TextGenerationModels);

    public static ModelDescriptor? Find(string? id)
        => id is null ? null : All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static ModelDescriptor RequireSpeechModel(string id)
        => SpeechModels.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? throw new KeyNotFoundException($"Unknown speech model id '{id}'.");
}
