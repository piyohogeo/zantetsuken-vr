using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Run Session Ownership Lease release
    /// execution coordinator. It holds exactly one
    /// <see cref="INvencRunSessionOwnershipReleaser"/>, calls it exactly once
    /// per admissible operation, and verifies the returned receipt. It owns no
    /// thread or queue, performs no file, hash, thread, or task operation, and
    /// is not an <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// A releaser exception propagates unchanged; the coordinator never repeats
    /// the call, never issues a receipt of its own, and never converts a
    /// partial release into a result. A null, foreign, or not-yet-released
    /// receipt is rejected with <see cref="InvalidOperationException"/>. This
    /// layer touches no process state, gate, Poison, Registry, disposition,
    /// Publication Service, cleanup result, or file, and the final
    /// linearization of Poison against a retry belongs to the later Run
    /// Coordinator integration rather than here.
    /// </remarks>
    internal sealed class NvencRunSessionOwnershipReleaseExecutionCoordinator
    {
        private readonly INvencRunSessionOwnershipReleaser _releaser;

        internal NvencRunSessionOwnershipReleaseExecutionCoordinator(
            INvencRunSessionOwnershipReleaser releaser)
        {
            _releaser = releaser ?? throw new ArgumentNullException(nameof(releaser));
        }

        internal INvencRunSessionOwnershipReleaser Releaser => _releaser;

        internal NvencRunSessionOwnershipReleaseReceipt Execute(
            NvencRunSessionOwnershipReleaseOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            // A first attempt or a retry after a partial release failure; a
            // completed release, a broken binding, and a first attempt whose
            // admission validity is gone are all refused before the releaser is
            // contacted.
            if (!NvencRunSessionOwnershipReleaseAdmission.IsAdmissible(operation))
            {
                throw new ArgumentException(
                    "The operation cannot start a Session Ownership Lease release attempt.",
                    nameof(operation));
            }

            NvencRunSessionOwnershipReleaseReceipt receipt = _releaser.Release(operation);

            if (receipt == null
                || !ReferenceEquals(receipt.Releaser, _releaser)
                || !ReferenceEquals(receipt.Operation, operation)
                || !receipt.IsIssuedFor(_releaser, operation))
            {
                throw new InvalidOperationException(
                    "Releaser returned a null, foreign, or not-yet-released receipt.");
            }

            return receipt;
        }
    }
}
