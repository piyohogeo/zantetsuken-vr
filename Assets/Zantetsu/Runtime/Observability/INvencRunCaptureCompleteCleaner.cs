namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Run CaptureComplete cleanup boundary. It
    /// cleans up after exactly one issued
    /// <see cref="NvencRunCaptureCompleteCleanupOperation"/> and returns one
    /// <see cref="NvencRunCaptureCompleteCleanupAttemptResult"/> per call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller is contractually the future cleanup phase; caller thread
    /// selection is not this boundary's duty, and no dispatcher or thread
    /// identity mechanism is added here.
    /// </para>
    /// <para>
    /// Before any side effect a <c>null</c> operation must throw
    /// <see cref="System.ArgumentNullException"/> for <c>operation</c>, and an
    /// invalid operation (one whose current
    /// <see cref="NvencRunCaptureCompleteCleanupOperation.IsValid"/> is false)
    /// must throw <see cref="System.ArgumentException"/> for <c>operation</c>.
    /// The operation is never mutated or disposed. Each call is one attempt and
    /// returns exactly one result; the implementation performs no second
    /// attempt, no retry, no rollback, no guessing of a cleanup outcome, and no
    /// fallback to another path, and owns no thread, queue, task, wait, or
    /// lease.
    /// </para>
    /// <para>
    /// Cleanup never touches the published side of the Run: the final chunk,
    /// the capture index, the Registry, and the disposition are all left
    /// alone. This surface deliberately fixes no deletion order and no
    /// platform API; what a production cleaner actually removes is decided
    /// with that cleaner.
    /// </para>
    /// </remarks>
    internal interface INvencRunCaptureCompleteCleaner
    {
        NvencRunCaptureCompleteCleanupAttemptResult Clean(
            NvencRunCaptureCompleteCleanupOperation operation);
    }
}
