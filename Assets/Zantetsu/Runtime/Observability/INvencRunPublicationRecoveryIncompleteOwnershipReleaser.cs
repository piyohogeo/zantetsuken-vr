namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous release boundary for a Run whose orphan cleanup has reached
    /// a terminal result: one call is one attempt to release the exact Session
    /// Ownership Lease the operation carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>null</c> operation throws
    /// <see cref="System.ArgumentNullException"/> and one that
    /// <see cref="NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission"/>
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
    /// A cleaned and a failed cleanup are released on the same terms: the
    /// cleanup status is part of the authority the operation already carries,
    /// never a branch in the release itself.
    /// </para>
    /// <para>
    /// This boundary belongs to the orphan cleanup graph alone; it is
    /// deliberately not merged with the CaptureComplete recovery release or the
    /// stopping one, whose authority graphs are different. An implementation
    /// touches no file, re-runs no cleanup, re-inspects and re-classifies
    /// nothing, and leaves the Registry, the disposition, the Service, and
    /// every process state alone, owning no thread, queue, task, or wait
    /// primitive.
    /// </para>
    /// </remarks>
    internal interface INvencRunPublicationRecoveryIncompleteOwnershipReleaser
    {
        NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Release(
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation);
    }
}
