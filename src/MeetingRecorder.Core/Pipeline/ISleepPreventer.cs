namespace MeetingRecorder.Core.Pipeline;

/// <summary>
/// Keeps Windows awake for the duration of a recording.
/// </summary>
/// <remarks>
/// Implemented on Windows with <c>SetThreadExecutionState</c>, which is a
/// per-process, per-thread request that needs no elevation and is released
/// automatically if the process dies - exactly the "no administrator rights,
/// restore the previous state afterwards" behaviour the requirements ask for.
/// </remarks>
public interface ISleepPreventer : IDisposable
{
    bool IsActive { get; }

    /// <summary>Requests that the system stays awake. Returns false if the request was refused.</summary>
    bool Prevent(string reason);

    /// <summary>Restores the normal power policy.</summary>
    void Restore();
}

/// <summary>No-op implementation used on non-Windows hosts and in tests.</summary>
public sealed class NullSleepPreventer : ISleepPreventer
{
    public bool IsActive { get; private set; }

    public bool Prevent(string reason)
    {
        IsActive = true;
        return true;
    }

    public void Restore() => IsActive = false;

    public void Dispose() => Restore();
}
