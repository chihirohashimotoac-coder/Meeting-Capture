using System.Diagnostics;
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
/// <para><b>One inference when one will do.</b> This used to summarise the
/// transcript in 1500-character blocks and then run four more prompts over the
/// digest - N + 4 inferences for every meeting, however short. For a ten minute
/// meeting that is several decodes of a few hundred tokens each to condense
/// something that fits in the context window whole, and on a CPU it is the
/// decoding that costs. So the transcript is now measured with the model's own
/// tokenizer (<see cref="MinutesPlanner"/>) and, when it fits, one prompt
/// produces all six sections. When it does not fit, the map phase is still
/// there - and the four section prompts have become a single structured reduce,
/// so the count is N + 1 rather than N + 4.</para>
///
/// <para><b>Nothing about the output changed.</b> The same six sections in the
/// same order, assembled by <see cref="MinutesTemplate"/> rather than taken
/// verbatim from the model, so they exist even when the model rambles. The
/// system message still forbids inventing anything the transcript does not
/// state, and unknown owners and due dates are still 要確認 rather than a
/// plausible guess. <see cref="MinutesResponseParser"/> is written so that a
/// malformed response costs at most one section, never the document.</para>
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

    /// <summary>
    /// Tokens reserved for the whole six-section document.
    /// </summary>
    /// <remarks>
    /// 1000 tokens is roughly a thousand Japanese characters, which is a full
    /// page of minutes - more than a ten minute meeting needs and enough that
    /// a two hour one is not cut off mid-section. It is a ceiling, not a target:
    /// the model stops when it has finished, and a short meeting decodes a short
    /// document.
    /// </remarks>
    private const int StructuredOutputTokens = 1000;

    /// <summary>Tokens reserved for one map-phase block summary.</summary>
    private const int BlockSummaryTokens = 320;

    /// <summary>
    /// Tokens allowed for the instructions wrapped around the transcript.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed: the constructor tokenizes the system message and
    /// the longest prompt template with the model's own vocabulary. This is only
    /// the value used before the model is loaded, and it is deliberately
    /// generous.
    /// </remarks>
    private const int AssumedPromptTokens = 400;

    /// <summary>
    /// Upper bound on blocks. 60 blocks is far more than a two hour meeting
    /// produces once blocks are sized to the context window; the cap exists so a
    /// pathological input cannot turn into an hour of CPU inference.
    /// </summary>
    private const int MaxBlocks = 60;

    private readonly string _modelPath;
    private readonly int _threads;
    private readonly uint _contextSize;
    private readonly ILogger _logger;
    private readonly ExtractiveMinutesGenerator _fallback = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MinutesResponseParser _parser = new();

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

    /// <summary>Where the last run spent its time, or null before the first run.</summary>
    public MinutesMetrics? Metrics { get; private set; }

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
        var metrics = new MinutesMetrics { ContextTokens = (int)_contextSize };
        var wallClock = Stopwatch.StartNew();
        var warnings = new List<string>();

        var lines = RenderTranscriptLines(request.Segments);
        if (lines.Count == 0)
        {
            return await FallbackAsync(request, progress, "文字起こしが空のため、抽出型で生成しました。", cancellationToken)
                .ConfigureAwait(false);
        }

        // Loading the weights is what makes a real tokenizer available, so the
        // plan cannot be made before it. It is also the single largest fixed
        // cost of the whole feature, which is why it is timed separately.
        progress?.Report(new MinutesProgress("モデルを読み込み中", 0, 1));
        var load = Stopwatch.StartNew();
        var executor = CreateExecutor();
        load.Stop();
        metrics.ModelLoad = load.Elapsed;

        var counter = CreateTokenCounter();
        var promptTokens = MeasurePromptOverhead(counter);

        var plan = MinutesPlanner.Plan(
            lines,
            counter,
            promptTokens,
            StructuredOutputTokens,
            (int)_contextSize,
            MaxBlocks);

        metrics.TranscriptTokens = counter.Count(string.Concat(lines));
        metrics.SinglePass = plan.SinglePass;
        metrics.Blocks = plan.Blocks.Count;
        metrics.Strategy = plan.Reason;

        if (plan.Blocks.Count == 0)
        {
            return await FallbackAsync(request, progress, "文字起こしが空のため、抽出型で生成しました。", cancellationToken)
                .ConfigureAwait(false);
        }

        var template = new MinutesTemplate();
        MinutesResponseParser.Result parsed;

        if (plan.SinglePass)
        {
            parsed = await SinglePassAsync(executor, plan.Blocks[0], counter, metrics, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            warnings.Add(
                $"文字起こしがモデルのコンテキスト長（{_contextSize} トークン）に収まらないため、"
                + $"{plan.Blocks.Count} ブロックに分割して要約しました。");

            var summaries = await MapAsync(executor, plan.Blocks, counter, metrics, progress, cancellationToken)
                .ConfigureAwait(false);

            if (summaries.Count == 0)
            {
                return await FallbackAsync(request, progress, "ローカルLLMが要点を抽出できなかったため、抽出型で生成しました。", cancellationToken)
                    .ConfigureAwait(false);
            }

            if (plan.Blocks.Count >= MaxBlocks)
            {
                warnings.Add($"文字起こしが長いため、先頭 {MaxBlocks} ブロック分のみを議事録生成の対象としました。");
            }

            parsed = await ReduceAsync(executor, summaries, counter, metrics, progress, cancellationToken)
                .ConfigureAwait(false);

            AddMapTopics(template, plan.Blocks, summaries);
        }

        if (parsed.FellBackToRawText)
        {
            warnings.Add(
                "モデルの出力が想定した見出し形式ではなかったため、生成された文章をそのまま「概要」に記載しています。"
                + "内容は transcript.md の原文で確認してください。");
        }

        Fill(template, parsed);

        template.Notes.Add($"この議事録は完全にローカルで動作する小型言語モデル（{ModelDisplayName}）で生成した実験的な結果です。");
        template.Notes.Add("生成AIの出力には誤りが含まれることがあります。必ず transcript.md の原文で内容を確認してください。");
        template.Notes.AddRange(warnings);

        wallClock.Stop();
        metrics.Total = wallClock.Elapsed;
        Metrics = metrics;
        _logger.Info(nameof(LocalLlmMinutesGenerator), Environment.NewLine + metrics.ToLogBlock());

        progress?.Report(new MinutesProgress("完了", 1, 1));

        return new MinutesDocument(
            template.ToMarkdown(request.Metadata, Name),
            template.ToPlainText(request.Metadata, Name),
            Name,
            UsedLocalLlm: true,
            warnings);
    }

    /// <summary>The whole transcript, all six sections, one decode.</summary>
    private async Task<MinutesResponseParser.Result> SinglePassAsync(
        StatelessExecutor executor,
        string transcript,
        ITokenCounter counter,
        MinutesMetrics metrics,
        IProgress<MinutesProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new MinutesProgress("議事録を作成中", 0, 1));

        var prompt = BuildStructuredPrompt(
            "次は会議の文字起こし全文です。この内容だけを使って議事録を作成してください。",
            transcript);

        var clock = Stopwatch.StartNew();
        var response = await InferAsync(executor, prompt, StructuredOutputTokens, cancellationToken).ConfigureAwait(false);
        clock.Stop();

        metrics.ReduceInferences = 1;
        metrics.ReduceTime = clock.Elapsed;
        metrics.PromptTokens += counter.Count(prompt);
        metrics.OutputTokens += counter.Count(response);

        return _parser.Parse(response);
    }

    /// <summary>One compact digest per block. The phase that scales with meeting length.</summary>
    private async Task<List<string>> MapAsync(
        StatelessExecutor executor,
        IReadOnlyList<string> blocks,
        ITokenCounter counter,
        MinutesMetrics metrics,
        IProgress<MinutesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var summaries = new List<string>(blocks.Count);
        var clock = new Stopwatch();

        for (var i = 0; i < blocks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new MinutesProgress($"本文を要約中 ({i + 1}/{blocks.Count})", i, blocks.Count + 1));

            var prompt =
                "次の会議文字起こしの一部から、事実として述べられている要点だけを日本語の箇条書きで抽出してください。\n" +
                "推測は禁止です。要点が無ければ「なし」と出力してください。\n\n---\n" + blocks[i] + "\n---";

            clock.Restart();
            var summary = await InferAsync(executor, prompt, BlockSummaryTokens, cancellationToken).ConfigureAwait(false);
            clock.Stop();

            metrics.MapInferences++;
            metrics.MapTime += clock.Elapsed;
            metrics.PromptTokens += counter.Count(prompt);
            metrics.OutputTokens += counter.Count(summary);

            if (!string.IsNullOrWhiteSpace(summary) && !summary.Trim().Equals("なし", StringComparison.Ordinal))
            {
                summaries.Add(summary.Trim());
            }
        }

        return summaries;
    }

    /// <summary>
    /// The four section prompts, collapsed into one.
    /// </summary>
    /// <remarks>
    /// Asking four times over the same digest was four prefills and four
    /// decodes of a few hundred tokens each. Asking once, for a marked-up
    /// document, is one prefill and roughly the same total decoding - and the
    /// model can see all six sections at once, so something it lists as a
    /// decision is less likely to appear again under open questions.
    /// </remarks>
    private async Task<MinutesResponseParser.Result> ReduceAsync(
        StatelessExecutor executor,
        IReadOnlyList<string> summaries,
        ITokenCounter counter,
        MinutesMetrics metrics,
        IProgress<MinutesProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new MinutesProgress("議事録をまとめ中", metrics.MapInferences, metrics.MapInferences + 1));

        var digest = string.Join("\n", summaries);
        var prompt = BuildStructuredPrompt(
            "次は会議の要点メモです。このメモに書かれている内容だけを使って議事録を作成してください。",
            digest);

        var clock = Stopwatch.StartNew();
        var response = await InferAsync(executor, prompt, StructuredOutputTokens, cancellationToken).ConfigureAwait(false);
        clock.Stop();

        metrics.ReduceInferences = 1;
        metrics.ReduceTime = clock.Elapsed;
        metrics.PromptTokens += counter.Count(prompt);
        metrics.OutputTokens += counter.Count(response);

        return _parser.Parse(response);
    }

    private static string BuildStructuredPrompt(string instruction, string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine(instruction);
        sb.AppendLine();
        sb.Append(MinutesResponseParser.FormatInstructions());
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine(body);
        sb.Append("---");
        return sb.ToString();
    }

    /// <summary>Moves the parsed sections onto the template.</summary>
    /// <remarks>
    /// Actions get <see cref="MinutesTemplate.Unknown"/> for owner and due date
    /// unconditionally, exactly as before. The model is told not to invent them
    /// and is not asked to supply them here either, because a field that is
    /// sometimes parsed out of free text is a field that is sometimes wrong -
    /// and a wrong owner on a task in a meeting record is worse than an honest
    /// 要確認.
    /// </remarks>
    private static void Fill(MinutesTemplate template, MinutesResponseParser.Result parsed)
    {
        template.Overview = parsed.Text(MinutesSection.Overview).Trim();

        template.Decisions.AddRange(Meaningful(parsed.Lines(MinutesSection.Decisions)));
        template.OpenQuestions.AddRange(Meaningful(parsed.Lines(MinutesSection.OpenQuestions)));

        foreach (var line in Meaningful(parsed.Lines(MinutesSection.Actions)))
        {
            template.Actions.Add(new MinutesTemplate.ActionItem
            {
                Task = line,
                Owner = MinutesTemplate.Unknown,
                DueDate = MinutesTemplate.Unknown,
            });
        }

        // Topics only when the map phase did not already fill them. On the
        // map-reduce path the per-block summaries are the better source: they
        // are time ordered, they carry the timestamps of the audio they came
        // from, and the digest the reduce was given is made of exactly the same
        // material - so what is skipped here is a restatement, not content.
        var topicPoints = Meaningful(parsed.Lines(MinutesSection.Topics)).ToList();
        if (topicPoints.Count > 0 && template.Topics.Count == 0)
        {
            var topic = new MinutesTemplate.TopicSection { Title = "議題別内容" };
            topic.Points.AddRange(topicPoints);
            template.Topics.Add(topic);
        }

        template.Notes.AddRange(Meaningful(parsed.Lines(MinutesSection.Notes)));
    }

    /// <summary>
    /// Topics from the map phase: the block boundaries are already in time
    /// order, so this stays factual and costs no inference.
    /// </summary>
    /// <remarks>
    /// The time range comes back out of the block's own text. Every transcript
    /// line was rendered with its timestamp in front of it, so the first and
    /// last of them are the range - read from the material rather than tracked
    /// alongside it, which is one fewer thing that can fall out of step.
    /// </remarks>
    private static void AddMapTopics(
        MinutesTemplate template,
        IReadOnlyList<string> blocks,
        IReadOnlyList<string> summaries)
    {
        for (var i = 0; i < summaries.Count; i++)
        {
            var topic = new MinutesTemplate.TopicSection
            {
                Title = $"要点 {i + 1}",
                TimeRange = i < blocks.Count ? TimeRangeOf(blocks[i]) : string.Empty,
            };

            topic.Points.AddRange(Meaningful(summaries[i].Split('\n')));

            if (topic.Points.Count > 0)
            {
                template.Topics.Add(topic);
            }
        }
    }

    /// <summary>"first 〜 last" from the timestamps a block's lines carry, or empty.</summary>
    private static string TimeRangeOf(string block)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(block, @"\[(\d+:\d{2}:\d{2})\]");
        if (matches.Count == 0)
        {
            return string.Empty;
        }

        var first = matches[0].Groups[1].Value;
        var last = matches[^1].Groups[1].Value;
        return first == last ? first : $"{first} 〜 {last}";
    }

    private static IEnumerable<string> Meaningful(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('-', '*', '・', '　', ' ').Trim();
            if (line.Length < 2 || line == "なし" || line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            yield return line;
        }
    }

    /// <summary>
    /// One line per segment, timestamped and attributed, ready to be split
    /// between blocks without ever cutting a sentence.
    /// </summary>
    private static List<string> RenderTranscriptLines(IReadOnlyList<TranscriptSegment> segments)
    {
        var lines = new List<string>();

        foreach (var segment in segments.OrderBy(s => s.StartMs))
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            lines.Add(
                $"[{segment.TimestampLabel()}] {segment.SourceLabel()}: {segment.Text.Trim()}"
                + Environment.NewLine);
        }

        return lines;
    }

    private ITokenCounter CreateTokenCounter()
        => _weights is null ? new HeuristicTokenCounter() : new LlamaTokenCounter(_weights);

    /// <summary>
    /// Measures the instructions that wrap every prompt, rather than assuming
    /// them.
    /// </summary>
    private int MeasurePromptOverhead(ITokenCounter counter)
    {
        var skeleton = SystemPrompt + BuildStructuredPrompt(
            "次は会議の文字起こし全文です。この内容だけを使って議事録を作成してください。",
            string.Empty);

        var measured = counter.Count(skeleton);

        // The chat template llama.cpp applies adds role markers this code never
        // sees. A flat allowance on top is cheaper than being wrong about them.
        return Math.Max(AssumedPromptTokens, measured + 128);
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
            AntiPrompts = new[] { "<|im_end|>", "<|endoftext|>" },
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

    /// <summary>
    /// The loaded model's own tokenizer. The only counter whose answer is a
    /// fact rather than an estimate.
    /// </summary>
    private sealed class LlamaTokenCounter : ITokenCounter
    {
        private readonly LLamaWeights _weights;

        public LlamaTokenCounter(LLamaWeights weights)
        {
            _weights = weights;
        }

        public int Count(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            try
            {
                return _weights.Tokenize(text, false, false, Encoding.UTF8).Length;
            }
            catch (Exception)
            {
                // A tokenizer that throws is not a reason to abandon a meeting's
                // minutes. Over-estimating sends the planner down the map-reduce
                // path, which always works.
                return text.Length;
            }
        }
    }
}
