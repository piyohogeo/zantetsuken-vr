using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the Phase 0.11 NVENC Capture Index recovery boundary exactly
    /// once under the held lock: from a recoverable publication decision it
    /// issues one Capture Index inspection operation, runs the configured
    /// inspection execution coordinator once, classifies the returned snapshot
    /// once, and returns that single classification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It holds exactly one readonly dependency, the inspection execution
    /// coordinator, and is not an <see cref="IDisposable"/>. It owns no thread,
    /// queue, or task, waits on nothing, touches no file or buffer, and neither
    /// holds nor releases the OS lock.
    /// </para>
    /// <para>
    /// Only a null input is rejected here; the detailed admission - a valid
    /// publication recovery decision whose disposition is
    /// PublicationRecoveryRequired, carrying the exact authoritative plan its
    /// snapshot observed, over a Run whose lock is still held - is the existing
    /// <see cref="NvencRunCaptureIndexRecoveryInspectionOperation"/>
    /// constructor's and is not restated as a second system. Issuing that
    /// operation reads nothing and changes nothing, so an incomplete, deferred,
    /// colliding, invalid, or unlocked decision is refused before the inspector
    /// is contacted.
    /// </para>
    /// <para>
    /// The returned <see cref="NvencRunCaptureIndexRecoveryDecision"/> is the
    /// classifier's own result, handed back by reference. Nothing is wrapped in
    /// a coordinator-bound result: a stateless coordinator cannot prove after
    /// the fact that a decision came from it, so no issuer, receipt, token,
    /// nonce, generation, or issue history claims it. The authority of the
    /// answer is the graph it already carries - decision, snapshot, operation,
    /// publication recovery decision, open outcome, and the lock behind them.
    /// </para>
    /// <para>
    /// This unit observes and classifies, then stops. It builds no commit
    /// operation, deletes, reuses, and replaces no temporary, commits no
    /// Capture Index, runs no CaptureComplete or lease release, re-observes no
    /// file, and re-verifies no plan or chunk. An inspector or classifier
    /// exception propagates by the same reference; nothing is retried,
    /// re-inspected, re-classified, or guessed into a disposition.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryOrchestrationCoordinator
    {
        private readonly NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator
            _inspectionExecution;

        internal NvencRunCaptureIndexRecoveryOrchestrationCoordinator(
            NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator inspectionExecution)
        {
            _inspectionExecution = inspectionExecution
                ?? throw new ArgumentNullException(nameof(inspectionExecution));
        }

        internal NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator InspectionExecution =>
            _inspectionExecution;

        internal NvencRunCaptureIndexRecoveryDecision Execute(
            NvencRunPublicationRecoveryDecision publicationRecoveryDecision)
        {
            if (publicationRecoveryDecision == null)
            {
                throw new ArgumentNullException(nameof(publicationRecoveryDecision));
            }

            // Issuance is the whole admission check, and it has no side effect.
            NvencRunCaptureIndexRecoveryInspectionOperation operation =
                new NvencRunCaptureIndexRecoveryInspectionOperation(publicationRecoveryDecision);

            // Exactly one inspection attempt. The execution coordinator already
            // requires a non-null, valid snapshot issued for this exact
            // operation, and lets an inspector exception through unchanged.
            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                _inspectionExecution.Execute(operation);

            // Exactly one classification of that exact snapshot.
            NvencRunCaptureIndexRecoveryDecision decision =
                NvencRunCaptureIndexRecoveryClassifier.Classify(snapshot);

            VerifyDecision(decision, snapshot, operation, publicationRecoveryDecision);

            return decision;
        }

        private static void VerifyDecision(
            NvencRunCaptureIndexRecoveryDecision decision,
            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot,
            NvencRunCaptureIndexRecoveryInspectionOperation operation,
            NvencRunPublicationRecoveryDecision publicationRecoveryDecision)
        {
            if (decision == null
                || !decision.IsValid
                || !ReferenceEquals(decision.Snapshot, snapshot)
                || !ReferenceEquals(decision.Operation, operation)
                || !ReferenceEquals(
                    operation.PublicationRecoveryDecision, publicationRecoveryDecision)
                || !ReferenceEquals(
                    decision.AuthoritativePlan, publicationRecoveryDecision.AuthoritativePlan)
                || !ReferenceEquals(decision.RootLayout, publicationRecoveryDecision.RootLayout)
                || decision.TestRunId != publicationRecoveryDecision.TestRunId
                || !string.Equals(
                    decision.RunInitializationId,
                    publicationRecoveryDecision.RunInitializationId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The classification must be valid and correlated to the inspected operation.");
            }
        }
    }
}
