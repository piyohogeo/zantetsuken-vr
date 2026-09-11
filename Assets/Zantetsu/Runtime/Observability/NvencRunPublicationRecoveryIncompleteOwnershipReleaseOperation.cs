using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning operation authorizing the release of a recovered
    /// Run's Session Ownership Lease once its orphan cleanup has reached a
    /// terminal result: the exact cleanup attempt result and the exact lease
    /// that Run holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cleaned and Failed are accepted on the same terms. A failed orphan
    /// cleanup may have discarded part of the Run already, and this result is
    /// not an authority on how far it got; what actually remains is decided by
    /// a later recovery re-observing the real filesystem while newly holding
    /// this Run's lock. Holding the lock after a failure would only prevent
    /// that, so the lease is returnable either way.
    /// </para>
    /// <para>
    /// The operation holds those two references only, releases nothing, and
    /// never disposes the lease. It maps neither cleanup status into anything
    /// new and adds no attempt result, receipt, proof, token, nonce,
    /// generation, coordinator, or retry latch. It reads and writes no file,
    /// touches no Registry, disposition, Service, or process state, owns no
    /// thread, queue, task, or wait primitive, and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// <para>
    /// The three predicates carry the same meaning as on the other recovery
    /// release operations, which this type deliberately does not share a base
    /// or a generalization with: its authority graph is the orphan cleanup's,
    /// not theirs. <see cref="IsBindingIntact"/> is pure reference correlation
    /// and reads no release state and no current upstream validity, so a
    /// partially or fully released lease keeps it true.
    /// <see cref="CanRelease"/> adds the lease's own releasability, so a first
    /// attempt and a retry after a partial failure both pass while a completed
    /// release does not. <see cref="IsValid"/> is first-attempt admission only
    /// and is false after any release: a receipt attesting a completed release
    /// must never rest on it, and a post-release check must rest on the first
    /// two.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation
    {
        private readonly NvencRunPublicationRecoveryIncompleteCleanupAttemptResult _cleanupResult;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation(
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult cleanupResult,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            _cleanupResult = cleanupResult;
            _ownershipLease = ownershipLease;
        }

        internal static NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation Create(
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult cleanupResult,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            // The result is the authority on its own shape: the uninitialized
            // default, an undefined status, a cleaned result without its
            // receipt, and a failed one carrying a receipt are all invalid
            // there, and none of that is restated here.
            if (!cleanupResult.IsValid)
            {
                throw new ArgumentException(
                    "Cleanup attempt result must be valid.", nameof(cleanupResult));
            }

            NvencRunPublicationRecoveryIncompleteCleanupOperation cleanupOperation =
                cleanupResult.CleanupOperation;
            CaptureRunInitializationOpenOutcome openOutcome = cleanupOperation.OpenOutcome;

            if (openOutcome == null
                || openOutcome.Status
                    != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired
                || openOutcome.Session != null
                || !DescribesTheSameRun(cleanupOperation, openOutcome))
            {
                throw new ArgumentException(
                    "The cleanup graph must come from a session-free publication-recovery open outcome for the same Run.",
                    nameof(cleanupResult));
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence =
                cleanupOperation.LockIdentityEvidence;
            if (lockIdentityEvidence == null || !lockIdentityEvidence.IsBoundTo(ownershipLease))
            {
                throw new ArgumentException(
                    "The lock identity evidence must be bound to this exact ownership lease.",
                    nameof(ownershipLease));
            }

            if (!ownershipLease.IsCreated || !ownershipLease.CanRelease)
            {
                throw new ArgumentException(
                    "The ownership lease must still fully hold the lock and be releasable.",
                    nameof(ownershipLease));
            }

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                new NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation(
                    cleanupResult, ownershipLease);

            if (!operation.IsValid)
            {
                throw new ArgumentException(
                    "The issued operation must be admissible for a release.", nameof(cleanupResult));
            }

            return operation;
        }

        internal NvencRunPublicationRecoveryIncompleteCleanupAttemptResult CleanupResult =>
            _cleanupResult;

        internal NvencRunPublicationRecoveryIncompleteCleanupOperation CleanupOperation =>
            _cleanupResult.CleanupOperation;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        // The factory admits only a valid result, so the cleanup operation
        // these read through is always there.
        internal NvencRunPublicationRecoveryDecision Decision =>
            _cleanupResult.CleanupOperation.Decision;

        internal NvencRunPublicationRecoveryInspectionSnapshot Snapshot =>
            _cleanupResult.CleanupOperation.Snapshot;

        internal CaptureRunInitializationOpenOutcome OpenOutcome =>
            _cleanupResult.CleanupOperation.OpenOutcome;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
            _cleanupResult.CleanupOperation.LockIdentityEvidence;

        internal CaptureRunRootLayout RootLayout => _cleanupResult.CleanupOperation.RootLayout;

        internal long TestRunId => _cleanupResult.CleanupOperation.TestRunId;

        internal string RunInitializationId =>
            _cleanupResult.CleanupOperation.RunInitializationId;

        /// <summary>
        /// Which terminal cleanup shape this release follows, carried as the
        /// existing status rather than mapped into anything new. It selects no
        /// behaviour here.
        /// </summary>
        internal NvencRunPublicationRecoveryIncompleteCleanupStatus CleanupStatus =>
            _cleanupResult.Status;

        /// <summary>
        /// Pure reference correlation between the held cleanup graph and the
        /// exact ownership lease. It reads no release state and no current
        /// upstream validity, so neither a partial nor a completed release
        /// revokes it.
        /// </summary>
        internal bool IsBindingIntact
        {
            get
            {
                try
                {
                    NvencRunPublicationRecoveryIncompleteCleanupOperation cleanupOperation =
                        _cleanupResult.CleanupOperation;

                    if (cleanupOperation == null || _ownershipLease == null)
                    {
                        return false;
                    }

                    CaptureRunInitializationOpenOutcome openOutcome = cleanupOperation.OpenOutcome;
                    CaptureRunLockIdentityEvidence lockIdentityEvidence =
                        cleanupOperation.LockIdentityEvidence;

                    return openOutcome != null
                        && openOutcome.Status
                            == CaptureRunInitializationOpenStatus.PublicationRecoveryRequired
                        && openOutcome.Session == null
                        && DescribesTheSameRun(cleanupOperation, openOutcome)
                        && lockIdentityEvidence != null
                        && lockIdentityEvidence.IsBoundTo(_ownershipLease);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The binding still holds and the exact lease's own disposal has not
        /// completed, so a first attempt or a retry after a partial failure is
        /// still possible.
        /// </summary>
        internal bool CanRelease
        {
            get
            {
                try
                {
                    return IsBindingIntact && _ownershipLease.CanRelease;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Admission validity, for the first issuance of a release only: the
        /// binding holds, the cleanup result is currently valid, and the lease
        /// still fully holds the lock and is releasable. It is false once any
        /// part of the release has happened, so nothing that attests a
        /// completed release may depend on it.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                try
                {
                    return IsBindingIntact
                        && _cleanupResult.IsValid
                        && _ownershipLease.IsCreated
                        && _ownershipLease.CanRelease;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool DescribesTheSameRun(
            NvencRunPublicationRecoveryIncompleteCleanupOperation cleanupOperation,
            CaptureRunInitializationOpenOutcome openOutcome)
        {
            return ReferenceEquals(cleanupOperation.RootLayout, openOutcome.RootLayout)
                && cleanupOperation.TestRunId == openOutcome.TestRunId
                && string.Equals(
                    cleanupOperation.RunInitializationId,
                    openOutcome.RunInitializationId,
                    StringComparison.Ordinal);
        }
    }
}
