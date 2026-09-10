using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free operation authorizing one Phase 0.11 NVENC
    /// Capture Index recovery inspection: the exact publication recovery
    /// decision that already found this Run recoverable, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a decision whose disposition is
    /// <see cref="NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired"/>
    /// can authorize this step: Incomplete, Deferred, and a publication
    /// recovery collision never reach the Capture Index at all. The
    /// authoritative plan must be the very reference the decision's snapshot
    /// already held, and the plan, root layout, and Run identity must still
    /// agree with that graph, so the Capture Index is only ever judged against
    /// the plan whose chunk was verified.
    /// </para>
    /// <para>
    /// The plan, root layout, and Run identity are forwarded, never copied.
    /// This type reads no file, re-serializes and re-hashes nothing, mints no
    /// receipt, token, nonce, or generation, owns and disposes nothing, and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// <see cref="IsValid"/> recomputes every check without throwing, so once
    /// the OS lock is released the decision becomes invalid and this operation
    /// follows it.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryInspectionOperation
    {
        private readonly NvencRunPublicationRecoveryDecision _publicationRecoveryDecision;

        internal NvencRunCaptureIndexRecoveryInspectionOperation(
            NvencRunPublicationRecoveryDecision publicationRecoveryDecision)
        {
            if (publicationRecoveryDecision == null)
            {
                throw new ArgumentNullException(nameof(publicationRecoveryDecision));
            }

            if (!publicationRecoveryDecision.IsValid)
            {
                throw new ArgumentException(
                    "Publication recovery decision must be valid.",
                    nameof(publicationRecoveryDecision));
            }

            if (publicationRecoveryDecision.Disposition
                != NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired)
            {
                throw new ArgumentException(
                    "Only a recoverable publication may be judged against the Capture Index.",
                    nameof(publicationRecoveryDecision));
            }

            if (!CarriesAuthoritativePlan(publicationRecoveryDecision))
            {
                throw new ArgumentException(
                    "The decision must carry the exact authoritative plan its snapshot observed.",
                    nameof(publicationRecoveryDecision));
            }

            _publicationRecoveryDecision = publicationRecoveryDecision;
        }

        internal NvencRunPublicationRecoveryDecision PublicationRecoveryDecision =>
            _publicationRecoveryDecision;

        internal NvencRunPublicationRecoveryInspectionSnapshot PublicationRecoverySnapshot =>
            _publicationRecoveryDecision.Snapshot;

        /// <summary>
        /// The exact plan the recovery decision published; nothing here copies
        /// or re-serializes it.
        /// </summary>
        internal CapturePublicationPlan AuthoritativePlan =>
            _publicationRecoveryDecision.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _publicationRecoveryDecision.RootLayout;

        internal long TestRunId => _publicationRecoveryDecision.TestRunId;

        internal string RunInitializationId => _publicationRecoveryDecision.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _publicationRecoveryDecision != null
                        && _publicationRecoveryDecision.IsValid
                        && _publicationRecoveryDecision.Disposition
                            == NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired
                        && CarriesAuthoritativePlan(_publicationRecoveryDecision);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The authoritative plan must be the snapshot's own plan reference and
        /// must still describe the decision's Run and root layout.
        /// </summary>
        private static bool CarriesAuthoritativePlan(NvencRunPublicationRecoveryDecision decision)
        {
            CapturePublicationPlan plan = decision.AuthoritativePlan;
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = decision.Snapshot;

            return plan != null
                && plan.IsValid
                && snapshot != null
                && ReferenceEquals(snapshot.PublicationPlan, plan)
                && snapshot.Operation != null
                && ReferenceEquals(decision.RootLayout, snapshot.Operation.RootLayout)
                && plan.TestRunId == decision.TestRunId
                && string.Equals(
                    plan.RunInitializationId,
                    decision.RunInitializationId,
                    StringComparison.Ordinal);
        }
    }
}
