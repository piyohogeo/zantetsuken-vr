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
                rootLayout.RunRootSha256);

            CaptureRunMarkerWriteOperation initialization = new CaptureRunMarkerWriteOperation(
                CaptureRunMarkerKind.Initialization,
                markerPaths.InitializationTemporaryPath,
                markerPaths.InitializationPath,
                CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.Initialization));

            CaptureRunMarkerWriteOperation ready = new CaptureRunMarkerWriteOperation(
                CaptureRunMarkerKind.Ready,
                markerPaths.ReadyTemporaryPath,
                markerPaths.ReadyPath,
                CaptureRunReadyMarkerCodec.SerializeCanonical(binding.Ready));

            CaptureRunRootProvisionOperation provisionOperation = new CaptureRunRootProvisionOperation(rootLayout);
            CaptureRunRootProvisionReceipt provisionReceipt = ValidateProvisionReceipt(
                _rootProvisioner, provisionOperation, _rootProvisioner.ProvisionNew(provisionOperation));

            CaptureRunMarkerWriteReceipt initializationWriteReceipt = ValidateWriteReceipt(
                _markerWriter, initialization, _markerWriter.WriteAtomic(initialization));

            CaptureRunMarkerWriteReceipt readyWriteReceipt = ValidateWriteReceipt(
                _markerWriter, ready, _markerWriter.WriteAtomic(ready));

            return new CaptureRunInitializationExecutionReceipt(
                markerPaths,
                binding.RunInitializationId,
                provisionReceipt,
                initializationWriteReceipt,
                readyWriteReceipt);
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

            return receipt;
        }
    }
}
