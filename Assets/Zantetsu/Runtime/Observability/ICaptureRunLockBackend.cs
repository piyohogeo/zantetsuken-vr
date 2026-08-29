namespace Zantetsu.Observability
{
    /// <summary>
    /// Non-blocking Capture Run OS lock backend. A backend attempts one
    /// acquisition per call and never retries, waits, or falls back to another
    /// path.
    /// </summary>
    /// <remarks>
    /// Contract: returning <c>true</c> yields a non-null, created handle whose
    /// ownership has transferred to the caller; returning <c>false</c> yields a
    /// null handle and means an ordinary contention result. Exceptions are
    /// reserved for unsafe paths, reparse points, I/O failures, and backend
    /// invariant violations. On <c>false</c> or an exception, no handle
    /// ownership is left with the caller.
    /// </remarks>
    internal interface ICaptureRunLockBackend
    {
        bool TryAcquire(string absoluteLockPath, out ICaptureRunLockHandle handle);
    }
}
