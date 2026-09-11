using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Holds one stopped publication recovery's lease release across attempts:
    /// it issues the release operation once from the stopping classification
    /// and the Session Ownership Lease, and keeps the receipt of the release
    /// that eventually succeeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes a retry possible after a partial release. The same
    /// operation is reused - a partially released lease can only be finished
    /// through the exact operation it was released by - and the retained
    /// receipt is the only success latch: no separate flag, retry counter,
    /// status, attempt result, result wrapper, proof, token, nonce, or
    /// generation.
    /// </para>
    /// <para>
    /// The receipt is assigned only after
    /// <see cref="NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator.Execute"/>
    /// has returned, so a disposal failure leaves nothing retained and the next
    /// call enters the shared admission's retry branch with that same
    /// operation. A release that finished the lock but produced an unusable
    /// receipt is not turned into success: the exception propagates, nothing is
    /// retained, and the next call stops at admission rather than inferring a
    /// success from the lease's state.
    /// </para>
    /// <para>
    /// The collision and deferred shapes are never branched on here. It owns no
    /// lease and disposes nothing, never disposes the lock itself, performs no
    /// filesystem re-inspection or reclassification, runs no cleanup, Capture
    /// Index, or CaptureComplete work, changes no process state, Poison,
    /// Registry, disposition, or Service, publishes no Run terminal status or
    /// completion result, owns no thread, queue, task, wait, or monitor gate,
    /// and is not an <see cref="IDisposable"/>. What it does re-check is only
    /// in memory: the retained receipt's correlation and the shared admission.
    /// The decision and the lease are reachable through the operation and are
    /// not duplicated here.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator
    {
        private readonly NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator
            _releaseExecution;

        private readonly NvencRunPublicationRecoveryStopOwnershipReleaseOperation _operation;

        private NvencRunPublicationRecoveryStopOwnershipReleaseReceipt _receipt;

        internal NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
            NvencRunPublicationRecoveryDecision decision,
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator releaseExecution)
        {
            if (releaseExecution == null)
            {
                throw new ArgumentNullException(nameof(releaseExecution));
            }

            // The operation factory is the only authority on the decision, the
            // snapshot, the open outcome, and the lease correlation; none of
            // that is restated here.
            _operation = NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                decision, ownershipLease);
            _releaseExecution = releaseExecution;
        }

        internal NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator
            ReleaseExecution => _releaseExecution;

        internal NvencRunPublicationRecoveryStopOwnershipReleaseOperation Operation => _operation;

        internal NvencRunPublicationRecoveryStopOwnershipReleaseReceipt Receipt => _receipt;

        /// <summary>
        /// Whether this Run's lease has been released and attested. It is
        /// derived from the retained receipt and its correlation, not from a
        /// separate flag or from the lease's own state.
        /// </summary>
        internal bool IsReleased =>
            _receipt != null
            && _receipt.IsIssuedFor(_releaseExecution.Releaser, _operation);

        internal NvencRunPublicationRecoveryStopOwnershipReleaseReceipt Release()
        {
            if (_receipt != null)
            {
                // Already released: the same receipt, without touching the
                // execution coordinator, the releaser, or the lease again.
                if (!_receipt.IsIssuedFor(_releaseExecution.Releaser, _operation))
                {
                    throw new InvalidOperationException(
                        "The retained release receipt is no longer correlated to this Run.");
                }

                return _receipt;
            }

            if (!NvencRunPublicationRecoveryStopOwnershipReleaseAdmission.IsAdmissible(_operation))
            {
                // In particular a lease that is fully released without a
                // retained receipt is not a success: nothing here infers one.
                throw new InvalidOperationException(
                    "The retained release operation is no longer admissible for a release attempt.");
            }

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                _releaseExecution.Execute(_operation);

            // Assigned only after a returned execution, so a partial release
            // leaves the retry to the next call with this same operation.
            _receipt = receipt;
            return _receipt;
        }
    }
}
