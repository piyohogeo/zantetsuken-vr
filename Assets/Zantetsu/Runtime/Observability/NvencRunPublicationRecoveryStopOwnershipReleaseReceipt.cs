using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable evidence that one stopping publication recovery released its
    /// Run's Session Ownership Lease: the exact releaser that performed it and
    /// the exact operation it performed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those two references are the whole state; the decision, its snapshot,
    /// the lease, the open outcome, the lock identity evidence, the stopping
    /// disposition, the root layout, and the Run identity are forwarded from
    /// the operation's graph. The plan and the observation details stay where
    /// they are, reachable through that graph.
    /// </para>
    /// <para>
    /// A completed release makes the operation's admission validity - and the
    /// decision's own validity - false, so this receipt asks for neither.
    /// <see cref="IsValid"/> requires the binding to still hold, the exact
    /// lease to be fully released, and no further release to be possible, which
    /// is exactly what a finished release looks like.
    /// </para>
    /// <para>
    /// This type releases nothing, owns and disposes nothing, touches no file,
    /// and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject. It is minted only after a successful release, through
    /// the success-only factory.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryStopOwnershipReleaseReceipt
    {
        private readonly INvencRunPublicationRecoveryStopOwnershipReleaser _releaser;
        private readonly NvencRunPublicationRecoveryStopOwnershipReleaseOperation _operation;

        private NvencRunPublicationRecoveryStopOwnershipReleaseReceipt(
            INvencRunPublicationRecoveryStopOwnershipReleaser releaser,
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
        {
            _releaser = releaser;
            _operation = operation;
        }

        /// <summary>
        /// Mints the evidence of one completed release. There is no failure
        /// receipt: a release that did not complete leaves by an exception and
        /// stays retryable.
        /// </summary>
        internal static NvencRunPublicationRecoveryStopOwnershipReleaseReceipt Released(
            INvencRunPublicationRecoveryStopOwnershipReleaser releaser,
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
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

            return new NvencRunPublicationRecoveryStopOwnershipReleaseReceipt(releaser, operation);
        }

        internal INvencRunPublicationRecoveryStopOwnershipReleaser Releaser => _releaser;

        internal NvencRunPublicationRecoveryStopOwnershipReleaseOperation Operation => _operation;

        internal NvencRunPublicationRecoveryDecision Decision => _operation.Decision;

        internal NvencRunPublicationRecoveryInspectionSnapshot Snapshot => _operation.Snapshot;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease =>
            _operation.OwnershipLease;

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _operation.OpenOutcome;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
            _operation.LockIdentityEvidence;

        internal NvencRunPublicationRecoveryDisposition Disposition => _operation.Disposition;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

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
            INvencRunPublicationRecoveryStopOwnershipReleaser releaser,
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
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
