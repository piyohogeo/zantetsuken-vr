using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Holds one finished orphan cleanup's lease release across attempts: it
    /// issues the release operation once from the cleanup's terminal result and
    /// the Session Ownership Lease, and keeps the receipt of the release that
    /// eventually succeeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes a retry possible after a partial release. This
    /// coordinator retries a partial release only through that same retained
    /// operation, and the retained receipt is the only success latch: no
    /// separate flag, retry counter, status, attempt result, result wrapper,
    /// proof, token, nonce, or generation.
    /// </para>
    /// <para>
    /// The receipt is assigned only after
    /// <see cref="NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator.Execute"/>
    /// has returned, so a disposal failure leaves nothing retained and the next
    /// call enters the shared admission's retry branch with that same
    /// operation. A release that finished the lock but produced an unusable
    /// receipt is not turned into success: the exception propagates, nothing is
    /// retained, and the next call stops at admission rather than inferring a
    /// success from the lease's state.
    /// </para>
    /// <para>
    /// The cleaned and failed cleanup shapes are never branched on here. It
    /// owns no lease and disposes nothing, never disposes the lock itself,
    /// performs no filesystem re-observation, reruns no cleanup, reclassifies
    /// no publication recovery, changes no process state, Poison, Registry,
    /// disposition, or Service, publishes no Run terminal status, owns no
    /// thread, queue, task, wait, or monitor gate, and is not an
    /// <see cref="IDisposable"/>. What it does re-check is only in memory: the
    /// retained receipt's correlation and the shared admission. The cleanup
    /// result, the decision, and the lease are reachable through the operation
    /// and are not duplicated here.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator
    {
        private readonly NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
            _releaseExecution;

        private readonly NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation _operation;

        private NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt _receipt;

        internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
                releaseExecution,
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult cleanupResult,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (releaseExecution == null)
            {
                throw new ArgumentNullException(nameof(releaseExecution));
            }

            // The operation factory is the only authority on the cleanup
            // result's shape, the open outcome, the lock identity evidence, the
            // root layout, the Run identity, and the lease correlation; none of
            // that is restated here.
            _operation = NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                cleanupResult, ownershipLease);
            _releaseExecution = releaseExecution;
        }

        internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
            ReleaseExecution => _releaseExecution;

        internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation Operation =>
            _operation;

        internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Receipt => _receipt;

        /// <summary>
        /// Whether this Run's lease has been released and attested. It is
        /// derived from the retained receipt and its correlation, not from a
        /// separate flag or from the lease's own state.
        /// </summary>
        internal bool IsReleased =>
            _receipt != null
            && _receipt.IsIssuedFor(_releaseExecution.Releaser, _operation);

        internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Release()
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

            if (!NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission.IsAdmissible(
                    _operation))
            {
                // In particular a lease that is fully released without a
                // retained receipt is not a success: nothing here infers one.
                throw new InvalidOperationException(
                    "The retained release operation is no longer admissible for a release attempt.");
            }

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                _releaseExecution.Execute(_operation);

            // Assigned only after a returned execution, so a partial release
            // leaves the retry to the next call with this same operation.
            _receipt = receipt;
            return _receipt;
        }
    }
}
