using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Readonly value result of one NVENC Run capture index commit attempt: the
    /// exact committer, the exact operation, and a status with a receipt held
    /// only for the <see cref="NvencRunCaptureIndexCommitStatus.Committed"/>
    /// shape. The two terminal shapes are mutually exclusive;
    /// <see cref="NvencRunCaptureIndexCommitStatus.None"/> is the uninitialized
    /// default and is never visible as a terminal shape.
    /// </summary>
    /// <remarks>
    /// The publication receipt, plan, root layout, and run identity are
    /// forwarded from the held operation and never duplicated as fields, and no
    /// new proof, token, or nonce is introduced.
    /// </remarks>
    internal readonly struct NvencRunCaptureIndexCommitAttemptResult
    {
        private readonly INvencRunCaptureIndexCommitter _committer;
        private readonly NvencRunCaptureIndexCommitOperation _operation;
        private readonly NvencRunCaptureIndexCommitReceipt _receipt;
        private readonly NvencRunCaptureIndexCommitStatus _status;

        private NvencRunCaptureIndexCommitAttemptResult(
            INvencRunCaptureIndexCommitter committer,
            NvencRunCaptureIndexCommitOperation operation,
            NvencRunCaptureIndexCommitReceipt receipt,
            NvencRunCaptureIndexCommitStatus status)
        {
            _committer = committer;
            _operation = operation;
            _receipt = receipt;
            _status = status;
        }

        internal INvencRunCaptureIndexCommitter Committer => _committer;

        internal NvencRunCaptureIndexCommitOperation Operation => _operation;

        internal NvencRunCaptureIndexCommitReceipt Receipt => _receipt;

        internal NvencRunCaptureIndexCommitStatus Status => _status;

        internal NvencRunArtifactPublicationReceipt ArtifactPublicationReceipt =>
            _operation.ArtifactPublicationReceipt;

        internal NvencRunArtifactPublicationOperation ArtifactPublicationOperation =>
            _operation.ArtifactPublicationOperation;

        internal CapturePublicationPlan Plan => _operation.Plan;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsNone => _status == NvencRunCaptureIndexCommitStatus.None;

        internal bool IsCommitted =>
            _status == NvencRunCaptureIndexCommitStatus.Committed
            && _committer != null
            && _operation != null
            && _receipt != null
            && _receipt.IsIssuedFor(_committer, _operation);

        internal bool IsFailed =>
            _status == NvencRunCaptureIndexCommitStatus.Failed
            && _committer != null
            && _operation != null
            && _operation.IsBindingIntact
            && _receipt == null;

        internal bool IsValid => IsCommitted || IsFailed;

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

        internal static NvencRunCaptureIndexCommitAttemptResult Committed(
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

            NvencRunCaptureIndexCommitReceipt receipt =
                NvencRunCaptureIndexCommitReceipt.Create(committer, operation);

            return new NvencRunCaptureIndexCommitAttemptResult(
                committer, operation, receipt, NvencRunCaptureIndexCommitStatus.Committed);
        }

        internal static NvencRunCaptureIndexCommitAttemptResult Failed(
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

            return new NvencRunCaptureIndexCommitAttemptResult(
                committer, operation, null, NvencRunCaptureIndexCommitStatus.Failed);
        }
    }
}
