using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous execution coordinator for the orphan cleanup's ownership
    /// release. It holds exactly one
    /// <see cref="INvencRunPublicationRecoveryIncompleteOwnershipReleaser"/>,
    /// calls it exactly once per admissible operation, and returns the exact
    /// receipt it produced.
    /// </summary>
    /// <remarks>
    /// Admission is the shared
    /// <see cref="NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission"/>,
    /// so this layer and the releaser can never disagree about whether an
    /// attempt or a retry is allowed. A releaser exception propagates unchanged
    /// - a partial release stays retryable through the same operation - and the
    /// call is never repeated. A null receipt, one whose lease was never
    /// released, and one issued for another releaser or another operation are
    /// rejected with <see cref="InvalidOperationException"/>. The cleaned and
    /// failed cleanup shapes are not told apart here. This layer touches no
    /// process state, Poison, filesystem, Registry, disposition, or Service,
    /// owns no thread, queue, or task, and is not an
    /// <see cref="IDisposable"/>.
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
    {
        private readonly INvencRunPublicationRecoveryIncompleteOwnershipReleaser _releaser;

        internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
            INvencRunPublicationRecoveryIncompleteOwnershipReleaser releaser)
        {
            _releaser = releaser ?? throw new ArgumentNullException(nameof(releaser));
        }

        internal INvencRunPublicationRecoveryIncompleteOwnershipReleaser Releaser => _releaser;

        internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Execute(
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission.IsAdmissible(
                    operation))
            {
                throw new ArgumentException(
                    "Operation must be admissible for a first release attempt or a retry.",
                    nameof(operation));
            }

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
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
