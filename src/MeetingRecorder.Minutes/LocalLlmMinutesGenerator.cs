using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Models;

namespace MeetingRecorder.Minutes;

/// <summary>
/// Experimental minutes generator that runs a small GGUF model locally through
/// llama.cpp (LLamaSharp).
/// </summary>
/// <remarks>
/// <para><b>Scope.</b> Minutes are the lowest priority feature in this product.
/// This generator only ever runs after recording has stopped and only when the
/// user presses the button, so it can never compete with capture. If the model
/// is missing, the native backend will not load, or inference throws, the
/// result falls back to <see cref="ExtractiveMinutesGenerator"/> and says so in
/// the document.</para>
///
/// <para><b>Map-reduce, not one giant prompt.</b> An hour of Japanese meeting
/// speech is far more text than a 1.5B model handles well in one pass on a CPU.
/// The transcript is summarised block by block (the "map" phase, where progress
/// is genuinely measurable), and only those summaries are fed into the four
/// section prompts.</para>
///
/// <para><b>The prompt forbids invention.</b> The system message states that
/// nothing outside the transcript may be written and that unknown owners and due
/// dates must be "要確認". The final document is then assembled by
/// <see cref="MinutesTemplate"/> rather than taken verbatim from the model, so
/// the required six sections exist even when the model rambles.</para>
/// </remarks>
public sealed class LocalLlmMinutesGenerator : IMinutesGenerator
{
    private const string SystemPrompt =
        "あなたは日本語の会議議事録作成アシスタントです。次の規則を厳守してください。\n" +
        "1. 与えられた文字起こしに書かれている内容だけを使うこと。書かれていない情報を推測・補完しては絶対にいけません。\n" +
        "2. 担当者名・期限・数値が文字起こしに明記されていない場合は必ず「要確認」と書くこと。\n" +
        "3. 人物名を推測してはいけません。文字起こしに現れる呼称のみを使うこと。\n" +
        "4. 出力は日本語の簡潔な箇条書きにすること。前置きや感想は書かないこと。\n" +
        "5. 該当する内容が無い場合は「なし」とだけ出力すること。";

    /// <summary>Characters of transcript per map-phase block.</summary>
    private const int BlockCharacters = 1500;

    /// <summary>
    /// Upper bound on blocks. 60 blocks is ~90 000 characters, far more than a
    /// two hour meeting produces; the cap exists so a pathological input cannot
    /// turn into an hour of CPU inference.
    /// </summary>
    private const int MaxBlocks = 60;

    private readonly string _modelPath;
    private readonly int _threads;
    private readonly uint _contextSize;
    private readonly ILogger _logger;
    private readonly ExtractiveMinutesGenerator _fallback = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LLamaWeights? _weights;
    private bool _disposed;

    public LocalLlmMinutesGenerator(string modelPath, string modelDisplayName, int threads, uint contextSize = 4096, ILogger? logger = null)
    {
        _modelPath = modelPath;
        _threads = Math.Max(1, threads);
        _contextSize = contextSize;
        _logger = logger ?? NullLogger.Instance;
        ModelDisplayName = modelDisplayName;
    }

    public string ModelDisplayName { get; }

    public string Name => $"ローカルLLM（{ModelDisplayName}・実験的機能）";

    public bool IsAvailable => File.Exists(_modelPath);

    public async Task<MinutesDocument> GenerateAsync(
        MinutesRequest request,
        IProgress<MinutesProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return await FallbackAsync(request, progress, "ローカルLLMのモデルファイルが見つからないため、抽出型で生成しました。", cancellationToken)
                .ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GenerateCoreAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(LocalLlmMinutesGenerator), "Local LLM generation failed; falling back to extraction.", ex);
            return await FallbackAsync(
                    request,
                    progress,
                    $"ローカルLLMでの生成に失敗したため、抽出型で生成しました（{ex.GetType().Name}: {ex.Message}）。",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MinutesDocument> GenerateCoreAsync(
        MinutesRequest request,
        IProgress<MinutesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var blocks = BuildBlocks(request.Segments, out var truncated);
        if (truncated)
        {
            warnings.Add($"文字起こしが長いため、先頭 {MaxBlocks} ブロック分のみを議事録生成の対象としました。");
        }

        if (blocks.Count == 0)
        {
            return await FallbackAsync(request, progress, "文字起こしが空のため、抽出型で生成しました。", cancellationToken)
                .ConfigureAwait(false);
        }

        var totalSteps = blocks.Count + 4;
        var step = 0;

        progress?.Report(new MinutesProgress("モデルを読み込み中", step, totalSteps));
        var executor = CreateExecutor();

        var summaries = new List<string>();
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new MinutesProgress($"本文を要約中 ({step + 1}/{blocks.Count})", step, totalSteps));

            var summary = await InferAsync(
                    executor,
                    "次の会議文字起こしの一部から、事実として述べられている要点だけを日本語の箇条書きで抽出してください。\n" +
                    "推測は禁止です。要点が無ければ「なし」と出力してください。\n\n---\n" + block + "\n---",
                    maxTokens: 320,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(summary) && !summary.Trim().Equals("なし", StringComparison.Ordinal))
            {
                summaries.Add(summary.Trim());
            }

            step++;
        }

