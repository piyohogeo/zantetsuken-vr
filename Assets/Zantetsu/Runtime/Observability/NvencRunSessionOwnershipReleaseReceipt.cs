using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run Session Ownership Lease release receipt:
    /// the exact releaser and the exact release operation whose lease is known
    /// to have been fully released in one synchronous call. It is issued only
    /// from a successful release and has no public constructor.
    /// </summary>
    /// <remarks>
    /// There is no failed receipt and no release status: a failed attempt
    /// propagates its own exception and may leave a partial release behind, so
    /// folding it into a single word would hide the state the caller has to
    /// read from the lease itself.
    /// <para>
    /// Validity deliberately does not use the operation's admission validity,
    /// which a completed release necessarily makes false. It rests on the
    /// operation's reference correlation plus the exact lease's own completion:
    /// the release is complete and no further attempt is possible. The cleanup
    /// result, the root layout, and the run identity are forwarded from the
    /// operation and never duplicated as fields. No raw lock handle is exposed
    /// and no proof, token, or nonce is introduced.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunSessionOwnershipReleaseReceipt
    {
        private readonly INvencRunSessionOwnershipReleaser _releaser;
        private readonly NvencRunSessionOwnershipReleaseOperation _operation;

        private NvencRunSessionOwnershipReleaseReceipt(
            INvencRunSessionOwnershipReleaser releaser,
            NvencRunSessionOwnershipReleaseOperation operation)
        {
            _releaser = releaser;
            _operation = operation;
        }

        /// <summary>
        /// Validated factory: only a completed release of the operation's exact
        /// lease may issue a receipt.
        /// </summary>
        internal static NvencRunSessionOwnershipReleaseReceipt Create(
            INvencRunSessionOwnershipReleaser releaser,
            NvencRunSessionOwnershipReleaseOperation operation)
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
                    "The operation no longer correlates.", nameof(operation));
            }

            if (!operation.OwnershipLease.IsReleaseComplete || operation.CanRelease)
            {
                throw new ArgumentException(
                    "The Session Ownership Lease release is not complete.", nameof(operation));
            }

            return new NvencRunSessionOwnershipReleaseReceipt(releaser, operation);
        }

        internal INvencRunSessionOwnershipReleaser Releaser => _releaser;

        internal NvencRunSessionOwnershipReleaseOperation Operation => _operation;

        internal NvencRunCaptureCompleteCleanupAttemptResult CleanupResult => _operation.CleanupResult;

        internal NvencRunCaptureCompleteCleanupOperation CleanupOperation => _operation.CleanupOperation;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsValid =>
            _releaser != null
            && _operation != null
            && _operation.IsBindingIntact
            && _operation.OwnershipLease.IsReleaseComplete
            && !_operation.CanRelease;

        internal bool IsIssuedFor(
            INvencRunSessionOwnershipReleaser releaser,
            NvencRunSessionOwnershipReleaseOperation operation)
        {
            return releaser != null
                && operation != null
                && ReferenceEquals(_releaser, releaser)
                && ReferenceEquals(_operation, operation)
                && IsValid;
        }
    }
}
