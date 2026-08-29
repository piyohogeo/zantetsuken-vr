using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous coordinator that drives the fixed two-phase Capture Run
    /// initialization sequence by connecting a root provisioner and a marker
    /// atomic writer, validating every receipt immediately after each call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixed order is: provision the staging root, write the staging
    /// initialization marker, provision the final root, write the final
    /// initialization marker, write the staging ready marker, and write the
    /// final ready marker. Each receipt is fully validated before the next
    /// external call is made; a mismatched receipt stops the sequence with an
    /// <see cref="InvalidOperationException"/>. Backend exceptions propagate
    /// unchanged and never trigger retry, rollback, deletion, or cleanup.
    /// </para>
    /// <para>
    /// A partial failure may leave roots, temporary entries, or final markers
    /// on disk; resumption is the responsibility of a future recovery
    /// coordinator, not of this type. This coordinator performs no filesystem
    /// work itself, owns no batch or receipt across calls, never disposes its
    /// dependencies, and mutates no batch, document set, or canonical bytes.
    /// Thread selection is the caller's responsibility.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationExecutionCoordinator
    {
        private readonly ICaptureRunRootProvisioner _rootProvisioner;
        private readonly ICaptureRunMarkerAtomicWriter _markerWriter;

        internal CaptureRunInitializationExecutionCoordinator(
            ICaptureRunRootProvisioner rootProvisioner,
            ICaptureRunMarkerAtomicWriter markerWriter)
        {
            if (rootProvisioner == null)
            {
                throw new ArgumentNullException(nameof(rootProvisioner));
            }

            if (markerWriter == null)
            {
                throw new ArgumentNullException(nameof(markerWriter));
            }

            _rootProvisioner = rootProvisioner;
            _markerWriter = markerWriter;
        }

        internal CaptureRunInitializationExecutionReceipt Execute(CaptureRunInitializationWriteBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            CaptureRunInitializationDocumentSet documents = batch.Documents;
            if (documents == null)
            {
                throw new ArgumentException("Batch must hold a document set.", nameof(batch));
            }

            CaptureRunInitializationPlan plan = documents.Plan;
            if (plan == null)
            {
                throw new ArgumentException("Document set must hold a plan.", nameof(batch));
            }

            CaptureRunMarkerPathSet markerPaths = plan.MarkerPaths;
            if (markerPaths == null)
            {
                throw new ArgumentException("Plan must hold a marker path set.", nameof(batch));
            }

            CaptureRunRootLayout rootLayout = markerPaths.RootLayout;
            if (rootLayout == null)
            {
                throw new ArgumentException("Marker path set must hold a root layout.", nameof(batch));
            }

            if (batch.Count != 4)
            {
                throw new ArgumentException("Batch must contain exactly four operations.", nameof(batch));
            }

            CaptureRunMarkerWriteOperation stagingInitialization = batch.StagingInitialization;
            CaptureRunMarkerWriteOperation finalInitialization = batch.FinalInitialization;
            CaptureRunMarkerWriteOperation stagingReady = batch.StagingReady;
            CaptureRunMarkerWriteOperation finalReady = batch.FinalReady;

            if (stagingInitialization == null || finalInitialization == null || stagingReady == null || finalReady == null)
            {
                throw new ArgumentException("Batch must contain four non-null operations.", nameof(batch));
            }

            if (!ReferenceEquals(batch.GetOperation(0), stagingInitialization)
                || !ReferenceEquals(batch.GetOperation(1), finalInitialization)
                || !ReferenceEquals(batch.GetOperation(2), stagingReady)
                || !ReferenceEquals(batch.GetOperation(3), finalReady))
            {
                throw new ArgumentException("Batch operations do not follow the fixed order.", nameof(batch));
            }

            RequireOperationMatches(stagingInitialization, CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, markerPaths.StagingInitializationTemporaryPath, markerPaths.StagingInitializationPath);
            RequireOperationMatches(finalInitialization, CaptureRunRootRole.Final, CaptureRunMarkerKind.Initialization, markerPaths.FinalInitializationTemporaryPath, markerPaths.FinalInitializationPath);
            RequireOperationMatches(stagingReady, CaptureRunRootRole.Staging, CaptureRunMarkerKind.Ready, markerPaths.StagingReadyTemporaryPath, markerPaths.StagingReadyPath);
            RequireOperationMatches(finalReady, CaptureRunRootRole.Final, CaptureRunMarkerKind.Ready, markerPaths.FinalReadyTemporaryPath, markerPaths.FinalReadyPath);

            CaptureRunRootProvisionOperation stagingProvisionOperation = new CaptureRunRootProvisionOperation(rootLayout, CaptureRunRootRole.Staging);
            CaptureRunRootProvisionReceipt stagingProvisionReceipt = ValidateProvisionReceipt(
                _rootProvisioner, stagingProvisionOperation, _rootProvisioner.ProvisionNew(stagingProvisionOperation));

            CaptureRunMarkerWriteReceipt stagingInitializationWriteReceipt = ValidateWriteReceipt(
                _markerWriter, stagingInitialization, _markerWriter.WriteAtomic(stagingInitialization));

            CaptureRunRootProvisionOperation finalProvisionOperation = new CaptureRunRootProvisionOperation(rootLayout, CaptureRunRootRole.Final);
            CaptureRunRootProvisionReceipt finalProvisionReceipt = ValidateProvisionReceipt(
                _rootProvisioner, finalProvisionOperation, _rootProvisioner.ProvisionNew(finalProvisionOperation));

            CaptureRunMarkerWriteReceipt finalInitializationWriteReceipt = ValidateWriteReceipt(
                _markerWriter, finalInitialization, _markerWriter.WriteAtomic(finalInitialization));

            CaptureRunMarkerWriteReceipt stagingReadyWriteReceipt = ValidateWriteReceipt(
                _markerWriter, stagingReady, _markerWriter.WriteAtomic(stagingReady));

            CaptureRunMarkerWriteReceipt finalReadyWriteReceipt = ValidateWriteReceipt(
                _markerWriter, finalReady, _markerWriter.WriteAtomic(finalReady));

            return new CaptureRunInitializationExecutionReceipt(
                batch,
                stagingProvisionReceipt,
                finalProvisionReceipt,
                stagingInitializationWriteReceipt,
                finalInitializationWriteReceipt,
                stagingReadyWriteReceipt,
                finalReadyWriteReceipt);
        }

        private static void RequireOperationMatches(
            CaptureRunMarkerWriteOperation operation,
            CaptureRunRootRole expectedRole,
            CaptureRunMarkerKind expectedKind,
            string expectedTemporaryPath,
            string expectedFinalPath)
        {
            if (operation.RootRole != expectedRole
                || operation.MarkerKind != expectedKind
                || !string.Equals(operation.TemporaryPath, expectedTemporaryPath, StringComparison.Ordinal)
                || !string.Equals(operation.FinalPath, expectedFinalPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("A batch operation does not match the marker path set.", "batch");
            }
        }

        private static CaptureRunRootProvisionReceipt ValidateProvisionReceipt(
            ICaptureRunRootProvisioner expectedIssuer,
            CaptureRunRootProvisionOperation expectedOperation,
            CaptureRunRootProvisionReceipt receipt)
        {
            if (receipt == null)
            {
                throw new InvalidOperationException("Provisioner returned no receipt.");
            }

            if (!receipt.IsValid)
            {
                throw new InvalidOperationException("Provisioner returned an invalid receipt.");
            }

            if (!ReferenceEquals(receipt.IssuedBy, expectedIssuer))
            {
                throw new InvalidOperationException("Provision receipt was issued by an unexpected provisioner.");
            }

            if (!ReferenceEquals(receipt.Operation, expectedOperation))
            {
                throw new InvalidOperationException("Provision receipt corresponds to an unexpected operation.");
            }

            if (!ReferenceEquals(receipt.RootLayout, expectedOperation.RootLayout)
                || receipt.RootRole != expectedOperation.RootRole
                || !string.Equals(receipt.TrustedBaseRoot, expectedOperation.TrustedBaseRoot, StringComparison.Ordinal)
                || !string.Equals(receipt.RunRoot, expectedOperation.RunRoot, StringComparison.Ordinal)
                || receipt.TestRunId != expectedOperation.TestRunId)
            {
                throw new InvalidOperationException("Provision receipt values do not match the operation.");
            }

            return receipt;
        }

        private static CaptureRunMarkerWriteReceipt ValidateWriteReceipt(
            ICaptureRunMarkerAtomicWriter expectedIssuer,
            CaptureRunMarkerWriteOperation expectedOperation,
            CaptureRunMarkerWriteReceipt receipt)
        {
            if (receipt == null)
            {
                throw new InvalidOperationException("Writer returned no receipt.");
            }

            if (!receipt.IsValid)
            {
                throw new InvalidOperationException("Writer returned an invalid receipt.");
            }

            if (!ReferenceEquals(receipt.IssuedBy, expectedIssuer))
            {
                throw new InvalidOperationException("Write receipt was issued by an unexpected writer.");
            }

            if (!ReferenceEquals(receipt.Operation, expectedOperation))
            {
                throw new InvalidOperationException("Write receipt corresponds to an unexpected operation.");
            }

            if (receipt.RootRole != expectedOperation.RootRole
                || receipt.MarkerKind != expectedOperation.MarkerKind
                || !string.Equals(receipt.TemporaryPath, expectedOperation.TemporaryPath, StringComparison.Ordinal)
                || !string.Equals(receipt.FinalPath, expectedOperation.FinalPath, StringComparison.Ordinal)
                || receipt.ByteCount != expectedOperation.ByteCount)
            {
                throw new InvalidOperationException("Write receipt values do not match the operation.");
            }

            return receipt;
        }
    }
}
