using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run capture index commit receipt: the exact
    /// committer and the exact operation whose capture index commit is known to
    /// have succeeded in one synchronous commit call. It is issued only from
    /// the <see cref="NvencRunCaptureIndexCommitAttemptResult.Committed"/> path
    /// and has no public constructor.
    /// </summary>
    /// <remarks>
    /// This type holds only the exact committer and the exact operation,
    /// performs no filesystem, hash, or serialization work, and is not an
    /// <see cref="IDisposable"/>. The publication receipt, plan, root layout,
    /// and run identity are forwarded from the operation and never duplicated
    /// as fields, and no new proof, token, nonce, canonical byte copy, resolved
    /// path, or filesystem handle is introduced. A receipt is process-local
    /// evidence that one synchronous commit call succeeded; it is not a
    /// persistent filesystem snapshot and not CaptureComplete evidence.
    /// </remarks>
    internal sealed class NvencRunCaptureIndexCommitReceipt
    {
        private readonly INvencRunCaptureIndexCommitter _committer;
        private readonly NvencRunCaptureIndexCommitOperation _operation;

        private NvencRunCaptureIndexCommitReceipt(
            INvencRunCaptureIndexCommitter committer,
            NvencRunCaptureIndexCommitOperation operation)
        {
            _committer = committer;
            _operation = operation;
        }

        internal static NvencRunCaptureIndexCommitReceipt Create(
            INvencRunCaptureIndexCommitter committer,
            NvencRunCaptureIndexCommitOperation operation)
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
                throw new ArgumentException(
                    "The operation is no longer valid.", nameof(operation));
            }

            return new NvencRunCaptureIndexCommitReceipt(committer, operation);
        }

        internal INvencRunCaptureIndexCommitter Committer => _committer;

        internal NvencRunCaptureIndexCommitOperation Operation => _operation;

        internal NvencRunArtifactPublicationReceipt ArtifactPublicationReceipt =>
            _operation.ArtifactPublicationReceipt;

        internal NvencRunArtifactPublicationOperation ArtifactPublicationOperation =>
            _operation.ArtifactPublicationOperation;

        internal CapturePublicationPlan Plan => _operation.Plan;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsValid =>
            _committer != null
            && _operation != null
            && _operation.IsBindingIntact;

        internal bool IsIssuedFor(
            INvencRunCaptureIndexCommitter committer,
            NvencRunCaptureIndexCommitOperation operation)
        {
            return committer != null
                && operation != null
                && ReferenceEquals(_committer, committer)
                && ReferenceEquals(_operation, operation)
                && IsValid;
        }
    }
}
