using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Capture Index recovery inspection execution
    /// coordinator. It holds exactly one
    /// <see cref="INvencRunCaptureIndexRecoveryInspector"/>, calls it exactly
    /// once per valid operation, and returns the exact snapshot it produced.
    /// </summary>
    /// <remarks>
    /// An inspector exception propagates unchanged; the coordinator never
    /// repeats the call, never fabricates a snapshot, and never converts a
    /// failure into an observation. A null snapshot, one that is invalid, or
    /// one that describes another operation is rejected with
    /// <see cref="InvalidOperationException"/>. The snapshot is handed back by
    /// reference rather than wrapped: a stateless coordinator cannot prove
    /// after the fact that an observation came from it, so no issuer receipt,
    /// token, or result type claims it. This layer classifies nothing, touches
    /// no file, process state, disposition, or lock, and is not an
    /// <see cref="IDisposable"/>.
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator
    {
        private readonly INvencRunCaptureIndexRecoveryInspector _inspector;

        internal NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(
            INvencRunCaptureIndexRecoveryInspector inspector)
        {
            _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        }

        internal INvencRunCaptureIndexRecoveryInspector Inspector => _inspector;

        internal NvencRunCaptureIndexRecoveryInspectionSnapshot Execute(
            NvencRunCaptureIndexRecoveryInspectionOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot = _inspector.Inspect(operation);

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
