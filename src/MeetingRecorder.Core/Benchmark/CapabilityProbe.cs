using System.Diagnostics;

namespace MeetingRecorder.Core.Benchmark;

/// <param name="LogicalCores">Logical processors visible to the process.</param>
/// <param name="TotalRamGb">Physical memory available to the runtime.</param>
/// <param name="SingleThreadScore">Throughput of one core, normalised (1.0 ~ i5-1335U P-core).</param>
/// <param name="MultiThreadScore">Aggregate throughput across the threads STT would use.</param>
/// <param name="ElapsedMs">How long the probe itself took.</param>
public readonly record struct MachineCapability(
    int LogicalCores,
    double TotalRamGb,
    double SingleThreadScore,
    double MultiThreadScore,
    double ElapsedMs);

/// <summary>
/// Measures the machine instead of pattern-matching its CPU name.
/// </summary>
/// <remarks>
/// <para>
/// The requirements are explicit that the model must not be chosen from a CPU
/// model string. The same part number behaves very differently depending on the
/// power profile the laptop is in, whether it is docked, how warm it already is,
/// and what else the user is running - a machine on battery saver can be less
/// than half as fast as the same machine on AC.
/// </para>
/// <para>
/// So this runs a small, deterministic, cache-resident floating point workload
/// that resembles what a transformer actually does (dense matrix multiply), and
/// scores the machine from wall-clock throughput. The score is normalised so
/// that ~1.0 is a Core i5-1335U performance core; that constant only anchors the
/// scale, every decision downstream uses the measured value.
/// </para>
/// </remarks>
public static class CapabilityProbe
{
    /// <summary>
    /// GFLOP/s of one i5-1335U P-core on this workload, used purely to normalise
    /// the score to a readable 1.0. Re-measure and adjust if the workload changes.
    /// </summary>
    private const double ReferenceGflops = 6.0;

    private const int MatrixSize = 96;

    public static MachineCapability Measure(int? sttThreads = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var cores = Environment.ProcessorCount;
        var ramGb = GetTotalRamGb();

        var single = MeasureThroughput(1, cancellationToken);
        var threads = sttThreads ?? RecommendThreadCount(cores);
        var multi = threads <= 1 ? single : MeasureThroughput(threads, cancellationToken);

        stopwatch.Stop();
        return new MachineCapability(cores, ramGb, single, multi, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// whisper.cpp stops scaling well past a handful of threads on hybrid mobile
    /// CPUs (the efficiency cores finish late and the whole GEMM waits), and
    /// leaving head-room keeps the UI and the audio pump responsive.
    /// </summary>
    public static int RecommendThreadCount(int logicalCores)
        => Math.Clamp(logicalCores / 2, 2, 6);

    public static double GetTotalRamGb()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0)
            {
                return info.TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;
            }
        }
        catch (Exception)
        {
            // Fall through to the conservative default.
        }

        return 8.0;
    }

    private static double MeasureThroughput(int threads, CancellationToken cancellationToken)
    {
        // Two passes: the first warms caches and lets the CPU ramp its clock, the
        // second is the one that counts. Without the warm-up a laptop that was
        // idle reports roughly half its sustained speed.
        RunWorkload(threads, iterations: 2, cancellationToken);

        const int iterations = 6;
        var stopwatch = Stopwatch.StartNew();
        RunWorkload(threads, iterations, cancellationToken);
        stopwatch.Stop();

        var flopsPerIteration = 2.0 * MatrixSize * MatrixSize * MatrixSize;
        var totalFlops = flopsPerIteration * iterations * threads;
        var gflops = totalFlops / Math.Max(0.0005, stopwatch.Elapsed.TotalSeconds) / 1e9;
        return gflops / ReferenceGflops;
    }

    private static void RunWorkload(int threads, int iterations, CancellationToken cancellationToken)
    {
        if (threads <= 1)
        {
            Multiply(iterations, cancellationToken);
            return;
        }

        var workers = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            workers[i] = new Thread(() => Multiply(iterations, cancellationToken))
            {
                IsBackground = true,
                Name = $"MeetingRecorder.Probe{i}",
            };
            workers[i].Start();
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }
    }

    private static void Multiply(int iterations, CancellationToken cancellationToken)
    {
        var n = MatrixSize;
        var a = new float[n * n];
        var b = new float[n * n];
        var c = new float[n * n];

        for (var i = 0; i < a.Length; i++)
        {
            // Deterministic, non-trivial values: constant inputs would let the
            // JIT and the CPU short-circuit the work.
            a[i] = (i % 17) * 0.031f;
            b[i] = (i % 13) * 0.047f;
        }

        for (var it = 0; it < iterations; it++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < n; i++)
            {
                for (var k = 0; k < n; k++)
                {
                    var aik = a[(i * n) + k];
                    var rowB = k * n;
                    var rowC = i * n;
                    for (var j = 0; j < n; j++)
                    {
                        c[rowC + j] += aik * b[rowB + j];
                    }
                }
            }

            // Keep the result live so nothing is optimised away.
            if (c[0] > float.MaxValue / 2)
            {
                Array.Clear(c);
            }
        }
    }
}
