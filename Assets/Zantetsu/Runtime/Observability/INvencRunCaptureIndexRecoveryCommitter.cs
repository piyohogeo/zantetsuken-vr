namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Capture Index recovery commit boundary: one
    /// call is one attempt to make the authoritative plan's canonical Capture
    /// Index the final <c>capture.index</c>, without overwriting anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>null</c> operation throws
    /// <see cref="System.ArgumentNullException"/> and an invalid one throws
    /// <see cref="System.ArgumentException"/>, both before any side effect.
    /// Only a known success returns a receipt; every failure leaves by an
    /// exception that propagates unchanged. There is no retry, poll, wait,
    /// re-inspection, rollback, or guessed outcome inside a call.
    /// </para>
    /// <para>
    /// Success means exactly this: following the operation's existing
    /// <see cref="CaptureRunCaptureIndexCommitMode"/>, the authoritative plan's
    /// canonical Capture Index now stands under the final name, established
    /// without overwriting an existing one.
    /// <see cref="CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit"/>
    /// re-confirms the temporary is absent and commits from a new one;
    /// <see cref="CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit"/>
    /// re-confirms no-follow that the existing temporary still holds those
    /// exact canonical bytes and commits it without rewriting;
    /// <see cref="CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit"/>
    /// re-confirms the existing temporary is still an ordinary, deletable,
    /// invalid file and replaces that exact temporary before committing.
    /// </para>
    /// <para>
    /// An existing final index, canonical bytes describing something else, a
    /// document past the limit, a reparse point, a file whose identity does not
    /// match what was observed, and anything that could not be observed at all
    /// are never deleted or overwritten. An exception after the rename is still
    /// an exception: no receipt is returned and no outcome is inferred on the
    /// spot. A caller must not blindly retry the same operation; under the OS
    /// lock it still holds, it re-inspects and decides again.
    /// </para>
    /// <para>
    /// The contract does not require <c>Flush(true)</c> or full durability. An
    /// implementation changes and releases no operation, decision, snapshot,
    /// plan, or OS lock, and owns no thread, queue, task, worker, or lease.
    /// </para>
    /// </remarks>
    internal interface INvencRunCaptureIndexRecoveryCommitter
    {
        NvencRunCaptureIndexRecoveryCommitReceipt Commit(
            NvencRunCaptureIndexRecoveryCommitOperation operation);
    }
}
