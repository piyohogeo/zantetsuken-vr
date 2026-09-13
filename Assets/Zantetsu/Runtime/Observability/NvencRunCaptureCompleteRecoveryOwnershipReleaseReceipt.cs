using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable evidence that one Phase 0.11 NVENC recovery ownership release
    /// completed: the exact releaser that performed it and the exact operation
    /// it performed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receipt holds exactly two references, the exact releaser and the
    /// exact operation, and they are the whole state. What was cleaned up and
    /// released is read from <see cref="Operation"/>: the cleanup result and
    /// its operation, the lease, the open outcome, the lock identity evidence,
    /// the root layout, the Run identity, the cleanup status, and whether a
    /// commit receipt is behind it - none of it is restated as a field or
    /// property of the receipt's own. The plan, the snapshot, the commit
    /// receipt, and the raw lock handles likewise stay reachable through that
    /// graph rather than being surfaced here.
    /// </para>
    /// <para>
    /// A completed release necessarily makes the operation's admission
    /// validity false, so this receipt never asks for it.
    /// <see cref="IsValid"/> instead requires the binding to still hold, the
    /// exact lease to be fully released, and no further release to be possible
    /// - which is exactly what a finished release looks like.
    /// </para>
    /// <para>
    /// This type releases nothing, owns and disposes nothing, touches no file,
    /// and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject. Only <see cref="Released"/> issues one, and only after
    /// a successful release.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt
    {
        private readonly INvencRunCaptureCompleteRecoveryOwnershipReleaser _releaser;
        private readonly NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation _operation;

        private NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt(
            INvencRunCaptureCompleteRecoveryOwnershipReleaser releaser,
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation)
        {
            _releaser = releaser;
            _operation = operation;
        }

        /// <summary>
        /// Mints the evidence of one completed release. There is no failure
        /// receipt: a release that did not complete leaves by an exception and
        /// stays retryable.
        /// </summary>
        internal static NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt Released(
            INvencRunCaptureCompleteRecoveryOwnershipReleaser releaser,
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation)
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

            return new NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt(releaser, operation);
        }

        internal INvencRunCaptureCompleteRecoveryOwnershipReleaser Releaser => _releaser;

        internal NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation Operation => _operation;

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
            INvencRunCaptureCompleteRecoveryOwnershipReleaser releaser,
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation)
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
