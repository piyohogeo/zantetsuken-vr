using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Run artifact publication execution
    /// coordinator. It holds exactly one
    /// <see cref="INvencRunArtifactPublisher"/>, calls it exactly once per
    /// valid operation, and verifies the returned attempt result. It owns no
    /// thread or queue, performs no file, hash, thread, or task operation, and
    /// is not an <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// A publisher exception propagates unchanged; the coordinator never
    /// repeats the publish, never guesses another status, and never issues a
    /// result after a failure. A null, invalid, foreign, default, or corrupt
    /// attempt result is rejected with <see cref="InvalidOperationException"/>.
    /// This layer does not poison the process; the future Publication Service
    /// and <see cref="NvencCaptureRunCoordinator"/> own thread and fatal
    /// policy.
    /// </remarks>
    internal sealed class NvencRunArtifactPublicationExecutionCoordinator
    {
        private readonly INvencRunArtifactPublisher _publisher;

        internal NvencRunArtifactPublicationExecutionCoordinator(
            INvencRunArtifactPublisher publisher)
        {
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        }

        internal INvencRunArtifactPublisher Publisher => _publisher;

        internal NvencRunArtifactPublicationAttemptResult Execute(
            NvencRunArtifactPublicationOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunArtifactPublicationAttemptResult attempt = _publisher.Publish(operation);

            if (!attempt.IsIssuedFor(_publisher, operation))
            {
                throw new InvalidOperationException(
                    "Publisher returned a null, foreign, default, or corrupt attempt result.");
            }

            return attempt;
        }
    }
}