        var digest = string.Join("\n", summaries);
        if (digest.Length == 0)
        {
            return await FallbackAsync(request, progress, "ローカルLLMが要点を抽出できなかったため、抽出型で生成しました。", cancellationToken)
                .ConfigureAwait(false);
        }

        var template = new MinutesTemplate();

        progress?.Report(new MinutesProgress("概要を作成中", step++, totalSteps));
        template.Overview = (await InferAsync(
                executor,
                "次は会議の要点メモです。会議全体の概要を日本語で3文以内にまとめてください。推測は禁止です。\n\n---\n" + digest + "\n---",
                maxTokens: 220,
                cancellationToken)
            .ConfigureAwait(false)).Trim();

        progress?.Report(new MinutesProgress("決定事項を抽出中", step++, totalSteps));
        AddLines(template.Decisions, await InferAsync(
            executor,
            "次の要点メモから、会議で決定された事項だけを箇条書き（「- 」始まり）で列挙してください。" +
            "決定されていない検討中の内容は含めないでください。該当が無ければ「なし」と出力してください。\n\n---\n" + digest + "\n---",
            maxTokens: 300,
            cancellationToken).ConfigureAwait(false));

        progress?.Report(new MinutesProgress("ToDoを抽出中", step++, totalSteps));
        var todoText = await InferAsync(
            executor,
            "次の要点メモから、今後実施すべきタスク（ToDo・ネクストアクション）を箇条書き（「- 」始まり）で列挙してください。" +
            "担当者や期限が明記されていない場合は書かないでください。該当が無ければ「なし」と出力してください。\n\n---\n" + digest + "\n---",
            maxTokens: 300,
            cancellationToken).ConfigureAwait(false);

        foreach (var line in SplitLines(todoText))
        {
            template.Actions.Add(new MinutesTemplate.ActionItem
            {
                Task = line,
                Owner = MinutesTemplate.Unknown,
                DueDate = MinutesTemplate.Unknown,
            });
        }

        progress?.Report(new MinutesProgress("継続検討事項を抽出中", step++, totalSteps));
        AddLines(template.OpenQuestions, await InferAsync(
            executor,
            "次の要点メモから、結論が出ずに継続検討となった事項を箇条書き（「- 」始まり）で列挙してください。" +
            "該当が無ければ「なし」と出力してください。\n\n---\n" + digest + "\n---",
            maxTokens: 300,
            cancellationToken).ConfigureAwait(false));

        // Topics are assembled deterministically from the map phase rather than
        // asked for again: the block boundaries are already time ordered, so
        // this keeps the section factual and cheap.
        for (var i = 0; i < summaries.Count; i++)
        {
            var topic = new MinutesTemplate.TopicSection
            {
                Title = $"要点 {i + 1}",
                TimeRange = blocks[i].TimeRange,
            };

            foreach (var line in SplitLines(summaries[i]))
            {
                topic.Points.Add(line);
            }

            if (topic.Points.Count > 0)
            {
                template.Topics.Add(topic);
            }
        }

        template.Notes.Add($"この議事録は完全にローカルで動作する小型言語モデル（{ModelDisplayName}）で生成した実験的な結果です。");
        template.Notes.Add("生成AIの出力には誤りが含まれることがあります。必ず transcript.md の原文で内容を確認してください。");
        template.Notes.AddRange(warnings);

        progress?.Report(new MinutesProgress("完了", totalSteps, totalSteps));

