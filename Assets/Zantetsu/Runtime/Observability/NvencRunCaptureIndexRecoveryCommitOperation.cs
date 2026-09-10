using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable operation authorizing one Phase 0.11 NVENC Capture Index
    /// recovery commit: the exact classification that already decided a commit
    /// is required, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the <see cref="NvencRunCaptureIndexRecoveryDisposition.CommitRequired"/>
    /// branch can be turned into a commit, and only with one of the three
    /// existing commit modes; CaptureComplete, a collision, and an
    /// unclassifiable mode are refused here. Everything else the commit needs -
    /// the snapshot, the inspection operation, the publication recovery
    /// decision, the authoritative plan, the root layout, and the Run identity
    /// - is forwarded from that decision's graph, never copied into a second
    /// field.
    /// </para>
    /// <para>
    /// The detailed correlation of plan, snapshot, root layout, Run identity,
    /// and the OS lock is the existing decision and operation graph's, and is
    /// deliberately not restated as another validator. No canonical bytes,
    /// path, hash, file state, lock handle, receipt, proof, token, nonce, or
    /// generation is held.
    /// </para>
    /// <para>
    /// This type reads and writes no file, serializes and hashes nothing,
    /// commits nothing, owns and disposes nothing, and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// <see cref="IsValid"/> recomputes the same minimal conditions without
    /// throwing, so once the OS lock is released the decision becomes invalid
    /// and this operation follows it.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryCommitOperation
    {
        private readonly NvencRunCaptureIndexRecoveryDecision _decision;

        internal NvencRunCaptureIndexRecoveryCommitOperation(
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

            if (decision.Disposition != NvencRunCaptureIndexRecoveryDisposition.CommitRequired)
            {
                throw new ArgumentException(
                    "Only a decision that requires a Capture Index commit may authorize one.",
                    nameof(decision));
            }

            if (!IsCommitMode(decision.CommitMode))
            {
                throw new ArgumentException(
                    "The decision must carry one of the defined Capture Index commit modes.",
                    nameof(decision));
            }

            _decision = decision;
        }

        internal NvencRunCaptureIndexRecoveryDecision CaptureIndexRecoveryDecision => _decision;

        internal NvencRunCaptureIndexRecoveryInspectionSnapshot CaptureIndexRecoverySnapshot =>
            _decision.Snapshot;

        internal NvencRunCaptureIndexRecoveryInspectionOperation
            CaptureIndexRecoveryInspectionOperation => _decision.Operation;

        internal NvencRunPublicationRecoveryDecision PublicationRecoveryDecision =>
            _decision.Operation.PublicationRecoveryDecision;

        /// <summary>
        /// The exact plan the publication recovery published; nothing here
        /// copies or re-serializes it.
        /// </summary>
        internal CapturePublicationPlan AuthoritativePlan => _decision.AuthoritativePlan;

        /// <summary>
        /// How the commit must treat the temporary, decided by the
        /// classification and never re-derived here.
        /// </summary>
        internal CaptureRunCaptureIndexCommitMode CommitMode => _decision.CommitMode;

        internal CaptureRunRootLayout RootLayout => _decision.RootLayout;

        internal long TestRunId => _decision.TestRunId;

        internal string RunInitializationId => _decision.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _decision != null
                        && _decision.IsValid
                        && _decision.Disposition
                            == NvencRunCaptureIndexRecoveryDisposition.CommitRequired
                        && IsCommitMode(_decision.CommitMode);
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool IsCommitMode(CaptureRunCaptureIndexCommitMode mode)
        {
            switch (mode)
            {
                case CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit:
                case CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit:
                case CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit:
                    return true;

                default:
                    return false;
            }
        }
    }
}
