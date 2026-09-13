using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable token returned after a Capture Run initialization sequence has
    /// fully succeeded. It correlates the Run's marker paths and initialization
    /// id with the provision receipt and the two write receipts produced along
    /// the way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor re-verifies the correlations it can see rather than
    /// trusting the coordinator: all four references must be non-null, the two
    /// write receipts must share one issuer, the provision operation must be
    /// the marker paths' root layout, and the operation each write receipt
    /// names must be one of the two the marker paths describe.
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
    /// the marker paths and hold no copied value. This type performs no
    /// filesystem work and is not an <see cref="IDisposable"/>, MonoBehaviour,
    /// or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationExecutionReceipt
    {
        private readonly CaptureRunMarkerPathSet _markerPaths;
        private readonly string _runInitializationId;
        private readonly CaptureRunRootProvisionReceipt _provision;
        private readonly CaptureRunMarkerWriteReceipt _initializationWrite;
        private readonly CaptureRunMarkerWriteReceipt _readyWrite;

        internal CaptureRunInitializationExecutionReceipt(
            CaptureRunMarkerPathSet markerPaths,
            string runInitializationId,
            CaptureRunRootProvisionReceipt provision,
            CaptureRunMarkerWriteReceipt initializationWrite,
            CaptureRunMarkerWriteReceipt readyWrite)
        {
            if (markerPaths == null)
            {
                throw new ArgumentNullException(nameof(markerPaths));
            }

            if (runInitializationId == null)
            {
                throw new ArgumentNullException(nameof(runInitializationId));
            }

            if (provision == null)
            {
                throw new ArgumentNullException(nameof(provision));
            }

            if (initializationWrite == null)
            {
                throw new ArgumentNullException(nameof(initializationWrite));
            }

            if (readyWrite == null)
            {
                throw new ArgumentNullException(nameof(readyWrite));
            }

            if (!CorrelationsHold(markerPaths, runInitializationId, provision, initializationWrite, readyWrite))
            {
                throw new ArgumentException("Execution receipt inputs are not mutually correlated.");
            }

            _markerPaths = markerPaths;
            _runInitializationId = runInitializationId;
            _provision = provision;
            _initializationWrite = initializationWrite;
            _readyWrite = readyWrite;
        }

        internal CaptureRunRootProvisionReceipt Provision => _provision;

        internal CaptureRunMarkerWriteReceipt InitializationWrite => _initializationWrite;

        internal CaptureRunMarkerWriteReceipt ReadyWrite => _readyWrite;

        internal CaptureRunMarkerPathSet MarkerPaths => _markerPaths;

        internal CaptureRunRootLayout RootLayout => _markerPaths.RootLayout;

        internal long TestRunId => _markerPaths.RootLayout.TestRunId;

        internal string RunInitializationId => _runInitializationId;

        internal bool IsValid =>
            CorrelationsHold(_markerPaths, _runInitializationId, _provision, _initializationWrite, _readyWrite);

        private static bool CorrelationsHold(
            CaptureRunMarkerPathSet markerPaths,
            string runInitializationId,
            CaptureRunRootProvisionReceipt provision,
            CaptureRunMarkerWriteReceipt initializationWrite,
            CaptureRunMarkerWriteReceipt readyWrite)
        {
            if (markerPaths == null
                || runInitializationId == null
                || provision == null
                || initializationWrite == null
                || readyWrite == null)
            {
                return false;
            }

            if (!provision.IsValid || !initializationWrite.IsValid || !readyWrite.IsValid)
            {
                return false;
            }

            CaptureRunRootLayout rootLayout = markerPaths.RootLayout;
            if (rootLayout == null || provision.IssuedBy == null)
            {
                return false;
            }

            ICaptureRunMarkerAtomicWriter writeIssuer = initializationWrite.IssuedBy;
            if (writeIssuer == null || !ReferenceEquals(writeIssuer, readyWrite.IssuedBy))
            {
                return false;
            }

            CaptureRunRootProvisionOperation provisionOperation = provision.Operation;
            if (provisionOperation == null
                || !ReferenceEquals(provisionOperation.RootLayout, rootLayout)
                || !string.Equals(provisionOperation.TrustedBaseRoot, rootLayout.TrustedBaseRoot, StringComparison.Ordinal)
                || !string.Equals(provisionOperation.RunRoot, rootLayout.RunRoot, StringComparison.Ordinal)
                || provisionOperation.TestRunId != rootLayout.TestRunId)
            {
                return false;
            }

            CaptureRunMarkerWriteOperation initialization = initializationWrite.Operation;
            CaptureRunMarkerWriteOperation ready = readyWrite.Operation;

            return initialization != null
                && ready != null
                && initialization.IsValid
                && ready.IsValid
                && WriteOperationMatches(
                    initialization,
                    CaptureRunMarkerKind.Initialization,
                    markerPaths.InitializationTemporaryPath,
                    markerPaths.InitializationPath)
                && WriteOperationMatches(
                    ready,
                    CaptureRunMarkerKind.Ready,
                    markerPaths.ReadyTemporaryPath,
                    markerPaths.ReadyPath);
        }

        private static bool WriteOperationMatches(
            CaptureRunMarkerWriteOperation operation,
            CaptureRunMarkerKind expectedKind,
            string expectedTemporaryPath,
            string expectedFinalPath)
        {
            return operation.MarkerKind == expectedKind
                && string.Equals(operation.TemporaryPath, expectedTemporaryPath, StringComparison.Ordinal)
                && string.Equals(operation.FinalPath, expectedFinalPath, StringComparison.Ordinal);
        }
    }
}
