using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable evidence that one finished orphan cleanup released its Run's
    /// Session Ownership Lease: the exact releaser that performed it and the
    /// exact operation it performed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those two references are the whole state; the cleanup attempt result and
    /// its operation, the publication recovery decision, its snapshot, the
    /// lease, the open outcome, the lock identity evidence, the cleanup status,
    /// the root layout, and the Run identity are forwarded from the operation's
    /// graph. The plan and the observation details stay where they are,
    /// reachable through that graph.
    /// </para>
    /// <para>
    /// A completed release makes the operation's admission validity - and the
    /// decision's own validity - false, so this receipt asks for neither.
    /// <see cref="IsValid"/> requires the binding to still hold, the exact
    /// lease to be fully released, and no further release to be possible, which
    /// is exactly what a finished release looks like.
    /// </para>
    /// <para>
    /// A cleaned and a failed cleanup are attested the same way: the status is
    /// carried, never branched on, and this receipt says nothing about what the
    /// cleanup left on disk.
    /// </para>
    /// <para>
    /// This type releases nothing, owns and disposes nothing, touches no file,
    /// and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject. It is minted only after a successful release, through
    /// the success-only factory, and adds no proof, token, nonce, generation,
    /// or filesystem snapshot.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt
    {
        private readonly INvencRunPublicationRecoveryIncompleteOwnershipReleaser _releaser;
        private readonly NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation _operation;

        private NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt(
            INvencRunPublicationRecoveryIncompleteOwnershipReleaser releaser,
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
        {
            _releaser = releaser;
            _operation = operation;
        }

        /// <summary>
        /// Mints the evidence of one completed release. There is no failure
        /// receipt: a release that did not complete leaves by an exception and
        /// stays retryable.
        /// </summary>
        internal static NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Released(
            INvencRunPublicationRecoveryIncompleteOwnershipReleaser releaser,
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
        {
            if (releaser == null)
            {
                throw new ArgumentNullException(nameof(releaser));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsBindingIntact)
            {
                throw new ArgumentException(
                    "Operation binding must still be intact.", nameof(operation));
            }

            if (!operation.OwnershipLease.IsReleaseComplete || operation.CanRelease)
            {
                throw new ArgumentException(
                    "The ownership lease must be fully released.", nameof(operation));
            }

            return new NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt(
                releaser, operation);
        }

        internal INvencRunPublicationRecoveryIncompleteOwnershipReleaser Releaser => _releaser;

        internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation Operation =>
            _operation;

        internal NvencRunPublicationRecoveryIncompleteCleanupAttemptResult CleanupResult =>
            _operation.CleanupResult;

        internal NvencRunPublicationRecoveryIncompleteCleanupOperation CleanupOperation =>
            _operation.CleanupOperation;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease =>
            _operation.OwnershipLease;

        internal NvencRunPublicationRecoveryDecision Decision => _operation.Decision;

        internal NvencRunPublicationRecoveryInspectionSnapshot Snapshot => _operation.Snapshot;

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _operation.OpenOutcome;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
            _operation.LockIdentityEvidence;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal NvencRunPublicationRecoveryIncompleteCleanupStatus CleanupStatus =>
            _operation.CleanupStatus;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _releaser != null
                        && _operation != null
                        && _operation.IsBindingIntact
                        && _operation.OwnershipLease.IsReleaseComplete
                        && !_operation.CanRelease;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether this receipt is the evidence of that exact releaser
        /// performing that exact operation, and is still valid.
        /// </summary>
        internal bool IsIssuedFor(
            INvencRunPublicationRecoveryIncompleteOwnershipReleaser releaser,
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
        {
            try
            {
                return ReferenceEquals(_releaser, releaser)
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
