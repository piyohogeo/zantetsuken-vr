using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the Phase 0.11 NVENC publication recovery boundary exactly
    /// once under the held lock: from a publication-recovery open outcome it
    /// issues one inspection operation, runs the configured inspection
    /// execution coordinator once, classifies the returned snapshot once, and
    /// returns that single classification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It holds exactly one readonly dependency, the inspection execution
    /// coordinator, and is not an <see cref="IDisposable"/>. It owns no
    /// thread, queue, or task, waits on nothing, touches no file, rents no
    /// verification buffer, and neither holds nor releases the OS lock: lock
    /// ownership stays with the initialization ownership lease.
    /// </para>
    /// <para>
    /// Only an obviously unusable input is rejected here; every detailed
    /// correlation - a valid outcome that requires publication recovery, holds
    /// no session, and still holds its lock through valid identity evidence
    /// bound to its own exact path set - is the existing
    /// <see cref="NvencRunPublicationRecoveryInspectionOperation"/>
    /// constructor's, and is deliberately not restated as a second system. The
    /// operation is issued before the inspector is ever contacted and issuing
    /// it has no side effect, so a refused outcome never reaches the
    /// filesystem.
    /// </para>
    /// <para>
    /// The returned <see cref="NvencRunPublicationRecoveryDecision"/> is the
    /// classifier's own result, handed back by reference. Nothing is wrapped in
    /// a coordinator-bound result type: a stateless coordinator cannot prove
    /// after the fact that a decision came from it without minting a token or
    /// keeping an issue history, so no such authority is claimed. A decision's
    /// authority is the graph it already carries - the snapshot, the operation,
    /// and the open outcome that still holds the lock.
    /// </para>
    /// <para>
    /// This unit classifies and stops. It deletes no incomplete root and no
    /// NVENC temporary, quarantines and rewrites nothing on a collision,
    /// rebuilds no Capture Index, runs no CaptureComplete, cleanup, or lease
    /// release, adds no NVENC branch to the existing PNG/JSON recovery path,
    /// and never restores or infers the previous process's
    /// <see cref="NvencRunEvidenceDisposition"/>. An inspector exception
    /// propagates by the same reference; nothing is retried, re-inspected, or
    /// guessed into a disposition.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryOrchestrationCoordinator
    {
        private readonly NvencRunPublicationRecoveryInspectionExecutionCoordinator _inspectionExecution;

        internal NvencRunPublicationRecoveryOrchestrationCoordinator(
            NvencRunPublicationRecoveryInspectionExecutionCoordinator inspectionExecution)
        {
            _inspectionExecution = inspectionExecution
                ?? throw new ArgumentNullException(nameof(inspectionExecution));
        }

        internal NvencRunPublicationRecoveryInspectionExecutionCoordinator InspectionExecution =>
            _inspectionExecution;

        internal NvencRunPublicationRecoveryDecision Execute(
            CaptureRunInitializationOpenOutcome openOutcome)
        {
            if (openOutcome == null)
            {
                throw new ArgumentNullException(nameof(openOutcome));
            }

            if (openOutcome.RootLayout == null)
            {
                throw new ArgumentException(
                    "Open outcome must carry its root layout.", nameof(openOutcome));
            }

            // Issuance is the whole admission check: the operation constructor
            // requires a valid publication-recovery outcome with no session
            // whose lock identity evidence is still valid and bound to its own
            // exact path set, over that outcome's exact root layout. It reads
            // nothing and changes nothing, so a refused outcome is refused
            // before the inspector is contacted.
            NvencRunPublicationRecoveryInspectionOperation operation =
                new NvencRunPublicationRecoveryInspectionOperation(
                    openOutcome, openOutcome.RootLayout);

            // Exactly one inspection attempt. The execution coordinator already
            // requires a non-null, valid snapshot issued for this exact
            // operation, and lets an inspector exception through unchanged.
            NvencRunPublicationRecoveryInspectionSnapshot snapshot =
                _inspectionExecution.Execute(operation);

            // Exactly one classification of that exact snapshot.
            NvencRunPublicationRecoveryDecision decision =
                NvencRunPublicationRecoveryClassifier.Classify(snapshot);

            VerifyDecision(decision, snapshot, operation, openOutcome);

            return decision;
        }

        private static void VerifyDecision(
            NvencRunPublicationRecoveryDecision decision,
            NvencRunPublicationRecoveryInspectionSnapshot snapshot,
            NvencRunPublicationRecoveryInspectionOperation operation,
            CaptureRunInitializationOpenOutcome openOutcome)
        {
            if (decision == null
                || !decision.IsValid
                || !ReferenceEquals(decision.Snapshot, snapshot)
                || !ReferenceEquals(decision.Operation, operation)
                || !ReferenceEquals(operation.OpenOutcome, openOutcome)
                || !ReferenceEquals(decision.RootLayout, openOutcome.RootLayout))
            {
                throw new InvalidOperationException(
                    "The classification must be valid and correlated to the inspected operation.");
            }
        }
    }
}
