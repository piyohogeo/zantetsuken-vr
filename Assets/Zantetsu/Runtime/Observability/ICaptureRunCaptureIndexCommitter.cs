using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous, single-attempt side-effect boundary that commits a
    /// validated Capture Index commit operation to its final path and must
    /// never overwrite an existing destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Commit"/> throws <see cref="ArgumentNullException"/> when
    /// <paramref name="operation"/> is <c>null</c> and
    /// <see cref="ArgumentException"/> when it is not
    /// <see cref="CaptureRunCaptureIndexCommitOperation.IsValid"/>. Both
    /// failures are filesystem-free and never produce a receipt.
    /// </para>
    /// <para>
    /// A receipt is returned only after the entire mode-specific sequence
    /// below has succeeded. A backend that cannot honor these guarantees, for
    /// example when the staging and final roots reside on different volumes,
    /// must not return a receipt.
    /// </para>
    /// <para>
    /// A returned receipt is never null, must satisfy
    /// <c>ReferenceEquals(receipt.IssuedBy, this)</c> and
    /// <c>ReferenceEquals(receipt.Operation, operation)</c>, and therefore
    /// <c>receipt.IsIssuedFor(this, operation)</c> must be <c>true</c>. The
    /// next coordinator must fail closed immediately for a null receipt, a
    /// receipt issued by a foreign committer, or a receipt for a different
    /// operation.
    /// </para>
    /// <para>
    /// CreateTemporaryAndCommit: re-confirm no-follow that
    /// <c>capture.index</c> is absent and that <c>capture.index.tmp</c> is
    /// absent; create the tmp non-overwriting; write the operation's canonical
    /// bytes in full; durably flush the file data; confirm by handle that the
    /// tmp byte length and content match the operation; perform the
    /// non-overwriting atomic rename from tmp to final; durably flush the final
    /// parent directory metadata; confirm no authoritative artifact remains at
    /// the tmp path; then return the receipt.
    /// </para>
    /// <para>
    /// ReuseCanonicalTemporaryAndCommit: re-confirm the final path is absent;
    /// open the existing tmp no-follow as a regular file; re-confirm its
    /// identity, byte length, and canonical content match the operation;
    /// durably flush the tmp data; perform the non-overwriting atomic rename
    /// from tmp to final; durably flush the parent directory metadata; confirm
    /// the tmp is gone; then return the receipt. The tmp is never rewritten
    /// and no alternate tmp is produced.
    /// </para>
    /// <para>
    /// ReplaceInvalidTemporaryAndCommit: re-confirm the final path is absent;
    /// re-observe the existing tmp no-follow and confirm it is a removable,
    /// non-authoritative file; delete only that exact tmp; durably flush the
    /// parent directory metadata; create the same fixed tmp path
    /// non-overwriting; write the canonical bytes in full; durably flush the
    /// file data; confirm by handle that the byte length and content match;
    /// perform the non-overwriting atomic rename from tmp to final; durably
    /// flush the parent directory metadata; confirm the tmp is gone; then
    /// return the receipt. A canonical tmp that disagrees with the
    /// authoritative plan, a limit-exceeded tmp, a reparse point, an identity
    /// mismatch, or an existing final path is a hard failure and is never
    /// deleted or overwritten.
    /// </para>
    /// <para>
    /// This call is synchronous and single-attempt: it performs no retry, no
    /// fallback, and no alternate-path commit. The committer must not mutate
    /// or dispose the operation, action plan, authoritative plan, or canonical
    /// bytes; must not retain the operation or any input-derived bytes in its
    /// own fields, queues, or caches (transient references and defensive
    /// copies during the call are allowed); and after the call may retain only
    /// the returned receipt's operation reference. The receipt holds no copy
    /// of the canonical bytes, no file handle, and no content digest.
    /// </para>
    /// <para>
    /// The committer must not contact the run registration, draft, or trace
    /// stores, Unity Objects, or notification paths, and leaves the choice of
    /// execution thread to the upper coordinator. If an exception occurs after
    /// the final file becomes visible, for example a flush failure after the
    /// rename, the final file may exist, so blindly retrying the same
    /// operation is forbidden and re-inspection under the held lock is
    /// mandatory.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunCaptureIndexCommitter
    {
        CaptureRunCaptureIndexCommitReceipt Commit(
            CaptureRunCaptureIndexCommitOperation operation);
    }
}
