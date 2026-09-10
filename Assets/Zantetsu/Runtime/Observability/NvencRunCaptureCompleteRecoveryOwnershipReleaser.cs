using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous production Phase 0.11 NVENC recovery ownership
    /// releaser: it releases the exact Session Ownership Lease its operation
    /// carries, once per call, and returns the receipt of that completed
    /// release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Admission is the shared
    /// <see cref="NvencRunCaptureCompleteRecoveryOwnershipReleaseAdmission"/>,
    /// so this releaser cannot disagree with its execution coordinator about
    /// whether a first attempt or a retry is allowed; there is no separate
    /// judgement here, and no retry counter, latch, status, attempt result,
    /// proof, token, or nonce.
    /// </para>
    /// <para>
    /// A disposal exception is neither caught, wrapped, nor turned into a
    /// failure result: it propagates by the same reference, no receipt is
    /// issued, and the partially released lease stays retryable through the
    /// same operation on a later call. Nothing is retried inside one call,
    /// rolled back, re-acquired, or released on any other lease. If the
    /// receipt's own issuance fails after a completed release, that exception
    /// propagates too - the release having happened is not a licence to
    /// fabricate a different receipt or a success.
    /// </para>
    /// <para>
    /// The type holds no instance field and no mutable static state, touches no
    /// file, Registry, disposition, Service, or process state, owns no thread,
    /// queue, task, or wait primitive, and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryOwnershipReleaser
        : INvencRunCaptureCompleteRecoveryOwnershipReleaser
    {
        public NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt Release(
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!NvencRunCaptureCompleteRecoveryOwnershipReleaseAdmission.IsAdmissible(operation))
            {
                throw new ArgumentException(
                    "Operation must be admissible for a first release attempt or a retry.",
                    nameof(operation));
            }

            // The lease's own disposal, exactly once. A partial failure leaves
            // by this call's exception with no receipt.
            operation.OwnershipLease.Dispose();

            // A returned disposal that did not actually finish the release is
            // not a success to attest.
            if (!operation.OwnershipLease.IsReleaseComplete || operation.CanRelease)
            {
                throw new InvalidOperationException(
                    "The ownership lease disposal returned without completing the release.");
            }

            return NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt.Released(this, operation);
        }
    }
}
