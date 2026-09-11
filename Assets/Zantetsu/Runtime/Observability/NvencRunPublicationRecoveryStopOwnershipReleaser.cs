using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous production releaser for a Run whose publication
    /// recovery stopped: it releases the exact Session Ownership Lease its
    /// operation carries, once per call, and returns the receipt of that
    /// completed release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Admission is the shared
    /// <see cref="NvencRunPublicationRecoveryStopOwnershipReleaseAdmission"/>,
    /// so this releaser cannot disagree with its execution coordinator about
    /// whether a first attempt or a retry is allowed; there is no separate
    /// judgement here, and no retry counter, latch, status, attempt result,
    /// proof, token, nonce, or generation.
    /// </para>
    /// <para>
    /// The collision and deferred shapes are not told apart: the disposition is
    /// part of the authority the operation already carries, never a branch in
    /// the release itself.
    /// </para>
    /// <para>
    /// A disposal exception is neither caught, wrapped, nor turned into a
    /// failure result: it propagates by the same reference, no receipt is
    /// issued, and the partially released lease stays retryable through the
    /// same operation on a later call - where the shared admission's retry
    /// branch lets the remaining handle be tried again. Nothing is retried
    /// inside one call, rolled back, re-acquired, or released on any other
    /// lease. A disposal that returned without finishing the release is refused
    /// rather than attested, and an exception from the receipt's own issuance
    /// propagates too: a released lock is not a licence to fabricate a
    /// different receipt or a success.
    /// </para>
    /// <para>
    /// The type holds no instance field and no mutable static state, owns no
    /// thread, queue, task, wait primitive, filesystem, buffer, or handle,
    /// inspects and cleans up nothing, touches no Capture Index,
    /// CaptureComplete, Registry, disposition, Service, or process state, and
    /// is not an <see cref="IDisposable"/>. It reaches the lock only through
    /// the lease's own public disposal, never through a raw handle.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryStopOwnershipReleaser
        : INvencRunPublicationRecoveryStopOwnershipReleaser
    {
        public NvencRunPublicationRecoveryStopOwnershipReleaseReceipt Release(
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!NvencRunPublicationRecoveryStopOwnershipReleaseAdmission.IsAdmissible(operation))
            {
                throw new ArgumentException(
                    "Operation must be admissible for a first release attempt or a retry.",
                    nameof(operation));
            }

            // The lease's own disposal, exactly once. A partial failure leaves
            // by this call's exception with no receipt.
            operation.OwnershipLease.Dispose();

            if (!operation.IsBindingIntact
                || !operation.OwnershipLease.IsReleaseComplete
                || operation.CanRelease)
            {
                throw new InvalidOperationException(
                    "The ownership lease disposal returned without completing the release.");
            }

            return NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(this, operation);
        }
    }
}
