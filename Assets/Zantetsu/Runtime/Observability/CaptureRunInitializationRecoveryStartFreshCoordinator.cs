using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Continues a recovery orchestration that resolved to StartFreshRequired
    /// by running the normal two-phase initialization under the already-held
    /// lock lease, with a newly issued initialization ID, and transferring
    /// lease ownership to a fresh session only on success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holds exactly two readonly dependencies — the initialization ID source
    /// and the fresh initialization execution coordinator — and is not an
    /// <see cref="IDisposable"/>. It never holds or calls a lock acquisition
    /// coordinator, a bootstrap coordinator, an inspector, or a recovery
    /// execution coordinator.
    /// </para>
    /// <para>
    /// All pre-validation happens before any side effect or ID issuance. The
    /// ID is issued exactly once, the document set and write batch are built,
    /// the execution coordinator runs once, and the receipt is verified
    /// immediately. The session factory call is the single linearization point
    /// of ownership transfer; no validation or exception-raising work follows
    /// a successful factory call.
    /// </para>
    /// <para>
    /// On failure the caller's lease reference stays unchanged and ownership
    /// remains with the caller; this coordinator never disposes the lease. No
    /// exception is transformed or wrapped, and no retry, rollback,
    /// compensating deletion, or automatic re-inspection is performed. A
    /// partial execution failure may leave roots, temporary entries, or
    /// markers on disk; the caller must restart from a fresh inspection under
    /// the same held lock. A failed run must not blindly re-run the same
    /// recovery result or write batch, and a replacement ID is never issued
    /// within the same call after an earlier issuance.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryStartFreshCoordinator
    {
        private readonly ICaptureRunInitializationIdSource _initializationIdSource;
        private readonly CaptureRunInitializationExecutionCoordinator _executionCoordinator;

        internal CaptureRunInitializationRecoveryStartFreshCoordinator(
            ICaptureRunInitializationIdSource initializationIdSource,
            CaptureRunInitializationExecutionCoordinator executionCoordinator)
        {
            if (initializationIdSource == null)
            {
                throw new ArgumentNullException(nameof(initializationIdSource));
            }

            if (executionCoordinator == null)
            {
                throw new ArgumentNullException(nameof(executionCoordinator));
            }

            _initializationIdSource = initializationIdSource;
            _executionCoordinator = executionCoordinator;
        }

        internal ICaptureRunInitializationIdSource InitializationIdSource => _initializationIdSource;

        internal CaptureRunInitializationExecutionCoordinator ExecutionCoordinator => _executionCoordinator;

        internal CaptureRunInitializationSession Continue(
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult,
            ref CaptureRunLockLease lockLease)
        {
            if (recoveryResult == null)
            {
                throw new ArgumentNullException(nameof(recoveryResult));
            }

            if (lockLease == null)
            {
                throw new ArgumentNullException(nameof(lockLease));
            }

            if (!recoveryResult.IsValid)
            {
                throw new ArgumentException("Recovery orchestration result must be valid.", nameof(recoveryResult));
            }

            if (!lockLease.IsCreated)
            {
                throw new ArgumentException("Lock lease must be created.", nameof(lockLease));
            }

            if (recoveryResult.Status != CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired)
            {
                throw new ArgumentException("Recovery orchestration result must require a start-fresh continuation.", nameof(recoveryResult));
            }

            if (recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.StartFresh
                && recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.CleanupTemporaryAndStartFresh)
            {
                throw new ArgumentException("Recovery orchestration result must be a start-fresh disposition.", nameof(recoveryResult));
            }

            if (recoveryResult.RunInitializationId != null)
            {
                throw new ArgumentException("Start-fresh recovery result must not carry a run initialization ID.", nameof(recoveryResult));
            }

            if (recoveryResult.Batch.ExpectedBinding != null)
            {
                throw new ArgumentException("Start-fresh recovery result must not carry an expected binding.", nameof(recoveryResult));
            }

            if (!ReferenceEquals(recoveryResult.LockLease, lockLease))
            {
                throw new ArgumentException("Recovery result lease must be the lease being continued.", nameof(lockLease));
            }

            CaptureRunLockPathSet pathSet = lockLease.PathSet;
            if (pathSet == null)
            {
                throw new ArgumentException("Lock lease must hold a path set.", nameof(lockLease));
            }

            if (!ReferenceEquals(pathSet.RootLayout, recoveryResult.RootLayout))
            {
                throw new ArgumentException("Lock lease and recovery result must share the same root layout.", nameof(recoveryResult));
            }

            if (pathSet.RootLayout.TestRunId != recoveryResult.TestRunId)
            {
                throw new ArgumentException("Lock lease and recovery result must share the same test run ID.", nameof(recoveryResult));
            }

            string runInitializationId = _initializationIdSource.Create();

            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(recoveryResult.RootLayout, runInitializationId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);

            CaptureRunInitializationExecutionReceipt receipt = _executionCoordinator.Execute(batch);

            if (receipt == null
                || !receipt.IsValid
                || !ReferenceEquals(receipt.RootLayout, recoveryResult.RootLayout)
                || receipt.TestRunId != recoveryResult.TestRunId
                || !string.Equals(receipt.RunInitializationId, runInitializationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Execution receipt must be valid and match the issued initialization.");
            }

            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);

            return CaptureRunInitializationSessionFactory.Create(ref lockLease, evidence);
        }
    }
}
