using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable operation normalizing the two ways a recovered Phase 0.11 Run
    /// can arrive at CaptureComplete with an authoritative Capture Index: the
    /// inspection already found the final index authoritative, or a recovery
    /// commit made it so and returned its receipt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two authorities are held as what they are, exclusively: the
    /// existing-final path holds only the classification, and the committed
    /// path holds only the commit receipt and reads the classification from
    /// that receipt's own commit operation. An existing final index is never dressed up as a commit
    /// receipt, and the same decision is never stored twice. No enum, proof,
    /// token, nonce, generation, or issuer authority is introduced.
    /// </para>
    /// <para>
    /// Each path is issued through its own factory so a caller cannot reach one
    /// while meaning the other, and each factory requires only what its own
    /// authority must show: a CaptureComplete classification with no commit
    /// mode, or a valid receipt of a commit whose own operation carries the
    /// CommitRequired classification. The receipt's own correlation is reused
    /// rather than re-derived, so neither that classification nor the plan,
    /// snapshot, root layout, and Run identity are re-validated here.
    /// </para>
    /// <para>
    /// This type reads no file, serializes and hashes nothing, commits nothing,
    /// releases no lock, owns and disposes nothing, and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// <see cref="IsValid"/> recomputes the conditions of whichever authority
    /// it holds without throwing, so once the OS lock is released the decision
    /// or receipt becomes invalid and this operation follows it.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryOperation
    {
        private readonly NvencRunCaptureIndexRecoveryDecision _captureCompleteDecision;
        private readonly NvencRunCaptureIndexRecoveryCommitReceipt _commitReceipt;

        private NvencRunCaptureCompleteRecoveryOperation(
            NvencRunCaptureIndexRecoveryDecision captureCompleteDecision,
            NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt)
        {
            _captureCompleteDecision = captureCompleteDecision;
            _commitReceipt = commitReceipt;
        }

        /// <summary>
        /// The path where the inspection already found the final Capture Index
        /// authoritative, so nothing was committed and there is no receipt.
        /// </summary>
        internal static NvencRunCaptureCompleteRecoveryOperation FromCaptureCompleteRequired(
            NvencRunCaptureIndexRecoveryDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (!decision.IsValid)
            {
                throw new ArgumentException(
                    "Capture Index recovery decision must be valid.", nameof(decision));
            }

            if (decision.Disposition
                != NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired)
            {
                throw new ArgumentException(
                    "Only an already authoritative Capture Index may reach CaptureComplete without a commit.",
                    nameof(decision));
            }

            if (decision.CommitMode != CaptureRunCaptureIndexCommitMode.None)
            {
                throw new ArgumentException(
                    "A CaptureComplete classification must carry no commit mode.",
                    nameof(decision));
            }

            return new NvencRunCaptureCompleteRecoveryOperation(decision, null);
        }

        /// <summary>
        /// The path where a recovery commit made the final Capture Index
        /// authoritative and returned the receipt of that known success.
        /// </summary>
        internal static NvencRunCaptureCompleteRecoveryOperation FromCommittedIndex(
            NvencRunCaptureIndexRecoveryCommitReceipt receipt)
        {
            if (receipt == null)
            {
                throw new ArgumentNullException(nameof(receipt));
            }

            if (!IsCommittedIndex(receipt))
            {
                throw new ArgumentException(
                    "Receipt must be a valid recovery commit of its own CommitRequired classification.",
                    nameof(receipt));
            }

            return new NvencRunCaptureCompleteRecoveryOperation(null, receipt);
        }

        /// <summary>
        /// The exact classification behind this operation, held directly on the
        /// existing-final path and read from the receipt's own commit operation
        /// on the committed path.
        /// </summary>
        internal NvencRunCaptureIndexRecoveryDecision CaptureIndexRecoveryDecision =>
            _captureCompleteDecision ?? _commitReceipt?.Operation.CaptureIndexRecoveryDecision;

        internal NvencRunCaptureIndexRecoveryInspectionSnapshot CaptureIndexRecoverySnapshot =>
            CaptureIndexRecoveryDecision?.Snapshot;

        /// <summary>
        /// The commit receipt, present only on the committed path.
        /// </summary>
        internal NvencRunCaptureIndexRecoveryCommitReceipt CaptureIndexRecoveryCommitReceipt =>
            _commitReceipt;

        internal bool HasCommitReceipt => _commitReceipt != null;

        internal NvencRunPublicationRecoveryDecision PublicationRecoveryDecision =>
            CaptureIndexRecoveryDecision?.Operation?.PublicationRecoveryDecision;

        internal CapturePublicationPlan AuthoritativePlan =>
            CaptureIndexRecoveryDecision?.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => CaptureIndexRecoveryDecision?.RootLayout;

        internal long TestRunId => CaptureIndexRecoveryDecision.TestRunId;

        internal string RunInitializationId => CaptureIndexRecoveryDecision.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    if (_commitReceipt == null)
                    {
                        NvencRunCaptureIndexRecoveryDecision decision = _captureCompleteDecision;

                        return decision != null
                            && decision.IsValid
                            && decision.Disposition
                                == NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired
                            && decision.CommitMode == CaptureRunCaptureIndexCommitMode.None;
                    }

                    return _captureCompleteDecision == null && IsCommittedIndex(_commitReceipt);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The receipt must still be the valid evidence of its own committer
        /// and commit operation. That one question carries the classification
        /// with it: a commit operation is valid only while it holds a valid
        /// CommitRequired decision with a defined commit mode, so the
        /// authorization is not re-derived here.
        /// </summary>
        private static bool IsCommittedIndex(NvencRunCaptureIndexRecoveryCommitReceipt receipt)
        {
            NvencRunCaptureIndexRecoveryCommitOperation commitOperation = receipt.Operation;

            return commitOperation != null
                && receipt.IsIssuedFor(receipt.Committer, commitOperation);
        }
    }
}
