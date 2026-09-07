using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Dsp;
using MeetingRecorder.Core.Models;
using MeetingRecorder.Core.Stt;

namespace MeetingRecorder.Diarization;

/// <summary>
/// Lightweight, fully local speaker diarization: MFCC statistics plus online
/// clustering, one cluster space per capture stream.
/// </summary>
/// <remarks>
/// <para><b>What this is.</b> Each recognized utterance is turned into a
/// 24-dimensional vector (mean and standard deviation of MFCC 1-12 over its
/// voiced frames) and matched against the clusters seen so far by cosine
/// similarity. A new cluster is created when nothing is close enough. Cluster
/// ids are anonymous ("Speaker 1", "Speaker 2") and only a human can attach a
/// real name to one.</para>
///
/// <para><b>What this is not.</b> It is not a neural speaker-embedding system.
/// It cannot separate overlapping speech, it will merge two people with similar
/// voices, and it will split one person who moves closer to the microphone. The
/// requirements list diarization as optional and explicitly allow falling back
/// to the microphone / system-audio split; that guaranteed split is always
/// present regardless of what this class decides, and the accuracy limits are
/// documented in README.md rather than hidden.</para>
///
/// <para>The trade-off is deliberate: a real embedding model would mean another
/// few hundred megabytes of download, another licence to clear, and CPU time
/// competing with the transcription that users actually asked for.</para>
/// </remarks>
public sealed class OnlineSpeakerDiarizer : ISpeakerDiarizer
{
    /// <summary>
    /// Cosine similarity above which an utterance joins an existing cluster.
    /// Tuned to be conservative: splitting one person into two clusters is a
    /// nuisance the user fixes with one rename, while merging two people
    /// produces a transcript that attributes words to the wrong person.
    /// </summary>
    public const double DefaultSimilarityThreshold = 0.82;

    /// <summary>Utterances shorter than this are not attributed at all.</summary>
    private const double MinimumSeconds = 0.6;

    private readonly object _sync = new();
    private readonly Mfcc _mfcc = new();
    private readonly Dictionary<AudioSourceKind, List<Cluster>> _clusters = new();
    private readonly double _threshold;
    private readonly int _maxClustersPerSource;
    private readonly ILogger _logger;

    public OnlineSpeakerDiarizer(
        double similarityThreshold = DefaultSimilarityThreshold,
        int maxClustersPerSource = 8,
        ILogger? logger = null)
    {
        _threshold = similarityThreshold;
        _maxClustersPerSource = maxClustersPerSource;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsEnabled { get; set; } = true;

    public int? Identify(AudioSourceKind source, ReadOnlySpan<float> samples16k)
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (samples16k.Length < SpeechConstants.SampleRate * MinimumSeconds)
        {
            return null;
        }

        // Very quiet audio produces unstable cepstra; refusing to guess is better
        // than producing a random cluster.
        if (AudioMath.RmsDb(samples16k) < -55.0)
        {
            return null;
        }

        var embedding = ComputeEmbedding(samples16k);
        if (embedding is null)
        {
            return null;
        }

        lock (_sync)
        {
            if (!_clusters.TryGetValue(source, out var list))
            {
                list = new List<Cluster>();
                _clusters[source] = list;
            }

            var bestIndex = -1;
            var bestSimilarity = double.MinValue;

            for (var i = 0; i < list.Count; i++)
            {
                var similarity = CosineSimilarity(list[i].Centroid, embedding);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0 && bestSimilarity >= _threshold)
            {
                list[bestIndex].Absorb(embedding);
                return list[bestIndex].Id;
            }

            if (list.Count >= _maxClustersPerSource)
            {
                // Out of cluster budget: attach to the closest one rather than
                // inventing an endless list of speakers.
                if (bestIndex >= 0)
                {
                    list[bestIndex].Absorb(embedding);
                    return list[bestIndex].Id;
                }

                return null;
            }

            var cluster = new Cluster(list.Count, embedding);
            list.Add(cluster);
            _logger.Debug(nameof(OnlineSpeakerDiarizer), $"New speaker cluster {cluster.Id} on {source} (best similarity {bestSimilarity:F2}).");
            return cluster.Id;
        }
    }

    /// <summary>
    /// Mean and standard deviation of MFCC coefficients 1..12, L2 normalised.
    /// Coefficient 0 is dropped because it is overall loudness, which says
    /// nothing about who is speaking and everything about how far away they are.
    /// </summary>
    internal double[]? ComputeEmbedding(ReadOnlySpan<float> samples16k)
    {
        var frames = _mfcc.Compute(samples16k);
        if (frames.Count < 5)
        {
            return null;
        }

        var dimensions = _mfcc.CoefficientCount - 1;
        var mean = new double[dimensions];
        var variance = new double[dimensions];

        foreach (var frame in frames)
        {
            for (var i = 0; i < dimensions; i++)
            {
                mean[i] += frame[i + 1];
            }
        }

        for (var i = 0; i < dimensions; i++)
        {
            mean[i] /= frames.Count;
        }

        foreach (var frame in frames)
        {
            for (var i = 0; i < dimensions; i++)
            {
                var delta = frame[i + 1] - mean[i];
                variance[i] += delta * delta;
            }
        }

        var embedding = new double[dimensions * 2];
        for (var i = 0; i < dimensions; i++)
        {
            embedding[i] = mean[i];
            embedding[dimensions + i] = Math.Sqrt(variance[i] / frames.Count);
        }

        Normalize(embedding);
        return embedding;
    }

    internal static void Normalize(double[] vector)
    {
        double norm = 0;
        foreach (var value in vector)
        {
            norm += value * value;
        }

        norm = Math.Sqrt(norm);
        if (norm < 1e-9)
        {
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
    }

    internal static double CosineSimilarity(double[] a, double[] b)
    {
        double dot = 0;
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }

    public void Reset()
    {
        lock (_sync)
        {
            _clusters.Clear();
        }
    }

    public void Dispose() => Reset();

    private sealed class Cluster
    {
        private int _count;

        public Cluster(int id, double[] centroid)
        {
            Id = id;
            Centroid = (double[])centroid.Clone();
            _count = 1;
        }

        public int Id { get; }

        public double[] Centroid { get; }

        /// <summary>Running average, re-normalised so cosine similarity stays meaningful.</summary>
        public void Absorb(double[] embedding)
        {
            _count++;
            var weight = 1.0 / _count;
            for (var i = 0; i < Centroid.Length && i < embedding.Length; i++)
            {
                Centroid[i] = (Centroid[i] * (1 - weight)) + (embedding[i] * weight);
            }

            Normalize(Centroid);
        }
    }
}
