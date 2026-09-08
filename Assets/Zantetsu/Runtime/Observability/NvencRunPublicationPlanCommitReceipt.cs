using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run publication plan commit receipt: the
    /// exact committer and the exact operation whose final-name placement is
    /// known to have succeeded. It is issued only from the
    /// <see cref="NvencRunPublicationPlanCommitAttemptResult.Committed"/> path
    /// and has no public constructor.
    /// </summary>
    /// <remarks>
    /// This type holds only the exact committer and exact operation, performs
    /// no filesystem or hash work, and is not an
    /// <see cref="IDisposable"/>. Its validity uses the operation's
    /// post-commit issuance binding, so advancing the Registry Slot to
    /// <c>Committed</c> does not invalidate an already-issued receipt, while a
    /// corrupted plan, descriptor, relation, Trace receipt, or Session Issue
    /// fails closed.
    /// </remarks>
    internal sealed class NvencRunPublicationPlanCommitReceipt
    {
        private readonly INvencRunPublicationPlanCommitter _committer;
        private readonly NvencRunPublicationPlanCommitOperation _operation;

        private NvencRunPublicationPlanCommitReceipt(
            INvencRunPublicationPlanCommitter committer,
            NvencRunPublicationPlanCommitOperation operation)
        {
            _committer = committer;
            _operation = operation;
        }

        internal static NvencRunPublicationPlanCommitReceipt Create(
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

            if (!operation.IsBindingIntact)
            {
                throw new ArgumentException(
                    "The operation's issuance binding is no longer intact.", nameof(operation));
            }

            return new NvencRunPublicationPlanCommitReceipt(committer, operation);
        }

        internal INvencRunPublicationPlanCommitter Committer => _committer;

        internal NvencRunPublicationPlanCommitOperation Operation => _operation;

        internal bool IsValid =>
            _committer != null
            && _operation != null
            && _operation.IsBindingIntact;

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
    }
}
