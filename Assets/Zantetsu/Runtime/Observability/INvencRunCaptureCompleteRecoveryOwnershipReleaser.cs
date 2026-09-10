namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC recovery ownership release boundary: one
    /// call is one attempt to release the exact Session Ownership Lease the
    /// operation carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>null</c> operation throws
    /// <see cref="System.ArgumentNullException"/> and one that
    /// <see cref="NvencRunCaptureCompleteRecoveryOwnershipReleaseAdmission"/>
    /// refuses throws <see cref="System.ArgumentException"/>, both before the
    /// lease is touched. There is no status and no attempt result: a call
    /// either returns the receipt of a completed release or lets an exception
    /// through.
    /// </para>
    /// <para>
    /// The attempt calls <c>Dispose</c> on that exact lease once. A disposal
    /// exception is neither caught, wrapped, nor turned into a failure result;
    /// it propagates by the same reference, and the partially released lease
    /// stays retryable through the same operation on a later call. Nothing is
    /// retried inside a call, rolled back, re-acquired, or released on any
    /// other lease.
    /// </para>
    /// <para>
    /// An implementation touches no file, Registry, disposition, Service, or
    /// process state, and owns no thread, queue, task, or wait primitive.
    /// </para>
    /// </remarks>
    internal interface INvencRunCaptureCompleteRecoveryOwnershipReleaser
    {
        NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt Release(
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation);
    }
}
