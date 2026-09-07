using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MeetingRecorder.Core.Diagnostics;
using MeetingRecorder.Core.Pipeline;

namespace MeetingRecorder.Audio;

/// <summary>
/// Keeps the machine awake while recording using
/// <c>SetThreadExecutionState</c>.
/// </summary>
/// <remarks>
/// <para>
/// This API needs no elevation, no group policy and no power-plan edit: it is a
/// per-thread request that Windows honours for as long as the requesting thread
/// lives, and that is released automatically if the process exits - so a crash
/// can never leave the user's laptop permanently unable to sleep.
/// </para>
/// <para>
/// Because the request is per-thread, it is made from a dedicated thread that
/// stays alive for the whole recording. Calling it from a thread-pool thread
/// would silently lose the request the moment that thread was recycled.
/// </para>
/// <para>
/// Only <c>ES_SYSTEM_REQUIRED</c> is requested, not <c>ES_DISPLAY_REQUIRED</c>:
/// the meeting must keep recording, but there is no reason to burn battery
/// keeping the screen on, and no reason to override the user's screen-lock
/// policy.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsSleepPreventer : ISleepPreventer
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(ExecutionState esFlags);

    private readonly ILogger _logger;
    private readonly object _sync = new();
    private Thread? _holder;
    private ManualResetEventSlim? _stop;
    private bool _disposed;

    public WindowsSleepPreventer(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsActive { get; private set; }

    public bool Prevent(string reason)
    {
        lock (_sync)
        {
            if (_disposed || IsActive)
            {
                return IsActive;
            }

            var started = new ManualResetEventSlim(false);
            var succeeded = false;
            _stop = new ManualResetEventSlim(false);

            _holder = new Thread(() =>
            {
                var previous = SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired);
                succeeded = previous != 0;
                started.Set();

                _stop.Wait();

                // Returning to ES_CONTINUOUS alone restores the normal power
                // policy; the thread then exits, which releases the request even
                // if this call somehow fails.
                SetThreadExecutionState(ExecutionState.Continuous);
            })
            {
                IsBackground = true,
                Name = "MeetingRecorder.SleepGuard",
            };

            _holder.Start();
            started.Wait(TimeSpan.FromSeconds(2));

            IsActive = succeeded;
            if (succeeded)
            {
                _logger.Info(nameof(WindowsSleepPreventer), $"Sleep suppressed: {reason}");
            }
            else
            {
                _logger.Warn(nameof(WindowsSleepPreventer), "SetThreadExecutionState was refused; the system may sleep during recording.");
                Restore();
            }

            return succeeded;
        }
    }

    public void Restore()
    {
        lock (_sync)
        {
            _stop?.Set();
            _holder?.Join(TimeSpan.FromSeconds(2));
            _stop?.Dispose();
            _stop = null;
            _holder = null;

            if (IsActive)
            {
                _logger.Info(nameof(WindowsSleepPreventer), "Sleep suppression released.");
            }

            IsActive = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Restore();
    }
}
