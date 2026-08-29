using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A single acquired Capture Run OS lock handle. A handle returned with
    /// <c>TryAcquire == true</c> transfers ownership to the caller, and
    /// disposing the handle releases the lock.
    /// </summary>
    /// <remarks>
    /// The acquired OS handle is the source of truth for ownership, never the
    /// existence of a lock file. Disposal is safe to call multiple times, and a
    /// failed disposal may be retried on a subsequent call.
    /// </remarks>
    internal interface ICaptureRunLockHandle : IDisposable
    {
        string LockPath { get; }

        bool IsCreated { get; }
    }
}
