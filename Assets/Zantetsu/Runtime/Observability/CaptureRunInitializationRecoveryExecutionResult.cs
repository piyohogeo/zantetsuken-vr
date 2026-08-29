using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of a completed recovery execution: the coordinator that
    /// issued it, the batch it executed, and the completed steps in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The completed-step array is defensively copied at construction and never
    /// exposed. <see cref="IsValid"/> recomputes the full correlation — count,
    /// order, prepared-step identity, receipt issuers, and receipt operations —
    /// from the held values without throwing.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryExecutionResult
    {
        private readonly CaptureRunInitializationRecoveryExecutionCoordinator _issuedBy;
        private readonly CaptureRunInitializationRecoveryExecutionBatch _batch;
        private readonly CaptureRunInitializationRecoveryCompletedStep[] _completedSteps;

        internal CaptureRunInitializationRecoveryExecutionResult(
            CaptureRunInitializationRecoveryExecutionCoordinator issuedBy,
            CaptureRunInitializationRecoveryExecutionBatch batch,
            CaptureRunInitializationRecoveryCompletedStep[] completedSteps)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (!batch.IsValid)
            {
                throw new ArgumentException("Execution batch must be valid.", nameof(batch));
            }

            if (completedSteps == null)
            {
                throw new ArgumentNullException(nameof(completedSteps));
            }

            if (!IsCorrelated(issuedBy, batch, completedSteps))
            {
                throw new ArgumentException("Completed steps must be fully correlated with the issuing coordinator and batch.", nameof(completedSteps));
            }

            _issuedBy = issuedBy;
            _batch = batch;
            _completedSteps = Copy(completedSteps);
        }

        internal CaptureRunInitializationRecoveryExecutionCoordinator IssuedBy => _issuedBy;

        internal CaptureRunInitializationRecoveryExecutionBatch Batch => _batch;

        internal int Count => _completedSteps.Length;

        internal CaptureRunInitializationRecoveryExecutionStatus Status
        {
            get
            {
                CaptureRunInitializationRecoveryDisposition disposition = _batch.ActionPlan.Decision.Disposition;
                switch (disposition)
                {
                    case CaptureRunInitializationRecoveryDisposition.StartFresh:
                    case CaptureRunInitializationRecoveryDisposition.CleanupTemporaryAndStartFresh:
                        return CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired;

                    case CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization:
                    case CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers:
                    case CaptureRunInitializationRecoveryDisposition.AlreadyInitialized:
                        return CaptureRunInitializationRecoveryExecutionStatus.InitializationReady;

                    case CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery:
                        return CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired;

                    case CaptureRunInitializationRecoveryDisposition.RunRootCollision:
                        return CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision;

                    default:
                        return CaptureRunInitializationRecoveryExecutionStatus.None;
                }
            }
        }

        internal CaptureRunRootLayout RootLayout => _batch.RootLayout;

        internal CaptureRunLockLease LockLease => _batch.LockLease;

        internal long TestRunId => _batch.TestRunId;

        internal string RunInitializationId
        {
            get
            {
                CaptureRunMarkerBinding binding = _batch.ExpectedBinding;
                return binding != null ? binding.RunInitializationId : null;
            }
        }

        internal CaptureRunInitializationRecoveryCompletedStep GetCompletedStep(int index)
        {
            if (index < 0 || index >= _completedSteps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Completed step index out of range.");
            }

            return _completedSteps[index];
        }

        internal bool IsValid => IsCorrelated(_issuedBy, _batch, _completedSteps);

        private static bool IsCorrelated(
            CaptureRunInitializationRecoveryExecutionCoordinator issuedBy,
            CaptureRunInitializationRecoveryExecutionBatch batch,
            CaptureRunInitializationRecoveryCompletedStep[] completedSteps)
        {
            if (issuedBy == null || batch == null || !batch.IsValid || completedSteps == null)
            {
                return false;
            }

            if (completedSteps.Length != batch.Count)
            {
                return false;
            }

            for (int i = 0; i < completedSteps.Length; i++)
            {
                CaptureRunInitializationRecoveryCompletedStep completed = completedSteps[i];
                if (completed == null || !completed.IsValid)
                {
                    return false;
                }

                if (!ReferenceEquals(completed.PreparedStep, batch.GetPreparedStep(i)))
                {
                    return false;
                }

                switch (completed.PreparedStep.Action)
                {
                    case CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary:
                    case CaptureRunInitializationRecoveryAction.RemoveEmptyRoot:
                        if (!ReferenceEquals(completed.CleanupReceipt.IssuedBy, issuedBy.CleanupBackend))
                        {
                            return false;
                        }

                        break;

                    case CaptureRunInitializationRecoveryAction.ProvisionRoot:
                        if (!ReferenceEquals(completed.ProvisionReceipt.IssuedBy, issuedBy.RootProvisioner))
                        {
                            return false;
                        }

                        break;

                    case CaptureRunInitializationRecoveryAction.WriteMarker:
                        if (!ReferenceEquals(completed.MarkerWriteReceipt.IssuedBy, issuedBy.MarkerWriter))
                        {
                            return false;
                        }

                        break;
                }
            }

            return true;
        }

        private static CaptureRunInitializationRecoveryCompletedStep[] Copy(CaptureRunInitializationRecoveryCompletedStep[] steps)
        {
            CaptureRunInitializationRecoveryCompletedStep[] copy = new CaptureRunInitializationRecoveryCompletedStep[steps.Length];
            Array.Copy(steps, copy, steps.Length);
            return copy;
        }
    }
}
