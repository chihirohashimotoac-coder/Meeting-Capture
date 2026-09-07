using System.Diagnostics;

namespace MeetingRecorder.Core.Diagnostics;

/// <param name="CpuPercent">Process CPU usage across all cores, 0-100.</param>
/// <param name="WorkingSetMb">Resident memory of the process.</param>
/// <param name="ManagedMb">Managed heap size.</param>
public readonly record struct ResourceSnapshot(double CpuPercent, double WorkingSetMb, double ManagedMb);

/// <summary>
/// Samples this process's own CPU and memory usage for the diagnostics panel.
/// </summary>
/// <remarks>
/// Deliberately local only: the numbers are shown in the window and written to
/// the local log, never uploaded anywhere.
/// </remarks>
public sealed class PerformanceMonitor
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly object _sync = new();
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleUtc = DateTime.UtcNow;

    public ResourceSnapshot Sample()
    {
        lock (_sync)
        {
            _process.Refresh();
            var now = DateTime.UtcNow;
            var cpuTime = _process.TotalProcessorTime;

            var elapsed = (now - _lastSampleUtc).TotalSeconds;
            var cpuDelta = (cpuTime - _lastCpuTime).TotalSeconds;
            _lastSampleUtc = now;
            _lastCpuTime = cpuTime;

            var percent = elapsed > 0.05
                ? Math.Clamp(cpuDelta / elapsed / Environment.ProcessorCount * 100.0, 0, 100)
                : 0;

            return new ResourceSnapshot(
                percent,
                _process.WorkingSet64 / 1024.0 / 1024.0,
                GC.GetTotalMemory(false) / 1024.0 / 1024.0);
        }
    }
}
