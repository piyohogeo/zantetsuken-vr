using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC publication recovery inspection execution
    /// coordinator. It holds exactly one
    /// <see cref="INvencRunPublicationRecoveryInspector"/>, calls it exactly
    /// once per valid operation, and verifies the returned snapshot. It owns no
    /// thread or queue, performs no file, hash, thread, or task operation, and
    /// is not an <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// An inspector exception propagates unchanged; the coordinator never
    /// repeats the call, never fabricates a snapshot, and never converts a
    /// failure into an observation. A null snapshot, one that is invalid, or
    /// one that describes another operation is rejected with
    /// <see cref="InvalidOperationException"/>. This layer touches no process
    /// state, Poison, Registry, disposition, cleanup, or lock, and adds no
    /// issuer receipt, token, or nonce: the exact configured inspector and the
    /// exact operation correlation are the whole authority.
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryInspectionExecutionCoordinator
    {
        private readonly INvencRunPublicationRecoveryInspector _inspector;

        internal NvencRunPublicationRecoveryInspectionExecutionCoordinator(
            INvencRunPublicationRecoveryInspector inspector)
        {
            _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        }

        internal INvencRunPublicationRecoveryInspector Inspector => _inspector;

        internal NvencRunPublicationRecoveryInspectionSnapshot Execute(
            NvencRunPublicationRecoveryInspectionOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = _inspector.Inspect(operation);

            if (snapshot == null
                || !ReferenceEquals(snapshot.Operation, operation)
                || !snapshot.IsValid)
            {
                throw new InvalidOperationException(
                    "Inspector returned a null, foreign, or invalid inspection snapshot.");
            }

            return snapshot;
        }
    }
}