        return new MinutesDocument(
            template.ToMarkdown(request.Metadata, Name),
            template.ToPlainText(request.Metadata, Name),
            Name,
            UsedLocalLlm: true,
            warnings);
    }

    private static readonly object NativeConfigSync = new();
    private static bool _nativeConfigured;

    /// <summary>
    /// Points llama.cpp at the runtime layout the publish step preserves and
    /// turns on instruction-set fallback.
    /// </summary>
    /// <remarks>
    /// The distribution ships four CPU variants under
    /// <c>runtimes/win-x64/native/{noavx,avx,avx2,avx512}/</c> rather than one
    /// flattened copy, because an AVX-512 binary on a machine without AVX-512
    /// does not fail politely - it raises an illegal instruction. Auto-fallback
    /// walks down that list until one loads, so an older office PC still works.
    /// <para>
    /// The configuration must be applied before the native library loads and
    /// throws if applied twice, hence the one-shot guard. A failure here is not
    /// fatal: it leaves LLamaSharp on its defaults, and if that also fails the
    /// caller falls back to the extractive generator.
    /// </para>
    /// </remarks>
    private void ConfigureNativeLibraryOnce()
    {
        lock (NativeConfigSync)
        {
            if (_nativeConfigured)
            {
                return;
            }

            _nativeConfigured = true;

            try
            {
                NativeLibraryConfig.All
                    .WithSearchDirectory(AppContext.BaseDirectory)
                    .WithAutoFallback(true);
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    nameof(LocalLlmMinutesGenerator),
                    $"Could not configure the llama.cpp native search path; using defaults: {ex.Message}");
            }
        }
    }

    private StatelessExecutor CreateExecutor()
    {
        ConfigureNativeLibraryOnce();

        _weights ??= LLamaWeights.LoadFromFile(new ModelParams(_modelPath)
        {
            ContextSize = _contextSize,
            Threads = _threads,
            // CPU only. A GPU is never required by this product, and llama.cpp
            // falls back silently on machines without one, which would make the
            // behaviour depend on the machine.
            GpuLayerCount = 0,
        });

        return new StatelessExecutor(_weights, new ModelParams(_modelPath)
        {
            ContextSize = _contextSize,
            Threads = _threads,
            GpuLayerCount = 0,
        })
        {
            ApplyTemplate = true,
            SystemMessage = SystemPrompt,
        };
    }

    private static async Task<string> InferAsync(
        StatelessExecutor executor,
        string prompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var parameters = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = new[] { "---", "<|im_end|>", "<|endoftext|>" },
            SamplingPipeline = new DefaultSamplingPipeline
            {
                // Low temperature: the job is extraction, not creative writing.
                Temperature = 0.2f,
                TopP = 0.9f,
                RepeatPenalty = 1.1f,
                Seed = 1234,
            },
        };

        var builder = new StringBuilder();
        await foreach (var token in executor.InferAsync(prompt, parameters, cancellationToken).ConfigureAwait(false))
        {
            builder.Append(token);
        }

        return builder.ToString();
    }

    private static void AddLines(List<string> destination, string text)
    {
        foreach (var line in SplitLines(text))
        {
            destination.Add(line);
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '・', '　', ' ').Trim();
            if (line.Length < 2 || line == "なし" || line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            yield return line;
        }
    }

    private List<TranscriptBlock> BuildBlocks(IReadOnlyList<TranscriptSegment> segments, out bool truncated)
    {
        var blocks = new List<TranscriptBlock>();
        var builder = new StringBuilder();
        long blockStart = -1;
        long blockEnd = 0;

        foreach (var segment in segments.OrderBy(s => s.StartMs))
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            if (blockStart < 0)
            {
                blockStart = segment.StartMs;
            }

            blockEnd = segment.EndMs;
            builder.Append('[').Append(segment.TimestampLabel()).Append("] ")
                   .Append(segment.SourceLabel()).Append(": ")
                   .AppendLine(segment.Text.Trim());

            if (builder.Length >= BlockCharacters)
            {
                blocks.Add(new TranscriptBlock(builder.ToString(), Range(blockStart, blockEnd)));
                builder.Clear();
                blockStart = -1;
            }
        }

        if (builder.Length > 0 && blockStart >= 0)
        {
            blocks.Add(new TranscriptBlock(builder.ToString(), Range(blockStart, blockEnd)));
        }

        truncated = blocks.Count > MaxBlocks;
        return truncated ? blocks.Take(MaxBlocks).ToList() : blocks;

        static string Range(long start, long end)
            => $"{TranscriptSegment.FormatTimestamp(start)} 〜 {TranscriptSegment.FormatTimestamp(end)}";
    }

    private async Task<MinutesDocument> FallbackAsync(
        MinutesRequest request,
        IProgress<MinutesProgress>? progress,
        string reason,
        CancellationToken cancellationToken)
    {
        _logger.Warn(nameof(LocalLlmMinutesGenerator), reason);
        var document = await _fallback.GenerateAsync(request, progress, cancellationToken).ConfigureAwait(false);
        var warnings = document.Warnings.Append(reason).ToList();

        return document with
        {
            GeneratorName = _fallback.Name,
            UsedLocalLlm = false,
            Warnings = warnings,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _weights?.Dispose();
        _weights = null;
        _fallback.Dispose();
        _gate.Dispose();
    }

    private sealed record TranscriptBlock(string Text, string TimeRange)
    {
        public static implicit operator string(TranscriptBlock block) => block.Text;
    }
}
