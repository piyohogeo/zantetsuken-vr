using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Holds one Phase 0.11 NVENC recovery ownership release across attempts:
    /// it issues the release operation once from the cleanup result and the
    /// Session Ownership Lease, and keeps the receipt of the release that
    /// eventually succeeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the boundary that makes a retry possible after a partial
    /// release. The same operation is reused - a partially released lease can
    /// only be finished through the exact operation it was released by - and
    /// the retained receipt is the only success latch: there is no separate
    /// flag, retry counter, status, attempt result, result wrapper, proof,
    /// token, nonce, or generation.
    /// </para>
    /// <para>
    /// The receipt is assigned only after
    /// <see cref="NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator.Execute"/>
    /// has returned, so a disposal failure leaves nothing retained and the next
    /// call attempts the same operation again. A release that finished the lock
    /// but produced an unusable receipt is not turned into success either: the
    /// exception propagates, nothing is retained, and the next call stops at
    /// admission rather than inferring a success from the lease's state.
    /// </para>
    /// <para>
    /// It owns no lease and disposes nothing, runs no cleanup, re-checks no
    /// file, changes no process state, Poison, Registry, or disposition, adds
    /// no Run terminal status or completion result, never disposes the OS lock
    /// itself, owns no thread, queue, task, wait, or monitor gate, and is not
    /// an <see cref="IDisposable"/>. The cleanup result, the lease, and the
    /// open outcome are reachable through the operation and are not duplicated
    /// as fields.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator
    {
        private readonly NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator
            _releaseExecution;

        private readonly NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation _operation;

        private NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt _receipt;

        internal NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator(
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult,
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator releaseExecution)
        {
            if (releaseExecution == null)
            {
                throw new ArgumentNullException(nameof(releaseExecution));
            }

            // The operation factory is the only authority on the cleanup
            // result, the open outcome, and the lease correlation; none of that
            // is restated here.
            _operation = NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation.Create(
                cleanupResult, ownershipLease);
            _releaseExecution = releaseExecution;
        }

        internal NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator
            ReleaseExecution => _releaseExecution;

        internal NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation Operation => _operation;

        internal NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt Receipt => _receipt;

        /// <summary>
        /// Whether this Run's lock has been released and attested. It is
        /// derived from the retained receipt and its correlation, not from a
        /// separate flag or from the lease's own state.
        /// </summary>
        internal bool IsReleased =>
            _receipt != null
            && _receipt.IsIssuedFor(_releaseExecution.Releaser, _operation);

        internal NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt Release()
        {
            if (_receipt != null)
            {
                // Already released: the same receipt, without touching the
                // execution coordinator or the lease again.
                if (!_receipt.IsIssuedFor(_releaseExecution.Releaser, _operation))
                {
                    throw new InvalidOperationException(
                        "The retained release receipt is no longer correlated to this Run.");
                }

                return _receipt;
            }

            if (!NvencRunCaptureCompleteRecoveryOwnershipReleaseAdmission.IsAdmissible(_operation))
            {
                // In particular a lease that is fully released without a
                // retained receipt is not a success: nothing here infers one.
                throw new InvalidOperationException(
                    "The retained release operation is no longer admissible for a release attempt.");
            }

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt =
                _releaseExecution.Execute(_operation);

            // Assigned only after a returned execution, so a partial release
            // leaves the retry to the next call with this same operation.
            _receipt = receipt;
            return _receipt;
        }
    }
}
