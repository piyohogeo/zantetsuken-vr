using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable evidence that one Phase 0.11 NVENC publication recovery orphan
    /// cleanup finished: the exact cleaner that performed it and the exact
    /// operation it performed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those two references are the whole state. The publication recovery
    /// decision, its snapshot, the initialization open outcome, the lock
    /// identity evidence, the Session Ownership Lease, the root layout, and the
    /// Run identity are forwarded from the operation's graph rather than
    /// copied, and no plan, temporary observation, path, hash, file set, or
    /// deletion progress is duplicated into a field of its own.
    /// </para>
    /// <para>
    /// This is process-local evidence of one successful synchronous call. It is
    /// not a filesystem snapshot, not a proof of full durability, and not
    /// evidence that the OS lock was released. It is minted only on success,
    /// through the success-only factory, which requires a cleaner, an
    /// operation, and that the operation is still valid.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, performs no filesystem
    /// work, releases no lock, and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject. <see cref="IsValid"/> never throws,
    /// so once the OS lock is released the operation becomes invalid and this
    /// receipt follows it.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteCleanupReceipt
    {
        private readonly INvencRunPublicationRecoveryIncompleteCleaner _cleaner;
        private readonly NvencRunPublicationRecoveryIncompleteCleanupOperation _operation;

        private NvencRunPublicationRecoveryIncompleteCleanupReceipt(
            INvencRunPublicationRecoveryIncompleteCleaner cleaner,
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
        {
            _cleaner = cleaner;
            _operation = operation;
        }

        /// <summary>
        /// Mints the evidence of one finished orphan cleanup. There is no
        /// failure receipt: a cleanup that did not finish is reported as Failed
        /// with no receipt at all.
        /// </summary>
        internal static NvencRunPublicationRecoveryIncompleteCleanupReceipt Cleaned(
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

            return new NvencRunPublicationRecoveryIncompleteCleanupReceipt(cleaner, operation);
        }

        internal INvencRunPublicationRecoveryIncompleteCleaner Cleaner => _cleaner;

        internal NvencRunPublicationRecoveryIncompleteCleanupOperation CleanupOperation =>
            _operation;

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

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _cleaner != null && _operation != null && _operation.IsValid;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether this receipt is the evidence of that exact cleaner
        /// performing that exact operation, and is still valid.
        /// </summary>
        internal bool IsIssuedFor(
            INvencRunPublicationRecoveryIncompleteCleaner cleaner,
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
        {
            try
            {
                return ReferenceEquals(_cleaner, cleaner)
                    && ReferenceEquals(_operation, operation)
                    && IsValid;
            }
            catch
            {
                return false;
            }
        }
    }
}
