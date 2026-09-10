using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous production Phase 0.11 NVENC recovery
    /// CaptureComplete completer: it accepts the state the operation already
    /// proves - the Run's final Capture Index is authoritative - and returns
    /// the receipt of that acceptance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately not a persistence step. Nothing is written, read,
    /// hashed, serialized, or re-inspected here, and no Registry, disposition,
    /// Service, or process state is touched: the durable work happened in the
    /// publication recovery and the Capture Index commit, and the operation
    /// carries their evidence.
    /// </para>
    /// <para>
    /// The two arrival paths are not branched on. Whether the inspection
    /// already found the final index authoritative or a recovery commit made it
    /// so is held by the operation, so nothing here re-inspects, re-commits, or
    /// re-issues a commit receipt. There is no failure status, attempt result,
    /// or retry latch: a null operation throws
    /// <see cref="ArgumentNullException"/>, an invalid one throws
    /// <see cref="ArgumentException"/>, and any other exception - the receipt
    /// factory's included - propagates unchanged.
    /// </para>
    /// <para>
    /// The type holds no instance field and no mutable static state, owns no
    /// thread, queue, task, wait, lease, handle, or buffer, releases no lock,
    /// and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryCompleter
        : INvencRunCaptureCompleteRecoveryCompleter
    {
        public NvencRunCaptureCompleteRecoveryReceipt Complete(
            NvencRunCaptureCompleteRecoveryOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            return NvencRunCaptureCompleteRecoveryReceipt.Completed(this, operation);
        }
    }
}
