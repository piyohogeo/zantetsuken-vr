using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning operation authorizing the orphan cleanup of a
    /// recovered Run whose publication never finished: the exact classification
    /// that found it incomplete and the exact Session Ownership Lease that Run
    /// holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <see cref="NvencRunPublicationRecoveryDisposition.Incomplete"/> is
    /// accepted. A Run that requires publication recovery continues into the
    /// Capture Index recovery, and a collision or a deferred verification stops
    /// with its own release path; neither is orphan cleanup's subject.
    /// </para>
    /// <para>
    /// What an incomplete observation implies about the graph - no finished
    /// plan, no authoritative plan, no chunk verification - is the decision's
    /// and the snapshot's own invariant and is deliberately not restated here.
    /// The disposition and that graph's validity are the authority.
    /// </para>
    /// <para>
    /// The operation holds those two references only. It enumerates, opens,
    /// reads, deletes, and flushes nothing, decides no deletion order, analyses
    /// and promotes no temporary, never disposes the lease, and adds no status,
    /// attempt result, receipt, proof, token, nonce, generation, latch, or
    /// coordinator. It is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes the same minimal admission conditions
    /// without throwing, so it is false once the lease has been released, in
    /// part or completely. No binding or releasability predicate is offered
    /// yet: the release authority that follows a finished cleanup is a later
    /// design step, and this type will grow one only when that step shows it
    /// is needed.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteCleanupOperation
    {
        private readonly NvencRunPublicationRecoveryDecision _decision;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private NvencRunPublicationRecoveryIncompleteCleanupOperation(
            NvencRunPublicationRecoveryDecision decision,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            _decision = decision;
            _ownershipLease = ownershipLease;
        }

        internal static NvencRunPublicationRecoveryIncompleteCleanupOperation Create(
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

            if (decision.Disposition != NvencRunPublicationRecoveryDisposition.Incomplete)
            {
                throw new ArgumentException(
                    "Only an incomplete publication recovery is orphan cleanup's subject.",
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

            NvencRunPublicationRecoveryIncompleteCleanupOperation operation =
                new NvencRunPublicationRecoveryIncompleteCleanupOperation(decision, ownershipLease);

            if (!operation.IsValid)
            {
                throw new ArgumentException(
                    "The issued operation must be admissible for an orphan cleanup.",
                    nameof(decision));
            }

            return operation;
        }

        internal NvencRunPublicationRecoveryDecision Decision => _decision;

        /// <summary>
        /// The observation this recovery classified. What the plan document and
        /// the NVENC precommit temporary were is read from here, not copied
        /// onto this operation.
        /// </summary>
        internal NvencRunPublicationRecoveryInspectionSnapshot Snapshot => _decision.Snapshot;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal CaptureRunInitializationOpenOutcome OpenOutcome =>
            _decision.Snapshot?.Operation?.OpenOutcome;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
            _decision.Snapshot?.Operation?.LockIdentityEvidence;

        internal CaptureRunRootLayout RootLayout => _decision.RootLayout;

        internal long TestRunId => _decision.TestRunId;

        internal string RunInitializationId => _decision.RunInitializationId;

        /// <summary>
        /// Admission validity for a first orphan cleanup: the same minimal
        /// conditions issuance required, recomputed without throwing.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                try
                {
                    if (_decision == null
                        || _ownershipLease == null
                        || !_decision.IsValid
                        || _decision.Disposition
                            != NvencRunPublicationRecoveryDisposition.Incomplete)
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
                        && lockIdentityEvidence.IsBoundTo(_ownershipLease)
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
