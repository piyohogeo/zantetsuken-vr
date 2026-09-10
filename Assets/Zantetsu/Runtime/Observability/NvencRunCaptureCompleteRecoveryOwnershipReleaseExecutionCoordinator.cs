using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC recovery ownership release execution
    /// coordinator. It holds exactly one
    /// <see cref="INvencRunCaptureCompleteRecoveryOwnershipReleaser"/>, calls
    /// it exactly once per admissible operation, and returns the exact receipt
    /// it produced.
    /// </summary>
    /// <remarks>
    /// Admission is the shared
    /// <see cref="NvencRunCaptureCompleteRecoveryOwnershipReleaseAdmission"/>,
    /// so this layer and the releaser can never disagree about whether an
    /// attempt or a retry is allowed. A releaser exception propagates
    /// unchanged - a partial release stays retryable through the same
    /// operation - and the call is never repeated. A null receipt, one that is
    /// invalid, and one issued for another releaser or another operation are
    /// rejected with <see cref="InvalidOperationException"/>. This layer
    /// touches no process state, Poison, filesystem, Registry, disposition, or
    /// Service, owns no thread, queue, or task, and is not an
    /// <see cref="IDisposable"/>.
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator
    {
        private readonly INvencRunCaptureCompleteRecoveryOwnershipReleaser _releaser;

        internal NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator(
            INvencRunCaptureCompleteRecoveryOwnershipReleaser releaser)
        {
            _releaser = releaser ?? throw new ArgumentNullException(nameof(releaser));
        }

        internal INvencRunCaptureCompleteRecoveryOwnershipReleaser Releaser => _releaser;

        internal NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt Execute(
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

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt =
                _releaser.Release(operation);

            if (receipt == null || !receipt.IsIssuedFor(_releaser, operation))
            {
                throw new InvalidOperationException(
                    "Releaser returned a null, foreign, or unreleased ownership release receipt.");
            }

            return receipt;
        }
    }
}
