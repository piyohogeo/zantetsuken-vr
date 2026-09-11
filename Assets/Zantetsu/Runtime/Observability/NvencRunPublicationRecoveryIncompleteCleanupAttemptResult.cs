using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Readonly value result of one Phase 0.11 NVENC publication recovery
    /// orphan cleanup attempt: the exact cleaner, the exact operation, a
    /// status, and a receipt held only for the
    /// <see cref="NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned"/>
    /// shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two terminal shapes are mutually exclusive and
    /// <see cref="NvencRunPublicationRecoveryIncompleteCleanupStatus.None"/> is
    /// the uninitialized default, never a terminal shape. A Failed attempt
    /// carries no receipt and says nothing about how far the cleanup got: this
    /// result is not an authority on progress, and there is no partial-progress
    /// ledger, per-target status, unknown outcome, or retryable flag. What
    /// remains on disk is answered by a later recovery re-observing the real
    /// filesystem while newly holding the Run's lock.
    /// </para>
    /// <para>
    /// The publication recovery decision, its snapshot, the initialization open
    /// outcome, the lock identity evidence, the Session Ownership Lease, the
    /// root layout, and the Run identity are forwarded from the held operation
    /// rather than duplicated as fields, and no plan, temporary observation,
    /// path, hash, file set, deletion progress, proof, token, or nonce is
    /// introduced. <see cref="IsValid"/> is the single place the valid shape is
    /// defined; <see cref="IsCleaned"/> and <see cref="IsFailed"/> only name
    /// which of those shapes it is.
    /// </para>
    /// </remarks>
    internal readonly struct NvencRunPublicationRecoveryIncompleteCleanupAttemptResult
    {
        private readonly INvencRunPublicationRecoveryIncompleteCleaner _cleaner;
        private readonly NvencRunPublicationRecoveryIncompleteCleanupOperation _operation;
        private readonly NvencRunPublicationRecoveryIncompleteCleanupReceipt _receipt;
        private readonly NvencRunPublicationRecoveryIncompleteCleanupStatus _status;

        private NvencRunPublicationRecoveryIncompleteCleanupAttemptResult(
            INvencRunPublicationRecoveryIncompleteCleaner cleaner,
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation,
            NvencRunPublicationRecoveryIncompleteCleanupReceipt receipt,
            NvencRunPublicationRecoveryIncompleteCleanupStatus status)
        {
            _cleaner = cleaner;
            _operation = operation;
            _receipt = receipt;
            _status = status;
        }

        internal static NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Cleaned(
            INvencRunPublicationRecoveryIncompleteCleaner cleaner,
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
        {
            RequireIssuable(cleaner, operation);

            return new NvencRunPublicationRecoveryIncompleteCleanupAttemptResult(
                cleaner,
                operation,
                NvencRunPublicationRecoveryIncompleteCleanupReceipt.Cleaned(cleaner, operation),
                NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned);
        }

        internal static NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Failed(
            INvencRunPublicationRecoveryIncompleteCleaner cleaner,
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
        {
            RequireIssuable(cleaner, operation);

            return new NvencRunPublicationRecoveryIncompleteCleanupAttemptResult(
                cleaner,
                operation,
                null,
                NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed);
        }

        internal INvencRunPublicationRecoveryIncompleteCleaner Cleaner => _cleaner;

        internal NvencRunPublicationRecoveryIncompleteCleanupOperation CleanupOperation =>
            _operation;

        internal NvencRunPublicationRecoveryIncompleteCleanupReceipt Receipt => _receipt;

        internal NvencRunPublicationRecoveryIncompleteCleanupStatus Status => _status;

        internal NvencRunPublicationRecoveryDecision Decision => _operation.Decision;

        internal NvencRunPublicationRecoveryInspectionSnapshot Snapshot => _operation.Snapshot;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease =>
            _operation.OwnershipLease;

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _operation.OpenOutcome;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
            _operation.LockIdentityEvidence;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsNone =>
            _status == NvencRunPublicationRecoveryIncompleteCleanupStatus.None;

        internal bool IsCleaned =>
            _status == NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned && IsValid;

        internal bool IsFailed =>
            _status == NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed && IsValid;

        /// <summary>
        /// The one definition of a valid result shape: the exact cleaner and a
        /// currently valid exact operation, a Cleaned status with the receipt
        /// of that exact pair, a Failed status with no receipt at all, and
        /// nothing else - the uninitialized default and any undefined status
        /// included.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                try
                {
                    if (_cleaner == null || _operation == null || !_operation.IsValid)
                    {
                        return false;
                    }

                    switch (_status)
                    {
                        case NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned:
                            return _receipt != null
                                && _receipt.IsIssuedFor(_cleaner, _operation);

                        case NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed:
                            return _receipt == null;

                        default:
                            return false;
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        internal bool IsIssuedFor(
            INvencRunPublicationRecoveryIncompleteCleaner cleaner,
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
        {
            return cleaner != null
                && operation != null
                && ReferenceEquals(_cleaner, cleaner)
                && ReferenceEquals(_operation, operation)
                && IsValid;
        }

        private static void RequireIssuable(
            INvencRunPublicationRecoveryIncompleteCleaner cleaner,
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
        {
            if (cleaner == null)
            {
                throw new ArgumentNullException(nameof(cleaner));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }
        }
    }
}
