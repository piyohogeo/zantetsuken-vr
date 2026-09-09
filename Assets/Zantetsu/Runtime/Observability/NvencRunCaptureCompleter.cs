using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous concrete Fresh NVENC CaptureComplete completer:
    /// it turns one already-verified CaptureComplete operation into one
    /// Completed attempt result and does nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By the time CaptureComplete runs, the artifact publication receipt and
    /// the capture index commit receipt are already settled, and the operation
    /// itself re-checks that whole graph. There is nothing left to prove, so
    /// this completer adds no verification, no evidence, and no side effect: it
    /// performs no filesystem, hash, serialization, cleanup, or notification
    /// work, never re-hashes or re-inspects the published artifact, and never
    /// copies a value out of the operation's graph into state of its own.
    /// </para>
    /// <para>
    /// It holds no instance field and no mutable static state, is not an
    /// <see cref="IDisposable"/>, and owns no thread, queue, task, wait, lease,
    /// handle, or buffer, so one instance is safely shared.
    /// </para>
    /// <para>
    /// After the argument checks there is no step that can legitimately fail,
    /// so this completer never fabricates a
    /// <see cref="NvencRunCaptureCompleteStatus.Failed"/> result. An unexpected
    /// exception from the result or receipt factory is a broken invariant, not
    /// an outcome: it propagates unchanged rather than being converted into a
    /// Failed result, and the Publication Service treats it as fatal.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleter : INvencRunCaptureCompleter
    {
        public NvencRunCaptureCompleteAttemptResult Complete(
            NvencRunCaptureCompleteOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            // The existing factory mints the exact receipt for this exact
            // completer and operation; no value is duplicated here.
            return NvencRunCaptureCompleteAttemptResult.Completed(this, operation);
        }
    }
}
