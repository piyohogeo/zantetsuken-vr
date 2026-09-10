using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one Phase 0.11 NVENC publication recovery
    /// orchestration: the coordinator that produced it and the single
    /// classification decision it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those two references are the whole state. The snapshot, operation, open
    /// outcome, disposition, authoritative plan, root layout, and Run identity
    /// are forwarded from that graph rather than copied, so there is no second
    /// byte sequence, hash, or path here, and no receipt, token, nonce, or
    /// generation is minted. The disposition is the existing
    /// <see cref="NvencRunPublicationRecoveryDisposition"/>; no parallel
    /// execution status is introduced.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, touches no file, holds
    /// no lock and releases none, and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject. <see cref="IsValid"/> re-checks the
    /// exact correlation of the held graph without throwing, so once the OS
    /// lock is released the operation, snapshot, and decision become invalid
    /// and this result follows them.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryOrchestrationResult
    {
        private readonly NvencRunPublicationRecoveryOrchestrationCoordinator _issuedBy;
        private readonly NvencRunPublicationRecoveryDecision _decision;

        internal NvencRunPublicationRecoveryOrchestrationResult(
            NvencRunPublicationRecoveryOrchestrationCoordinator issuedBy,
            NvencRunPublicationRecoveryDecision decision)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (!decision.IsValid)
            {
                throw new ArgumentException("Decision must be valid.", nameof(decision));
            }

            _issuedBy = issuedBy;
            _decision = decision;
        }

        internal NvencRunPublicationRecoveryOrchestrationCoordinator IssuedBy => _issuedBy;

        internal NvencRunPublicationRecoveryDecision Decision => _decision;

        internal NvencRunPublicationRecoveryInspectionSnapshot Snapshot => _decision.Snapshot;

        internal NvencRunPublicationRecoveryInspectionOperation Operation => _decision.Operation;

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _decision.Operation.OpenOutcome;

        internal NvencRunPublicationRecoveryDisposition Disposition => _decision.Disposition;

        /// <summary>
        /// The exact plan reference the snapshot held, present only for a
        /// recoverable Run and null for every other disposition.
        /// </summary>
        internal CapturePublicationPlan AuthoritativePlan => _decision.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _decision.RootLayout;

        internal long TestRunId => _decision.TestRunId;

        internal string RunInitializationId => _decision.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    if (_issuedBy == null || _decision == null || !_decision.IsValid)
                    {
                        return false;
                    }

                    NvencRunPublicationRecoveryInspectionSnapshot snapshot = _decision.Snapshot;
                    NvencRunPublicationRecoveryInspectionOperation operation = _decision.Operation;

                    return snapshot != null
                        && operation != null
                        && operation.IsValid
                        && ReferenceEquals(snapshot.Operation, operation)
                        && ReferenceEquals(_decision.RootLayout, operation.RootLayout)
                        && operation.OpenOutcome != null
                        && ReferenceEquals(operation.RootLayout, operation.OpenOutcome.RootLayout);
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
