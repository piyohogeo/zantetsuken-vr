using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable ownership-reconciliation result fixed at the moment the
    /// terminal intent queue is finally drained after producer join.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This snapshot proves, at the point of issue, that the queue is empty, the
    /// queue owns no private buffer, the caller reports no producer-retained
    /// private buffer, and every accepted intent has been processed. A matching
    /// <see cref="RunAcceptedIntentCount"/> and <see cref="RunProcessedIntentCount"/>
    /// alone is not sufficient: an empty queue, a zero queue-owned private buffer
    /// count, and a zero producer-retained private buffer count are also required.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> is derived from the held values; no independent
    /// validity flag is stored.
    /// </para>
    /// <para>
    /// The only canonical issuer is <see cref="CaptureFrameDraftTerminalIntentQueue.CreateOwnershipSnapshot"/>,
    /// which passes itself as <see cref="IssuedBy"/> after all preconditions hold.
    /// The internal constructor is not a public issue path and must only be used
    /// by that queue (or by tests that exercise the constructor contract directly).
    /// </para>
    /// <para>
    /// A snapshot must be created only before any remaining pending draft is
    /// moved to the freeze-drain terminal reason; it does not prove the draft
    /// registry state, the forced-drop set, the trace queue, or the logger seal.
    /// It owns and disposes no queue, entry, or intent, holds no intent, entry,
    /// registry, logger, native container, array, or mutable collection, and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class TerminalIntentOwnershipSnapshot
    {
        private readonly CaptureFrameDraftTerminalIntentQueue _issuedBy;
        private readonly long _testRunId;
        private readonly int _queueCount;
        private readonly int _runAcceptedIntentCount;
        private readonly int _runProcessedIntentCount;
        private readonly int _queueOwnedPrivateBufferCount;
        private readonly int _producerRetainedPrivateBufferCount;

        internal TerminalIntentOwnershipSnapshot(
            CaptureFrameDraftTerminalIntentQueue issuedBy,
            long testRunId,
            int queueCount,
            int runAcceptedIntentCount,
            int runProcessedIntentCount,
            int queueOwnedPrivateBufferCount,
            int producerRetainedPrivateBufferCount)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testRunId), testRunId, "Test run ID must be greater than zero.");
            }

            if (queueCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(queueCount), queueCount, "Queue count must not be negative.");
            }

            if (runAcceptedIntentCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(runAcceptedIntentCount), runAcceptedIntentCount, "Accepted intent count must not be negative.");
            }

            if (runProcessedIntentCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(runProcessedIntentCount), runProcessedIntentCount, "Processed intent count must not be negative.");
            }

            if (queueOwnedPrivateBufferCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(queueOwnedPrivateBufferCount), queueOwnedPrivateBufferCount, "Queue-owned private buffer count must not be negative.");
            }

            if (producerRetainedPrivateBufferCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(producerRetainedPrivateBufferCount), producerRetainedPrivateBufferCount, "Producer retained private buffer count must not be negative.");
            }

            if (queueCount != 0)
            {
                throw new ArgumentException("Queue count must be zero.", nameof(queueCount));
            }

            if (runAcceptedIntentCount != runProcessedIntentCount)
            {
                throw new ArgumentException("Accepted and processed intent counts must match.", nameof(runProcessedIntentCount));
            }

            if (queueOwnedPrivateBufferCount != 0)
            {
                throw new ArgumentException("Queue-owned private buffer count must be zero.", nameof(queueOwnedPrivateBufferCount));
            }

            if (producerRetainedPrivateBufferCount != 0)
            {
                throw new ArgumentException("Producer retained private buffer count must be zero.", nameof(producerRetainedPrivateBufferCount));
            }

            _issuedBy = issuedBy;
            _testRunId = testRunId;
            _queueCount = queueCount;
            _runAcceptedIntentCount = runAcceptedIntentCount;
            _runProcessedIntentCount = runProcessedIntentCount;
            _queueOwnedPrivateBufferCount = queueOwnedPrivateBufferCount;
            _producerRetainedPrivateBufferCount = producerRetainedPrivateBufferCount;
        }

        public long TestRunId => _testRunId;

        public int QueueCount => _queueCount;

        public int RunAcceptedIntentCount => _runAcceptedIntentCount;

        public int RunProcessedIntentCount => _runProcessedIntentCount;

        public int QueueOwnedPrivateBufferCount => _queueOwnedPrivateBufferCount;

        public int ProducerRetainedPrivateBufferCount => _producerRetainedPrivateBufferCount;

        internal CaptureFrameDraftTerminalIntentQueue IssuedBy => _issuedBy;

        public bool IsValid =>
            TestRunId > 0
            && QueueCount == 0
            && RunAcceptedIntentCount >= 0
            && RunProcessedIntentCount >= 0
            && RunAcceptedIntentCount == RunProcessedIntentCount
            && QueueOwnedPrivateBufferCount == 0
            && ProducerRetainedPrivateBufferCount == 0
            && IssuedBy != null;
    }
}
