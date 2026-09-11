using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC publication recovery orphan cleanup
    /// execution coordinator. It holds exactly one
    /// <see cref="INvencRunPublicationRecoveryIncompleteCleaner"/>, calls it
    /// exactly once per valid operation, and returns the exact result it
    /// produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cleaner exception propagates by the same reference and is never
    /// caught, wrapped, or turned into a Failed result; the call is not
    /// repeated, and no result or receipt is ever fabricated here.
    /// </para>
    /// <para>
    /// The uninitialized default result is refused, and so is one that does not
    /// name this exact cleaner and this exact operation or is not a valid
    /// shape, with an <see cref="InvalidOperationException"/>. The status and
    /// receipt shape itself is judged only by the result's own
    /// <c>IsValid</c> - that single definition is not restated as a second set
    /// of branches here.
    /// </para>
    /// <para>
    /// This layer touches no filesystem, lock, process state, Registry,
    /// disposition, or Service, changes and releases nothing in the operation's
    /// authority graph, owns no thread, queue, or task, and is not an
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator
    {
        private readonly INvencRunPublicationRecoveryIncompleteCleaner _cleaner;

        internal NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(
            INvencRunPublicationRecoveryIncompleteCleaner cleaner)
        {
            _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
        }

        internal INvencRunPublicationRecoveryIncompleteCleaner Cleaner => _cleaner;

        internal NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Execute(
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result =
                _cleaner.Clean(operation);

            if (result.IsNone || !result.IsIssuedFor(_cleaner, operation))
            {
                throw new InvalidOperationException(
                    "Cleaner returned a default, foreign, or invalid orphan cleanup result.");
            }

            return result;
        }
    }
}
