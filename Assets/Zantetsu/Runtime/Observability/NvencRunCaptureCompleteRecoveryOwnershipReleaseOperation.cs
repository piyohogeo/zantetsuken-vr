using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning Phase 0.11 NVENC recovery ownership release
    /// operation: the exact CaptureComplete cleanup attempt result and the
    /// exact Session Ownership Lease that recovered Run holds, fixed together
    /// for the release boundary that follows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failed cleanup does not suppress the lock release: both terminal
    /// cleanup shapes prepare a release, and which one occurred stays readable
    /// through <see cref="CleanupStatus"/>. No separate reflected state is
    /// introduced for that; the cleanup result itself is the record.
    /// </para>
    /// <para>
    /// This type releases nothing and never disposes the lease. It touches no
    /// file, process state, Registry, disposition, or Service, adds no proof,
    /// token, nonce, generation, status, or latch, holds no coordinator, and is
    /// not an <see cref="IDisposable"/>. Every correlation is
    /// <see cref="object.ReferenceEquals"/> against the cleanup graph the
    /// result already carries.
    /// </para>
    /// <para>
    /// The three predicates are deliberately distinct.
    /// <see cref="IsBindingIntact"/> is pure reference correlation and reads no
    /// release state, so a partially or fully released lease keeps it true.
    /// <see cref="CanRelease"/> adds the lease's own current releasability, so
    /// it stays true for a retry after a partial failure and turns false once
    /// the release has completed. <see cref="IsValid"/> is admission only: it
    /// additionally requires a currently valid cleanup result and a lease that
    /// is still fully retained, so it is false after any release. A post-release
    /// check must therefore rest on <see cref="IsBindingIntact"/> or
    /// <see cref="CanRelease"/>, never on <see cref="IsValid"/>.
    /// </para>
    /// <para>
    /// The forwarding surface is intentionally narrow: only what a release
    /// needs. The plan, the snapshot, and the optional commit receipt are
    /// reachable through the forwarded graph and are not duplicated here.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation
    {
        private readonly NvencRunCaptureCompleteRecoveryCleanupAttemptResult _cleanupResult;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation(
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            _cleanupResult = cleanupResult;
            _ownershipLease = ownershipLease;
        }

        internal static NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation Create(
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            // A default or contradictory result is not a terminal cleanup, and
            // each terminal shape's own receipt rule is the result's to
            // enforce.
            if (!cleanupResult.IsValid)
            {
                throw new ArgumentException(
                    "The cleanup attempt result must be a valid terminal shape.",
                    nameof(cleanupResult));
            }

            CaptureRunInitializationOpenOutcome openOutcome = OpenOutcomeOf(cleanupResult);
            if (openOutcome == null
                || openOutcome.Status
                    != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired)
            {
                throw new ArgumentException(
                    "The cleanup graph must come from a publication-recovery open outcome.",
                    nameof(cleanupResult));
            }

            if (openOutcome.Session != null)
            {
                throw new ArgumentException(
                    "A recovered Run must not hold a session.", nameof(cleanupResult));
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence =
                LockIdentityEvidenceOf(cleanupResult);
            if (lockIdentityEvidence == null || !lockIdentityEvidence.IsBoundTo(ownershipLease))
            {
                throw new ArgumentException(
                    "The lock identity evidence must be bound to this exact ownership lease.",
                    nameof(ownershipLease));
            }

            if (!DescribesTheSameRun(cleanupResult, openOutcome))
            {
                throw new ArgumentException(
                    "The cleanup graph and the open outcome must describe the same Run.",
                    nameof(cleanupResult));
            }

            if (!ownershipLease.IsCreated || !ownershipLease.CanRelease)
            {
                throw new ArgumentException(
                    "The ownership lease must still fully hold the lock and be releasable.",
                    nameof(ownershipLease));
            }

            return new NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation(
                cleanupResult, ownershipLease);
        }

        internal NvencRunCaptureCompleteRecoveryCleanupAttemptResult CleanupResult =>
            _cleanupResult;

        internal NvencRunCaptureCompleteRecoveryCleanupOperation CleanupOperation =>
            _cleanupResult.Operation;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal CaptureRunInitializationOpenOutcome OpenOutcome => OpenOutcomeOf(_cleanupResult);

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
            LockIdentityEvidenceOf(_cleanupResult);

        internal CaptureRunRootLayout RootLayout => _cleanupResult.RootLayout;

        internal long TestRunId => _cleanupResult.TestRunId;

        internal string RunInitializationId => _cleanupResult.RunInitializationId;

        /// <summary>
        /// Which terminal cleanup shape prepared this release. A failed cleanup
        /// releases the lock just as a clean one does.
        /// </summary>
        internal NvencRunCaptureCompleteRecoveryCleanupStatus CleanupStatus =>
            _cleanupResult.Status;

        internal bool HasCommitReceipt => _cleanupResult.HasCommitReceipt;

        /// <summary>
        /// Pure reference correlation between the held cleanup result and the
        /// exact ownership lease. It reads no release state and no upstream
        /// validity, so neither a partial nor a completed release revokes it.
        /// </summary>
        internal bool IsBindingIntact
        {
            get
            {
                try
                {
                    if (_ownershipLease == null || _cleanupResult.Operation == null)
                    {
                        return false;
                    }

                    CaptureRunLockIdentityEvidence lockIdentityEvidence =
                        LockIdentityEvidenceOf(_cleanupResult);

                    return lockIdentityEvidence != null
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
        /// still fully holds the lock and is releasable. Any release, partial
        /// or complete, makes this false.
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

        private static CaptureRunInitializationOpenOutcome OpenOutcomeOf(
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult)
        {
            return PublicationRecoveryOperationOf(cleanupResult)?.OpenOutcome;
        }

        private static CaptureRunLockIdentityEvidence LockIdentityEvidenceOf(
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult)
        {
            return PublicationRecoveryOperationOf(cleanupResult)?.LockIdentityEvidence;
        }

        private static NvencRunPublicationRecoveryInspectionOperation PublicationRecoveryOperationOf(
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult)
        {
            return cleanupResult.PublicationRecoveryDecision?.Operation;
        }

        private static bool DescribesTheSameRun(
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult,
            CaptureRunInitializationOpenOutcome openOutcome)
        {
            return ReferenceEquals(cleanupResult.RootLayout, openOutcome.RootLayout)
                && cleanupResult.TestRunId == openOutcome.TestRunId
                && string.Equals(
                    cleanupResult.RunInitializationId,
                    openOutcome.RunInitializationId,
                    StringComparison.Ordinal);
        }
    }
}
