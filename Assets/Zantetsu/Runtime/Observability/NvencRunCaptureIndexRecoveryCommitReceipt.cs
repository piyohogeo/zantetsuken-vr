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
    /// Those two references are the whole state. The decision, snapshot,
    /// publication recovery decision, authoritative plan, commit mode, root
    /// layout, and Run identity are forwarded from the operation's graph rather
    /// than copied, and no canonical bytes, path, hash, handle, filesystem
    /// observation, or token is held.
    /// </para>
    /// <para>
    /// This is not a durable filesystem proof. It says only that a synchronous
    /// call made under the still-held OS lock returned successfully; a later
    /// question about what is on disk is answered by inspecting again, not by
    /// this receipt. It is minted only on success, through the success-only
    /// factory, which requires a committer, an operation, and that the
    /// operation is still valid.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, touches no file, releases
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

        internal NvencRunCaptureIndexRecoveryDecision CaptureIndexRecoveryDecision =>
            _operation.CaptureIndexRecoveryDecision;

        internal NvencRunCaptureIndexRecoveryInspectionSnapshot CaptureIndexRecoverySnapshot =>
            _operation.CaptureIndexRecoverySnapshot;

        internal NvencRunPublicationRecoveryDecision PublicationRecoveryDecision =>
            _operation.PublicationRecoveryDecision;

        internal CapturePublicationPlan AuthoritativePlan => _operation.AuthoritativePlan;

        internal CaptureRunCaptureIndexCommitMode CommitMode => _operation.CommitMode;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

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
