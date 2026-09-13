using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable evidence that one Phase 0.11 NVENC Capture Index recovery
    /// commit is known to have succeeded: the exact committer that performed it
    /// and the exact operation it performed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receipt holds exactly two readonly references, the exact committer
    /// and the exact operation, and they are the whole state. The decision, its
    /// snapshot, the publication recovery decision, the authoritative plan, the
    /// commit mode, the root layout, and the Run identity are read from
    /// <see cref="Operation"/>: the receipt restates none of it and duplicates
    /// none of it as a field of its own. No canonical bytes, path, hash,
    /// handle, filesystem observation, or token is held either.
    /// </para>
    /// <para>
    /// This is process-local evidence of one successful synchronous commit
    /// call and nothing more: not a durable filesystem proof and not proof that
    /// the OS lock was released. It says only that a call made under the
    /// still-held lock returned successfully; a later question about what is on
    /// disk is answered by inspecting again, not by this receipt. It is minted only on success, through the success-only
    /// factory, which requires a committer, an operation, and that the
    /// operation is still valid.
    /// </para>
    /// <para>
    /// This type does not own, change, or dispose the operation graph's
    /// lifetime, touches no file, releases
    /// no lock, and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject. <see cref="IsValid"/> never throws, so once the OS
    /// lock is released the operation becomes invalid and this receipt follows
    /// it.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryCommitReceipt
    {
        private readonly INvencRunCaptureIndexRecoveryCommitter _committer;
        private readonly NvencRunCaptureIndexRecoveryCommitOperation _operation;

        private NvencRunCaptureIndexRecoveryCommitReceipt(
            INvencRunCaptureIndexRecoveryCommitter committer,
            NvencRunCaptureIndexRecoveryCommitOperation operation)
        {
            _committer = committer;
            _operation = operation;
        }

        /// <summary>
        /// Mints the evidence of one known success. There is no failure or
        /// unknown-outcome receipt: a commit that did not succeed leaves by an
        /// exception.
        /// </summary>
        internal static NvencRunCaptureIndexRecoveryCommitReceipt Committed(
            INvencRunCaptureIndexRecoveryCommitter committer,
            NvencRunCaptureIndexRecoveryCommitOperation operation)
        {
            if (committer == null)
            {
                throw new ArgumentNullException(nameof(committer));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            return new NvencRunCaptureIndexRecoveryCommitReceipt(committer, operation);
        }

        internal INvencRunCaptureIndexRecoveryCommitter Committer => _committer;

        internal NvencRunCaptureIndexRecoveryCommitOperation Operation => _operation;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _committer != null && _operation != null && _operation.IsValid;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether this receipt is the evidence of that exact committer
        /// performing that exact operation, and is still valid.
        /// </summary>
        internal bool IsIssuedFor(
            INvencRunCaptureIndexRecoveryCommitter committer,
            NvencRunCaptureIndexRecoveryCommitOperation operation)
        {
            try
            {
                return ReferenceEquals(_committer, committer)
                    && ReferenceEquals(_operation, operation)
                    && IsValid;
            }
            catch
            {
                return false;
            }
        }
    }
}
