namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC recovery CaptureComplete boundary: one call
    /// is one attempt to accept that a recovered Run has reached an
    /// authoritative Capture Index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>null</c> operation throws
    /// <see cref="System.ArgumentNullException"/> and an invalid one throws
    /// <see cref="System.ArgumentException"/>, both before any side effect.
    /// Only a success returns a receipt; every failure leaves by an exception
    /// that propagates unchanged. There is no retry, fallback, re-inspection,
    /// re-commit, or guessed outcome inside a call.
    /// </para>
    /// <para>
    /// Completing here writes no new durable file. It accepts the state the
    /// operation already establishes - the final Capture Index is
    /// authoritative, whether the inspection found it so or a recovery commit
    /// made it so - and the difference between those two authorities stays
    /// inside the operation. An existing final index is never given an invented
    /// commit receipt, and a committed Run's receipt is never replaced.
    /// </para>
    /// <para>
    /// An implementation changes and releases no operation, decision, snapshot,
    /// commit receipt, plan, or OS lock, touches no file, hash, serialization,
    /// Registry, disposition, or Service, and owns no thread, queue, task,
    /// wait, or lease.
    /// </para>
    /// </remarks>
    internal interface INvencRunCaptureCompleteRecoveryCompleter
    {
        NvencRunCaptureCompleteRecoveryReceipt Complete(
            NvencRunCaptureCompleteRecoveryOperation operation);
    }
}
