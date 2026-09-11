using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous production releaser for a Run whose orphan
    /// cleanup has finished: it releases the exact Session Ownership Lease its
    /// operation carries, once per call, and returns the receipt of that
    /// completed release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Admission is the shared
    /// <see cref="NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission"/>,
    /// so this releaser cannot disagree with its execution coordinator about
    /// whether a first attempt or a retry is allowed; there is no separate
    /// judgement here, and no retry counter, latch, status, attempt result,
    /// proof, token, nonce, or generation.
    /// </para>
    /// <para>
    /// A cleaned and a failed cleanup are not told apart: the cleanup status is
    /// part of the authority the operation already carries, never a branch in
    /// the release itself, and neither is the decision, the disposition, or any
    /// process state.
    /// </para>
    /// <para>
    /// A disposal exception is neither caught, wrapped, nor turned into a
    /// failure result: it propagates by the same reference, no receipt is
    /// issued, and the partially released lease stays retryable through the
    /// same operation on a later call - where the shared admission's retry
    /// branch lets the remaining handle be tried again. Nothing is retried
    /// inside one call, rolled back, re-acquired, or released on any other
    /// lease. The receipt's own factory is what requires an intact binding and
    /// a fully released lease, so a disposal that returned without finishing
    /// the release is refused there rather than attested here, and that
    /// exception propagates too: a released lock is not a licence to fabricate
    /// a different receipt or a success.
    /// </para>
    /// <para>
    /// The type holds no instance field and no mutable static state, owns no
    /// thread, queue, task, wait primitive, filesystem, buffer, or handle, and
    /// is not an <see cref="IDisposable"/>. It evaluates that shared admission
    /// before touching the lease; it performs no filesystem re-observation,
    /// does not rerun the cleanup, does not reclassify the publication
    /// recovery, and touches no Registry, disposition, Service, or process
    /// state. It reaches the lock only through the lease's own public
    /// disposal, never through a raw handle, and shares no base or
    /// generalization with the other recovery releasers, whose authority
    /// graphs differ.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteOwnershipReleaser
        : INvencRunPublicationRecoveryIncompleteOwnershipReleaser
    {
        public NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Release(
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission.IsAdmissible(
                    operation))
            {
                throw new ArgumentException(
                    "Operation must be admissible for a first release attempt or a retry.",
                    nameof(operation));
            }

            // The lease's own disposal, exactly once. A partial failure leaves
            // by this call's exception with no receipt.
            operation.OwnershipLease.Dispose();

            return NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                this, operation);
        }
    }
}
