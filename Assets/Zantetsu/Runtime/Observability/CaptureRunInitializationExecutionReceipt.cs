using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable token returned after a Capture Run initialization sequence has
    /// fully succeeded. It correlates the Run's marker paths and initialization
    /// id with the two provision receipts and four write receipts produced
    /// along the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor re-verifies the correlations it can see rather than
    /// trusting the coordinator: all eight references must be non-null, the two
    /// provision receipts must share one issuer, the four write receipts must
    /// share one issuer, the provision operations must be the staging and final
    /// operations of the marker paths' root layout, and each write receipt must
    /// describe an operation that is one of the four the marker paths name.
    /// <see cref="IsValid"/> recomputes the same checks from the stored values
    /// without an independent flag.
    /// </para>
    /// <para>
    /// The initialization id is kept as the coordinator supplied it and is only
    /// ever checked for being present. It is never re-derived from the marker
    /// bytes, and no marker is decoded or hashed here to corroborate it.
    /// </para>
    /// <para>
    /// <see cref="RootLayout"/> and <see cref="TestRunId"/> are forwarded from
    /// the marker paths and hold no copied value. This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationExecutionReceipt
    {
        private readonly CaptureRunMarkerPathSet _markerPaths;
        private readonly string _runInitializationId;
        private readonly CaptureRunRootProvisionReceipt _stagingProvision;
        private readonly CaptureRunRootProvisionReceipt _finalProvision;
        private readonly CaptureRunMarkerWriteReceipt _stagingInitializationWrite;
        private readonly CaptureRunMarkerWriteReceipt _finalInitializationWrite;
        private readonly CaptureRunMarkerWriteReceipt _stagingReadyWrite;
        private readonly CaptureRunMarkerWriteReceipt _finalReadyWrite;

        internal CaptureRunInitializationExecutionReceipt(
            CaptureRunMarkerPathSet markerPaths,
            string runInitializationId,
            CaptureRunRootProvisionReceipt stagingProvision,
            CaptureRunRootProvisionReceipt finalProvision,
            CaptureRunMarkerWriteReceipt stagingInitializationWrite,
            CaptureRunMarkerWriteReceipt finalInitializationWrite,
            CaptureRunMarkerWriteReceipt stagingReadyWrite,
            CaptureRunMarkerWriteReceipt finalReadyWrite)
        {
            if (markerPaths == null)
            {
                throw new ArgumentNullException(nameof(markerPaths));
            }

            if (runInitializationId == null)
            {
                throw new ArgumentNullException(nameof(runInitializationId));
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

            if (!CorrelationsHold(markerPaths, runInitializationId, stagingProvision, finalProvision, stagingInitializationWrite, finalInitializationWrite, stagingReadyWrite, finalReadyWrite))
            {
                throw new ArgumentException("Execution receipt inputs are not mutually correlated.");
            }

            _markerPaths = markerPaths;
            _runInitializationId = runInitializationId;
            _stagingProvision = stagingProvision;
            _finalProvision = finalProvision;
            _stagingInitializationWrite = stagingInitializationWrite;
            _finalInitializationWrite = finalInitializationWrite;
            _stagingReadyWrite = stagingReadyWrite;
            _finalReadyWrite = finalReadyWrite;
        }

        internal CaptureRunRootProvisionReceipt StagingProvision => _stagingProvision;

        internal CaptureRunRootProvisionReceipt FinalProvision => _finalProvision;

        internal CaptureRunMarkerWriteReceipt StagingInitializationWrite => _stagingInitializationWrite;

        internal CaptureRunMarkerWriteReceipt FinalInitializationWrite => _finalInitializationWrite;

        internal CaptureRunMarkerWriteReceipt StagingReadyWrite => _stagingReadyWrite;

        internal CaptureRunMarkerWriteReceipt FinalReadyWrite => _finalReadyWrite;

        internal CaptureRunMarkerPathSet MarkerPaths => _markerPaths;

        internal CaptureRunRootLayout RootLayout => _markerPaths.RootLayout;

        internal long TestRunId => _markerPaths.RootLayout.TestRunId;

        internal string RunInitializationId => _runInitializationId;

        internal bool IsValid => CorrelationsHold(_markerPaths, _runInitializationId, _stagingProvision, _finalProvision, _stagingInitializationWrite, _finalInitializationWrite, _stagingReadyWrite, _finalReadyWrite);

        private static bool CorrelationsHold(
            CaptureRunMarkerPathSet markerPaths,
            string runInitializationId,
            CaptureRunRootProvisionReceipt stagingProvision,
            CaptureRunRootProvisionReceipt finalProvision,
            CaptureRunMarkerWriteReceipt stagingInitializationWrite,
            CaptureRunMarkerWriteReceipt finalInitializationWrite,
            CaptureRunMarkerWriteReceipt stagingReadyWrite,
            CaptureRunMarkerWriteReceipt finalReadyWrite)
        {
            if (markerPaths == null
                || runInitializationId == null
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

            CaptureRunRootLayout rootLayout = markerPaths.RootLayout;
            if (rootLayout == null)
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

            CaptureRunMarkerWriteOperation stagingInitialization = stagingInitializationWrite.Operation;
            CaptureRunMarkerWriteOperation finalInitialization = finalInitializationWrite.Operation;
            CaptureRunMarkerWriteOperation stagingReady = stagingReadyWrite.Operation;
            CaptureRunMarkerWriteOperation finalReady = finalReadyWrite.Operation;

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

            if (!WriteReceiptMatches(stagingInitializationWrite, stagingInitialization)
                || !WriteReceiptMatches(finalInitializationWrite, finalInitialization)
                || !WriteReceiptMatches(stagingReadyWrite, stagingReady)
                || !WriteReceiptMatches(finalReadyWrite, finalReady))
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
