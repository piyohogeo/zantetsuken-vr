using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Allocation-free result of one NVENC Run publication plan commit
    /// attempt: the exact committer, the exact operation, and a status with a
    /// receipt held only for the <see cref="NvencRunPublicationPlanCommitStatus.Committed"/>
    /// shape. The three terminal shapes are mutually exclusive;
    /// <see cref="NvencRunPublicationPlanCommitStatus.None"/> is the
    /// uninitialized default and is never visible as a terminal shape.
    /// </summary>
    internal readonly struct NvencRunPublicationPlanCommitAttemptResult
    {
        private readonly INvencRunPublicationPlanCommitter _committer;
        private readonly NvencRunPublicationPlanCommitOperation _operation;
        private readonly NvencRunPublicationPlanCommitReceipt _receipt;
        private readonly NvencRunPublicationPlanCommitStatus _status;

        private NvencRunPublicationPlanCommitAttemptResult(
            INvencRunPublicationPlanCommitter committer,
            NvencRunPublicationPlanCommitOperation operation,
            NvencRunPublicationPlanCommitReceipt receipt,
            NvencRunPublicationPlanCommitStatus status)
        {
            _committer = committer;
            _operation = operation;
            _receipt = receipt;
            _status = status;
        }

        internal INvencRunPublicationPlanCommitter Committer => _committer;

        internal NvencRunPublicationPlanCommitOperation Operation => _operation;

        internal NvencRunPublicationPlanCommitReceipt Receipt => _receipt;

        internal NvencRunPublicationPlanCommitStatus Status => _status;

        internal bool IsNone => _status == NvencRunPublicationPlanCommitStatus.None;

        internal bool IsCommitted =>
            _status == NvencRunPublicationPlanCommitStatus.Committed
            && _committer != null
            && _operation != null
            && _receipt != null
            && _receipt.IsIssuedFor(_committer, _operation);

        internal bool IsFailedBeforeRename =>
            _status == NvencRunPublicationPlanCommitStatus.FailedBeforeRename
            && _committer != null
            && _operation != null
            && _receipt == null;

        internal bool IsCommitOutcomeUnknown =>
            _status == NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown
            && _committer != null
            && _operation != null
            && _receipt == null;

        internal bool IsValid => IsCommitted || IsFailedBeforeRename || IsCommitOutcomeUnknown;

        internal bool IsIssuedFor(
            INvencRunPublicationPlanCommitter committer,
            NvencRunPublicationPlanCommitOperation operation)
        {
            return committer != null
                && operation != null
                && ReferenceEquals(_committer, committer)
                && ReferenceEquals(_operation, operation)
                && IsValid;
        }

        internal static NvencRunPublicationPlanCommitAttemptResult Committed(
            INvencRunPublicationPlanCommitter committer,
            NvencRunPublicationPlanCommitOperation operation)
        {
            if (committer == null)
            {
                throw new ArgumentNullException(nameof(committer));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            NvencRunPublicationPlanCommitReceipt receipt =
                NvencRunPublicationPlanCommitReceipt.Create(committer, operation);

            return new NvencRunPublicationPlanCommitAttemptResult(
                committer, operation, receipt, NvencRunPublicationPlanCommitStatus.Committed);
        }

        internal static NvencRunPublicationPlanCommitAttemptResult FailedBeforeRename(
            INvencRunPublicationPlanCommitter committer,
            NvencRunPublicationPlanCommitOperation operation)
        {
            if (committer == null)
            {
                throw new ArgumentNullException(nameof(committer));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            return new NvencRunPublicationPlanCommitAttemptResult(
                committer, operation, null, NvencRunPublicationPlanCommitStatus.FailedBeforeRename);
        }

        internal static NvencRunPublicationPlanCommitAttemptResult CommitOutcomeUnknown(
            INvencRunPublicationPlanCommitter committer,
            NvencRunPublicationPlanCommitOperation operation)
        {
            if (committer == null)
            {
                throw new ArgumentNullException(nameof(committer));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            return new NvencRunPublicationPlanCommitAttemptResult(
                committer, operation, null, NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown);
        }
    }
}
