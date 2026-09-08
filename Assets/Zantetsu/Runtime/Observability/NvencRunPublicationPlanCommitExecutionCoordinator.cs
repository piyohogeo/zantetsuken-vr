using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Run publication plan commit execution
    /// coordinator. It holds exactly one
    /// <see cref="INvencRunPublicationPlanCommitter"/>, calls it exactly once
    /// per valid operation, verifies the returned attempt result, mints a
    /// coordinator-specific issuance proof, and issues a
    /// <see cref="NvencRunPublicationPlanCommitExecutionResult"/>. It owns no
    /// thread or queue, performs no file, hash, thread, or task operation, and
    /// is not an <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// A committer exception propagates unchanged; the coordinator never
    /// repeats the commit, never rebuilds the operation, and never issues a
    /// result after a failure. A null, invalid, foreign, or unverified attempt
    /// result is rejected with <see cref="InvalidOperationException"/> before
    /// any proof or result is issued. This layer does not poison the process;
    /// the future Publication Service owns thread and fatal policy.
    /// </remarks>
    internal sealed class NvencRunPublicationPlanCommitExecutionCoordinator
    {
        private static readonly object IssuanceGate = new object();

        private readonly INvencRunPublicationPlanCommitter _committer;

        internal NvencRunPublicationPlanCommitExecutionCoordinator(
            INvencRunPublicationPlanCommitter committer)
        {
            _committer = committer ?? throw new ArgumentNullException(nameof(committer));
        }

        internal INvencRunPublicationPlanCommitter Committer => _committer;

        internal NvencRunPublicationPlanCommitExecutionResult Execute(
            NvencRunPublicationPlanCommitOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            NvencRunPublicationPlanCommitAttemptResult attempt = _committer.Commit(operation);

            if (!IsValidAttempt(attempt, operation))
            {
                throw new InvalidOperationException(
                    "Committer returned a null, foreign, or unverified attempt result.");
            }

            IssuanceProof proof = IssuanceProof.Mint(
                IssuanceGate, this, _committer, operation, attempt);
            return NvencRunPublicationPlanCommitExecutionResult.Create(
                this, proof, operation, attempt);
        }

        private bool IsValidAttempt(
            NvencRunPublicationPlanCommitAttemptResult attempt,
            NvencRunPublicationPlanCommitOperation operation)
        {
            if (attempt.IsNone || !attempt.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(attempt.Committer, _committer)
                || !ReferenceEquals(attempt.Operation, operation))
            {
                return false;
            }

            switch (attempt.Status)
            {
                case NvencRunPublicationPlanCommitStatus.Committed:
                {
                    NvencRunPublicationPlanCommitReceipt receipt = attempt.Receipt;
                    return receipt != null
                        && receipt.IsIssuedFor(_committer, operation);
                }

                case NvencRunPublicationPlanCommitStatus.FailedBeforeRename:
                case NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown:
                    return attempt.Receipt == null;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Coordinator-specific issuance proof. It binds the exact coordinator,
        /// the private issuance gate, the exact committer, operation, and
        /// attempt result. Its constructor is private and only the coordinator
        /// calls the mint path; only the O(1) exception-safe
        /// <see cref="IsMintedFor"/> is exposed. The gate, and any token or
        /// nonce, is never exposed.
        /// </summary>
        internal sealed class IssuanceProof
        {
            private readonly NvencRunPublicationPlanCommitExecutionCoordinator _coordinator;
            private readonly object _issuanceGate;
            private readonly INvencRunPublicationPlanCommitter _committer;
            private readonly NvencRunPublicationPlanCommitOperation _operation;
            private readonly NvencRunPublicationPlanCommitAttemptResult _attempt;

            private IssuanceProof(
                NvencRunPublicationPlanCommitExecutionCoordinator coordinator,
                object issuanceGate,
                INvencRunPublicationPlanCommitter committer,
                NvencRunPublicationPlanCommitOperation operation,
                NvencRunPublicationPlanCommitAttemptResult attempt)
            {
                _coordinator = coordinator;
                _issuanceGate = issuanceGate;
                _committer = committer;
                _operation = operation;
                _attempt = attempt;
            }

            internal static IssuanceProof Mint(
                object issuanceGate,
                NvencRunPublicationPlanCommitExecutionCoordinator coordinator,
                INvencRunPublicationPlanCommitter committer,
                NvencRunPublicationPlanCommitOperation operation,
                NvencRunPublicationPlanCommitAttemptResult attempt)
            {
                if (!ReferenceEquals(
                        issuanceGate,
                        NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceGate))
                {
                    throw new ArgumentException(
                        "Issuance gate does not match.", nameof(issuanceGate));
                }

                return new IssuanceProof(
                    coordinator, issuanceGate, committer, operation, attempt);
            }

            internal bool IsMintedFor(
                NvencRunPublicationPlanCommitExecutionCoordinator coordinator,
                INvencRunPublicationPlanCommitter committer,
                NvencRunPublicationPlanCommitOperation operation,
                NvencRunPublicationPlanCommitAttemptResult attempt)
            {
                return coordinator != null
                    && committer != null
                    && operation != null
                    && ReferenceEquals(_coordinator, coordinator)
                    && ReferenceEquals(
                        _issuanceGate,
                        NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceGate)
                    && ReferenceEquals(_committer, committer)
                    && ReferenceEquals(_operation, operation)
                    && AttemptEquals(_attempt, attempt);
            }

            private static bool AttemptEquals(
                NvencRunPublicationPlanCommitAttemptResult a,
                NvencRunPublicationPlanCommitAttemptResult b)
            {
                return ReferenceEquals(a.Committer, b.Committer)
                    && ReferenceEquals(a.Operation, b.Operation)
                    && a.Status == b.Status
                    && ReferenceEquals(a.Receipt, b.Receipt);
            }
        }
    }
}
