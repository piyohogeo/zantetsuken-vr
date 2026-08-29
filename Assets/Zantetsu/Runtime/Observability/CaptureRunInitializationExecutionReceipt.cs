using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable token returned after a Capture Run initialization sequence has
    /// fully succeeded. It correlates the driving write batch with the two
    /// provision receipts and four write receipts produced along the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor re-verifies every correlation itself rather than
    /// trusting the coordinator: all seven references must be non-null, the two
    /// provision receipts must share one issuer, the four write receipts must
    /// share one issuer, the provision operations must be the staging and final
    /// operations of the batch's root layout, the write receipts must match the
    /// batch's four operations by reference, and the batch must preserve its
    /// fixed four-operation order. <see cref="IsValid"/> recomputes the same
    /// checks from the stored values without an independent flag.
    /// </para>
    /// <para>
    /// <see cref="RootLayout"/>, <see cref="TestRunId"/>, and
    /// <see cref="RunInitializationId"/> are forwarded from the batch and hold
    /// no copied value. This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationExecutionReceipt
    {
        private readonly CaptureRunInitializationWriteBatch _batch;
        private readonly CaptureRunRootProvisionReceipt _stagingProvision;
        private readonly CaptureRunRootProvisionReceipt _finalProvision;
        private readonly CaptureRunMarkerWriteReceipt _stagingInitializationWrite;
        private readonly CaptureRunMarkerWriteReceipt _finalInitializationWrite;
        private readonly CaptureRunMarkerWriteReceipt _stagingReadyWrite;
        private readonly CaptureRunMarkerWriteReceipt _finalReadyWrite;

        internal CaptureRunInitializationExecutionReceipt(
            CaptureRunInitializationWriteBatch batch,
            CaptureRunRootProvisionReceipt stagingProvision,
            CaptureRunRootProvisionReceipt finalProvision,
            CaptureRunMarkerWriteReceipt stagingInitializationWrite,
            CaptureRunMarkerWriteReceipt finalInitializationWrite,
            CaptureRunMarkerWriteReceipt stagingReadyWrite,
            CaptureRunMarkerWriteReceipt finalReadyWrite)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (stagingProvision == null)
            {
                throw new ArgumentNullException(nameof(stagingProvision));
            }

            if (finalProvision == null)
            {
                throw new ArgumentNullException(nameof(finalProvision));
            }

            if (stagingInitializationWrite == null)
            {
                throw new ArgumentNullException(nameof(stagingInitializationWrite));
            }

            if (finalInitializationWrite == null)
            {
                throw new ArgumentNullException(nameof(finalInitializationWrite));
            }

            if (stagingReadyWrite == null)
            {
                throw new ArgumentNullException(nameof(stagingReadyWrite));
            }

            if (finalReadyWrite == null)
            {
                throw new ArgumentNullException(nameof(finalReadyWrite));
            }

            if (!CorrelationsHold(batch, stagingProvision, finalProvision, stagingInitializationWrite, finalInitializationWrite, stagingReadyWrite, finalReadyWrite))
            {
                throw new ArgumentException("Execution receipt inputs are not mutually correlated.");
            }

            _batch = batch;
            _stagingProvision = stagingProvision;
            _finalProvision = finalProvision;
            _stagingInitializationWrite = stagingInitializationWrite;
            _finalInitializationWrite = finalInitializationWrite;
            _stagingReadyWrite = stagingReadyWrite;
            _finalReadyWrite = finalReadyWrite;
        }

        internal CaptureRunInitializationWriteBatch Batch => _batch;

        internal CaptureRunRootProvisionReceipt StagingProvision => _stagingProvision;

        internal CaptureRunRootProvisionReceipt FinalProvision => _finalProvision;

        internal CaptureRunMarkerWriteReceipt StagingInitializationWrite => _stagingInitializationWrite;

        internal CaptureRunMarkerWriteReceipt FinalInitializationWrite => _finalInitializationWrite;

        internal CaptureRunMarkerWriteReceipt StagingReadyWrite => _stagingReadyWrite;

        internal CaptureRunMarkerWriteReceipt FinalReadyWrite => _finalReadyWrite;

        internal CaptureRunRootLayout RootLayout => _batch.Documents.Plan.MarkerPaths.RootLayout;

        internal long TestRunId => _batch.Documents.Plan.TestRunId;

        internal string RunInitializationId => _batch.Documents.Plan.RunInitializationId;

        internal bool IsValid => CorrelationsHold(_batch, _stagingProvision, _finalProvision, _stagingInitializationWrite, _finalInitializationWrite, _stagingReadyWrite, _finalReadyWrite);

        private static bool CorrelationsHold(
            CaptureRunInitializationWriteBatch batch,
            CaptureRunRootProvisionReceipt stagingProvision,
            CaptureRunRootProvisionReceipt finalProvision,
            CaptureRunMarkerWriteReceipt stagingInitializationWrite,
            CaptureRunMarkerWriteReceipt finalInitializationWrite,
            CaptureRunMarkerWriteReceipt stagingReadyWrite,
            CaptureRunMarkerWriteReceipt finalReadyWrite)
        {
            if (batch == null
                || stagingProvision == null
                || finalProvision == null
                || stagingInitializationWrite == null
                || finalInitializationWrite == null
                || stagingReadyWrite == null
                || finalReadyWrite == null)
            {
                return false;
            }

            if (!stagingProvision.IsValid
                || !finalProvision.IsValid
                || !stagingInitializationWrite.IsValid
                || !finalInitializationWrite.IsValid
                || !stagingReadyWrite.IsValid
                || !finalReadyWrite.IsValid)
            {
                return false;
            }

            CaptureRunInitializationDocumentSet documents = batch.Documents;
            CaptureRunInitializationPlan plan = documents != null ? documents.Plan : null;
            CaptureRunMarkerPathSet markerPaths = plan != null ? plan.MarkerPaths : null;
            CaptureRunRootLayout rootLayout = markerPaths != null ? markerPaths.RootLayout : null;

            if (documents == null || plan == null || markerPaths == null || rootLayout == null)
            {
                return false;
            }

            ICaptureRunRootProvisioner stagingProvisionIssuer = stagingProvision.IssuedBy;
            ICaptureRunRootProvisioner finalProvisionIssuer = finalProvision.IssuedBy;
            if (stagingProvisionIssuer == null || !ReferenceEquals(stagingProvisionIssuer, finalProvisionIssuer))
            {
                return false;
            }

            ICaptureRunMarkerAtomicWriter stagingInitializationWriteIssuer = stagingInitializationWrite.IssuedBy;
            ICaptureRunMarkerAtomicWriter finalInitializationWriteIssuer = finalInitializationWrite.IssuedBy;
            ICaptureRunMarkerAtomicWriter stagingReadyWriteIssuer = stagingReadyWrite.IssuedBy;
            ICaptureRunMarkerAtomicWriter finalReadyWriteIssuer = finalReadyWrite.IssuedBy;
            if (stagingInitializationWriteIssuer == null
                || !ReferenceEquals(stagingInitializationWriteIssuer, finalInitializationWriteIssuer)
                || !ReferenceEquals(stagingInitializationWriteIssuer, stagingReadyWriteIssuer)
                || !ReferenceEquals(stagingInitializationWriteIssuer, finalReadyWriteIssuer))
            {
                return false;
            }

            CaptureRunRootProvisionOperation stagingProvisionOperation = stagingProvision.Operation;
            CaptureRunRootProvisionOperation finalProvisionOperation = finalProvision.Operation;

            if (stagingProvisionOperation == null
                || finalProvisionOperation == null
                || !ReferenceEquals(stagingProvisionOperation.RootLayout, rootLayout)
                || !ReferenceEquals(finalProvisionOperation.RootLayout, rootLayout)
                || stagingProvisionOperation.RootRole != CaptureRunRootRole.Staging
                || finalProvisionOperation.RootRole != CaptureRunRootRole.Final
                || !string.Equals(stagingProvisionOperation.TrustedBaseRoot, rootLayout.StagingTrustedBaseRoot, StringComparison.Ordinal)
                || !string.Equals(stagingProvisionOperation.RunRoot, rootLayout.StagingRunRoot, StringComparison.Ordinal)
                || stagingProvisionOperation.TestRunId != rootLayout.TestRunId
                || !string.Equals(finalProvisionOperation.TrustedBaseRoot, rootLayout.FinalTrustedBaseRoot, StringComparison.Ordinal)
                || !string.Equals(finalProvisionOperation.RunRoot, rootLayout.FinalRunRoot, StringComparison.Ordinal)
                || finalProvisionOperation.TestRunId != rootLayout.TestRunId)
            {
                return false;
            }

            CaptureRunMarkerWriteOperation stagingInitialization = batch.StagingInitialization;
            CaptureRunMarkerWriteOperation finalInitialization = batch.FinalInitialization;
            CaptureRunMarkerWriteOperation stagingReady = batch.StagingReady;
            CaptureRunMarkerWriteOperation finalReady = batch.FinalReady;

            if (stagingInitialization == null
                || finalInitialization == null
                || stagingReady == null
                || finalReady == null
                || !stagingInitialization.IsValid
                || !finalInitialization.IsValid
                || !stagingReady.IsValid
                || !finalReady.IsValid
                || !WriteOperationMatches(stagingInitialization, CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, markerPaths.StagingInitializationTemporaryPath, markerPaths.StagingInitializationPath)
                || !WriteOperationMatches(finalInitialization, CaptureRunRootRole.Final, CaptureRunMarkerKind.Initialization, markerPaths.FinalInitializationTemporaryPath, markerPaths.FinalInitializationPath)
                || !WriteOperationMatches(stagingReady, CaptureRunRootRole.Staging, CaptureRunMarkerKind.Ready, markerPaths.StagingReadyTemporaryPath, markerPaths.StagingReadyPath)
                || !WriteOperationMatches(finalReady, CaptureRunRootRole.Final, CaptureRunMarkerKind.Ready, markerPaths.FinalReadyTemporaryPath, markerPaths.FinalReadyPath))
            {
                return false;
            }

            if (!ReferenceEquals(stagingInitializationWrite.Operation, stagingInitialization)
                || !ReferenceEquals(finalInitializationWrite.Operation, finalInitialization)
                || !ReferenceEquals(stagingReadyWrite.Operation, stagingReady)
                || !ReferenceEquals(finalReadyWrite.Operation, finalReady)
                || !WriteReceiptMatches(stagingInitializationWrite, stagingInitialization)
                || !WriteReceiptMatches(finalInitializationWrite, finalInitialization)
                || !WriteReceiptMatches(stagingReadyWrite, stagingReady)
                || !WriteReceiptMatches(finalReadyWrite, finalReady))
            {
                return false;
            }

            if (batch.Count != 4
                || !ReferenceEquals(batch.GetOperation(0), stagingInitialization)
                || !ReferenceEquals(batch.GetOperation(1), finalInitialization)
                || !ReferenceEquals(batch.GetOperation(2), stagingReady)
                || !ReferenceEquals(batch.GetOperation(3), finalReady))
            {
                return false;
            }

            return true;
        }

        private static bool WriteOperationMatches(
            CaptureRunMarkerWriteOperation operation,
            CaptureRunRootRole expectedRole,
            CaptureRunMarkerKind expectedKind,
            string expectedTemporaryPath,
            string expectedFinalPath)
        {
            return operation.RootRole == expectedRole
                && operation.MarkerKind == expectedKind
                && string.Equals(operation.TemporaryPath, expectedTemporaryPath, StringComparison.Ordinal)
                && string.Equals(operation.FinalPath, expectedFinalPath, StringComparison.Ordinal);
        }

        private static bool WriteReceiptMatches(
            CaptureRunMarkerWriteReceipt receipt,
            CaptureRunMarkerWriteOperation operation)
        {
            return receipt.RootRole == operation.RootRole
                && receipt.MarkerKind == operation.MarkerKind
                && string.Equals(receipt.TemporaryPath, operation.TemporaryPath, StringComparison.Ordinal)
                && string.Equals(receipt.FinalPath, operation.FinalPath, StringComparison.Ordinal)
                && receipt.ByteCount == operation.ByteCount;
        }
    }
}
