using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous production Fresh NVENC Session Ownership Lease
    /// releaser: it validates that one attempt may start, delegates once to the
    /// operation's exact lease, and issues a receipt only after that attempt
    /// returned normally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The releaser holds no instance field and no mutable static state, owns
    /// no thread, queue, task, or wait primitive, touches no filesystem,
    /// Registry, disposition, or Publication Service, and is not an
    /// <see cref="IDisposable"/>. Whether an attempt may start is decided by
    /// the shared
    /// <see cref="NvencRunSessionOwnershipReleaseAdmission"/> predicate rather
    /// than re-derived here, so this releaser and the execution coordinator can
    /// never disagree: a first attempt needs the operation's admission
    /// validity and a fully retained lease, and a retry after a partial release
    /// failure needs only the reference correlation and a lease whose disposal
    /// has not completed.
    /// </para>
    /// <para>
    /// The lease's own disposal is the release. Its exception propagates
    /// unchanged - never caught, wrapped, or folded into a failed result - and
    /// a partial release is neither rolled back nor retried inside one call:
    /// the same operation is simply usable again on the next call, and the
    /// lease itself retries only the handles that failed. The lock is never
    /// re-acquired. A receipt is created exactly once, from the normal return
    /// path only; if that creation fails it is an invariant violation and
    /// propagates rather than being replaced by a fabricated receipt. No status
    /// value, attempt result, retry latch, proof, token, or nonce is
    /// introduced, and the final linearization against Poison belongs to the
    /// Run Coordinator integration rather than here.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunSessionOwnershipReleaser : INvencRunSessionOwnershipReleaser
    {
        public NvencRunSessionOwnershipReleaseReceipt Release(
            NvencRunSessionOwnershipReleaseOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!NvencRunSessionOwnershipReleaseAdmission.IsAdmissible(operation))
            {
                throw new ArgumentException(
                    "The operation cannot start a Session Ownership Lease release attempt.",
                    nameof(operation));
            }

            operation.OwnershipLease.Dispose();

            return NvencRunSessionOwnershipReleaseReceipt.Create(this, operation);
        }
    }
}
