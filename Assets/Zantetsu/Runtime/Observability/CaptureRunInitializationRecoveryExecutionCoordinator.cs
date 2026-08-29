using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Executes a prepared recovery execution batch in order and verifies each
    /// backend receipt immediately after the call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator holds exactly three readonly dependencies — the cleanup
    /// backend, the root provisioner, and the marker atomic writer — and is not
    /// an <see cref="IDisposable"/>. <see cref="Execute"/> processes each
    /// prepared step once, verifies the returned receipt before proceeding,
    /// and returns a result only after every step succeeded.
    /// </para>
    /// <para>
    /// A backend exception propagates unchanged: the coordinator performs no
    /// retry, no rollback, no compensating cleanup or root or marker deletion,
    /// and never disposes the lease or its dependencies. A failed execution
    /// returns no result, leaves already-committed filesystem changes in place,
    /// and skips all remaining steps. The caller must not blindly re-run the
    /// same batch; it re-inspects under the held lock. A contract-violating
    /// receipt throws <see cref="InvalidOperationException"/> and also skips
    /// the remaining steps.
    /// </para>
    /// <para>
    /// Routing steps contact no backend: StartFreshRequired does not invoke the
    /// existing bootstrap (which would re-acquire the lock),
    /// InitializationReady only reports completion, PublicationRecoveryRequired
    /// defers to publication recovery, and RunRootCollision touches no backend
    /// and carries no mutation receipt.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryExecutionCoordinator
    {
        private readonly ICaptureRunInitializationRecoveryCleanupBackend _cleanupBackend;
        private readonly ICaptureRunRootProvisioner _rootProvisioner;
        private readonly ICaptureRunMarkerAtomicWriter _markerWriter;

        internal CaptureRunInitializationRecoveryExecutionCoordinator(
            ICaptureRunInitializationRecoveryCleanupBackend cleanupBackend,
            ICaptureRunRootProvisioner rootProvisioner,
            ICaptureRunMarkerAtomicWriter markerWriter)
        {
            if (cleanupBackend == null)
            {
                throw new ArgumentNullException(nameof(cleanupBackend));
            }

            if (rootProvisioner == null)
            {
                throw new ArgumentNullException(nameof(rootProvisioner));
            }

            if (markerWriter == null)
            {
                throw new ArgumentNullException(nameof(markerWriter));
            }

            _cleanupBackend = cleanupBackend;
            _rootProvisioner = rootProvisioner;
            _markerWriter = markerWriter;
        }

        internal ICaptureRunInitializationRecoveryCleanupBackend CleanupBackend => _cleanupBackend;

        internal ICaptureRunRootProvisioner RootProvisioner => _rootProvisioner;

        internal ICaptureRunMarkerAtomicWriter MarkerWriter => _markerWriter;

        internal CaptureRunInitializationRecoveryExecutionResult Execute(
            CaptureRunInitializationRecoveryExecutionBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (!batch.IsValid)
            {
                throw new ArgumentException("Execution batch must be valid.", nameof(batch));
            }

            CaptureRunInitializationRecoveryCompletedStep[] completedSteps = new CaptureRunInitializationRecoveryCompletedStep[batch.Count];

            for (int i = 0; i < batch.Count; i++)
            {
                CaptureRunInitializationRecoveryPreparedStep prepared = batch.GetPreparedStep(i);

                CaptureRunInitializationRecoveryCleanupReceipt cleanupReceipt = null;
                CaptureRunRootProvisionReceipt provisionReceipt = null;
                CaptureRunMarkerWriteReceipt writeReceipt = null;

                switch (prepared.Action)
                {
                    case CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary:
                    case CaptureRunInitializationRecoveryAction.RemoveEmptyRoot:
                        cleanupReceipt = _cleanupBackend.Execute(prepared.CleanupOperation);
                        VerifyCleanupReceipt(cleanupReceipt, prepared.CleanupOperation);
                        break;

                    case CaptureRunInitializationRecoveryAction.ProvisionRoot:
                        provisionReceipt = _rootProvisioner.ProvisionNew(prepared.ProvisionOperation);
                        VerifyProvisionReceipt(provisionReceipt, prepared.ProvisionOperation);
                        break;

                    case CaptureRunInitializationRecoveryAction.WriteMarker:
                        writeReceipt = _markerWriter.WriteAtomic(prepared.MarkerWriteOperation);
                        VerifyWriteReceipt(writeReceipt, prepared.MarkerWriteOperation);
                        break;
                }

                completedSteps[i] = new CaptureRunInitializationRecoveryCompletedStep(
                    prepared,
                    cleanupReceipt,
                    provisionReceipt,
                    writeReceipt);
            }

            return new CaptureRunInitializationRecoveryExecutionResult(this, batch, completedSteps);
        }

        private void VerifyCleanupReceipt(
            CaptureRunInitializationRecoveryCleanupReceipt receipt,
            CaptureRunInitializationRecoveryCleanupOperation operation)
        {
            if (receipt == null || !receipt.IsIssuedFor(_cleanupBackend, operation))
            {
                throw new InvalidOperationException("Cleanup receipt must be valid and issued for the cleanup operation.");
            }
        }

        private void VerifyProvisionReceipt(
            CaptureRunRootProvisionReceipt receipt,
            CaptureRunRootProvisionOperation operation)
        {
            if (receipt == null
                || !receipt.IsValid
                || !ReferenceEquals(receipt.IssuedBy, _rootProvisioner)
                || !ReferenceEquals(receipt.Operation, operation))
            {
                throw new InvalidOperationException("Provision receipt must be valid and issued for the provision operation.");
            }

            if (receipt.RootRole != operation.RootRole
                || !ReferenceEquals(receipt.RootLayout, operation.RootLayout)
                || !string.Equals(receipt.TrustedBaseRoot, operation.TrustedBaseRoot, StringComparison.Ordinal)
                || !string.Equals(receipt.RunRoot, operation.RunRoot, StringComparison.Ordinal)
                || receipt.TestRunId != operation.TestRunId)
            {
                throw new InvalidOperationException("Provision receipt must match the provision operation.");
            }
        }

        private void VerifyWriteReceipt(
            CaptureRunMarkerWriteReceipt receipt,
            CaptureRunMarkerWriteOperation operation)
        {
            if (receipt == null
                || !receipt.IsValid
                || !ReferenceEquals(receipt.IssuedBy, _markerWriter)
                || !ReferenceEquals(receipt.Operation, operation))
            {
                throw new InvalidOperationException("Write receipt must be valid and issued for the write operation.");
            }

            if (receipt.RootRole != operation.RootRole
                || receipt.MarkerKind != operation.MarkerKind
                || !string.Equals(receipt.TemporaryPath, operation.TemporaryPath, StringComparison.Ordinal)
                || !string.Equals(receipt.FinalPath, operation.FinalPath, StringComparison.Ordinal)
                || receipt.ByteCount != operation.ByteCount)
            {
                throw new InvalidOperationException("Write receipt must match the write operation.");
            }
        }
    }
}
