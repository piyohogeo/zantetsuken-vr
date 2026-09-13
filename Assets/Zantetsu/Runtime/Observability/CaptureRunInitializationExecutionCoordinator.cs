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
    /// coordinator, not of this type. The marker paths, the marker binding,
    /// the three canonical byte arrays and the four write operations are all
    /// produced before any backend is called, so a rejected root layout or
    /// initialization id stops the sequence before the first side effect. The
    /// two ready operations share the one ready array, which nothing mutates
    /// after it is serialized. This coordinator performs no filesystem work
    /// itself, owns no operation or receipt across calls, never disposes its
    /// dependencies, and mutates no canonical bytes.
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

        internal CaptureRunInitializationExecutionReceipt Execute(
            CaptureRunRootLayout rootLayout,
            string runInitializationId)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(rootLayout);

            CaptureRunMarkerBinding binding = new CaptureRunMarkerBinding(
                rootLayout.TestRunId,
                runInitializationId,
                rootLayout.StagingRunRootSha256,
                rootLayout.FinalRunRootSha256);

            byte[] stagingInitializationBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.StagingInitialization);
            byte[] finalInitializationBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.FinalInitialization);
            byte[] readyBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady);

            CaptureRunMarkerWriteOperation stagingInitialization = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging,
                CaptureRunMarkerKind.Initialization,
                markerPaths.StagingInitializationTemporaryPath,
                markerPaths.StagingInitializationPath,
                stagingInitializationBytes);

            CaptureRunMarkerWriteOperation finalInitialization = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Final,
                CaptureRunMarkerKind.Initialization,
                markerPaths.FinalInitializationTemporaryPath,
                markerPaths.FinalInitializationPath,
                finalInitializationBytes);

            CaptureRunMarkerWriteOperation stagingReady = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging,
                CaptureRunMarkerKind.Ready,
                markerPaths.StagingReadyTemporaryPath,
                markerPaths.StagingReadyPath,
                readyBytes);

            CaptureRunMarkerWriteOperation finalReady = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Final,
                CaptureRunMarkerKind.Ready,
                markerPaths.FinalReadyTemporaryPath,
                markerPaths.FinalReadyPath,
                readyBytes);

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
                markerPaths,
                binding.RunInitializationId,
                stagingProvisionReceipt,
                finalProvisionReceipt,
                stagingInitializationWriteReceipt,
                finalInitializationWriteReceipt,
                stagingReadyWriteReceipt,
                finalReadyWriteReceipt);
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
