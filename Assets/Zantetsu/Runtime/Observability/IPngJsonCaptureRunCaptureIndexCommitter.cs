using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous, single-attempt side-effect boundary that commits a
    /// validated PngJson Capture Index commit operation to its final path and
    /// must never overwrite an existing destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Commit"/> throws <see cref="ArgumentNullException"/> when
    /// <paramref name="operation"/> or <paramref name="token"/> is <c>null</c>,
    /// and <see cref="ArgumentException"/> when the operation is not correlated
    /// with the supplied token through
    /// <see cref="PngJsonCaptureRunCaptureIndexCommitOperation.IsValidWithToken"/>.
    /// All such failures are filesystem-free and never produce a receipt.
    /// </para>
    /// <para>
    /// The committer must require
    /// <see cref="PngJsonCaptureRunCaptureIndexCommitOperation.IsValidWithToken"/>
    /// before any side effect, so canonical byte corruption is never missed; it
    /// must never re-validate the whole action plan and never re-issue a
    /// validation token, but must use the exact token passed by the
    /// coordinator.
    /// </para>
    /// <para>
    /// A receipt is returned only after the entire mode-specific sequence has
    /// succeeded, and the three modes are strictly separated by their handling
    /// of the temporary index.
    /// </para>
    /// <para>
    /// <c>CreateTemporaryAndCommit</c> re-confirms the final path is absent,
    /// writes the operation's canonical bytes in full to a new temporary index,
    /// and durably flushes the temporary data.
    /// </para>
    /// <para>
    /// <c>ReuseCanonicalTemporaryAndCommit</c> re-opens the existing temporary
    /// index no-follow as a regular file, re-confirms its identity, byte length,
    /// and canonical content match the operation, and durably flushes the
    /// temporary data, but never rewrites or deletes the temporary.
    /// </para>
    /// <para>
    /// <c>ReplaceInvalidTemporaryAndCommit</c> re-observes the existing
    /// temporary no-follow, confirms it is the exact removable,
    /// non-authoritative file, deletes only that exact temporary, durably
    /// flushes its parent directory metadata, then writes the operation's
    /// canonical bytes in full to a new temporary at the same path and durably
    /// flushes the temporary data.
    /// </para>
    /// <para>
    /// After the mode-specific work, every path re-confirms the final path is
    /// absent, performs the non-overwriting atomic rename from the temporary to
    /// the final path, durably flushes the final parent directory metadata, and
    /// confirms the temporary is gone. A mismatched canonical temporary, a
    /// limit-exceeded temporary, a reparse point, an identity mismatch, or an
    /// existing final path is a hard failure and is never deleted or
    /// overwritten.
    /// </para>
    /// <para>
    /// This call is synchronous and single-attempt: it performs no retry, no
    /// rollback, no cleanup, no re-inspection, and no alternative-path
    /// fallback. The committer must not mutate or dispose the operation, action
    /// plan, authoritative plan, canonical bytes, or owner; must not retain the
    /// operation or any input-derived bytes beyond the returned receipt; and
    /// holds no responsibility for releasing the lock owner. The returned
    /// receipt holds no copy of the canonical bytes, no handle, and no content
    /// digest. An exception raised by the implementation propagates unchanged
    /// and never produces a receipt.
    /// </para>
    /// <para>
    /// This interface neither inherits nor changes the existing
    /// <see cref="ICaptureRunCaptureIndexCommitter"/> boundary, and the
    /// durability, non-overwrite, no-follow, and post-failure re-inspection
    /// guarantees are preserved here against the PngJson operation types.
    /// </para>
    /// </remarks>
    internal interface IPngJsonCaptureRunCaptureIndexCommitter
    {
        PngJsonCaptureRunCaptureIndexCommitReceipt Commit(
            PngJsonCaptureRunCaptureIndexCommitOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
    }
}
