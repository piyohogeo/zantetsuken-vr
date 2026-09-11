using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning operation authorizing the release of a recovered
    /// Run's Session Ownership Lease when the publication recovery stopped: the
    /// exact classification that stopped it and the exact lease that Run holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the two stopping dispositions are accepted.
    /// <see cref="NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision"/>
    /// has found another writer's evidence and
    /// <see cref="NvencRunPublicationRecoveryDisposition.Deferred"/> could not
    /// verify the chunk; in both the Run goes no further and nothing more is
    /// owed to the filesystem, so the lock can be let go.
    /// <see cref="NvencRunPublicationRecoveryDisposition.Incomplete"/> is
    /// refused: whether such a Run may be released without first removing or
    /// quarantining its orphaned roots is not settled, and
    /// <see cref="NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired"/>
    /// is refused because that Run continues into the Capture Index recovery
    /// the CaptureComplete cleanup path owns.
    /// </para>
    /// <para>
    /// The operation holds those two references only, releases nothing, and
    /// never disposes the lease. It maps neither disposition into a new status
    /// and adds no attempt result, receipt, proof, token, nonce, generation,
    /// coordinator, or retry latch. It reads and writes no file and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// <para>
    /// The three predicates carry the same meaning as on the other recovery
    /// release operation. <see cref="IsBindingIntact"/> is pure reference
    /// correlation and reads no release state, so a partially or fully released
    /// lease keeps it true. <see cref="CanRelease"/> adds the lease's own
    /// releasability. <see cref="IsValid"/> is admission only and is false
    /// after any release, so a post-release check must rest on the first two.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryStopOwnershipReleaseOperation
    {
        private readonly NvencRunPublicationRecoveryDecision _decision;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private NvencRunPublicationRecoveryStopOwnershipReleaseOperation(
            NvencRunPublicationRecoveryDecision decision,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            _decision = decision;
            _ownershipLease = ownershipLease;
        }

        internal static NvencRunPublicationRecoveryStopOwnershipReleaseOperation Create(
            NvencRunPublicationRecoveryDecision decision,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            if (decision == null || !decision.IsValid)
            {
                throw new ArgumentException(
                    "Publication recovery decision must be valid.", nameof(decision));
            }

            if (!IsStoppingDisposition(decision.Disposition))
            {
                throw new ArgumentException(
                    "Only a collision or a deferred publication recovery releases the lock here.",
                    nameof(decision));
            }

            NvencRunPublicationRecoveryInspectionOperation inspection = decision.Snapshot.Operation;
            CaptureRunInitializationOpenOutcome openOutcome = inspection?.OpenOutcome;

            if (openOutcome == null
                || openOutcome.Status
                    != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired
                || openOutcome.Session != null
                || !DescribesTheSameRun(decision, openOutcome))
            {
                throw new ArgumentException(
                    "The decision graph must come from a session-free publication-recovery open outcome for the same Run.",
                    nameof(decision));
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence = inspection.LockIdentityEvidence;
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

            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation =
                new NvencRunPublicationRecoveryStopOwnershipReleaseOperation(
                    decision, ownershipLease);

            if (!operation.IsValid)
            {
                throw new ArgumentException(
                    "The issued operation must be admissible for a release.", nameof(decision));
            }

            return operation;
        }

        internal NvencRunPublicationRecoveryDecision Decision => _decision;

        internal NvencRunPublicationRecoveryInspectionSnapshot Snapshot => _decision.Snapshot;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal CaptureRunInitializationOpenOutcome OpenOutcome =>
            _decision.Snapshot?.Operation?.OpenOutcome;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
            _decision.Snapshot?.Operation?.LockIdentityEvidence;

        /// <summary>
        /// Which stopping classification this release follows, carried as the
        /// existing disposition rather than mapped into anything new.
        /// </summary>
        internal NvencRunPublicationRecoveryDisposition Disposition => _decision.Disposition;

        internal CaptureRunRootLayout RootLayout => _decision.RootLayout;

        internal long TestRunId => _decision.TestRunId;

        internal string RunInitializationId => _decision.RunInitializationId;

        /// <summary>
        /// Pure reference correlation between the held decision graph and the
        /// exact ownership lease. It reads no release state and no current
        /// decision validity, so neither a partial nor a completed release
        /// revokes it.
        /// </summary>
        internal bool IsBindingIntact
        {
            get
            {
                try
                {
                    if (_decision == null
                        || _ownershipLease == null
                        || !IsStoppingDisposition(_decision.Disposition))
                    {
                        return false;
                    }

                    NvencRunPublicationRecoveryInspectionOperation inspection =
                        _decision.Snapshot?.Operation;
                    CaptureRunInitializationOpenOutcome openOutcome = inspection?.OpenOutcome;
                    CaptureRunLockIdentityEvidence lockIdentityEvidence =
                        inspection?.LockIdentityEvidence;

                    return openOutcome != null
                        && openOutcome.Status
                            == CaptureRunInitializationOpenStatus.PublicationRecoveryRequired
                        && openOutcome.Session == null
                        && DescribesTheSameRun(_decision, openOutcome)
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
        /// binding holds, the decision is currently valid, and the lease still
        /// fully holds the lock and is releasable.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                try
                {
                    return IsBindingIntact
                        && _decision.IsValid
                        && _ownershipLease.IsCreated
                        && _ownershipLease.CanRelease;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool IsStoppingDisposition(NvencRunPublicationRecoveryDisposition disposition)
        {
            return disposition
                    == NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision
                || disposition == NvencRunPublicationRecoveryDisposition.Deferred;
        }

        private static bool DescribesTheSameRun(
            NvencRunPublicationRecoveryDecision decision,
            CaptureRunInitializationOpenOutcome openOutcome)
        {
            return ReferenceEquals(decision.RootLayout, openOutcome.RootLayout)
                && decision.TestRunId == openOutcome.TestRunId
                && string.Equals(
                    decision.RunInitializationId,
                    openOutcome.RunInitializationId,
                    StringComparison.Ordinal);
        }
    }
}
