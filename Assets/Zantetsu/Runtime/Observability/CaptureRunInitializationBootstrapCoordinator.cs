using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Drives the full Capture Run initialization bootstrap: acquire both OS
    /// locks, issue the initialization ID exactly once, build the document set
    /// and write batch, execute the two-phase initialization, and hand the
    /// lock lease to a session that owns it for the Run's lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This coordinator is main-thread only and not thread-safe in this stage,
    /// because the underlying lock acquisition coordinator is. Lock contention
    /// is ordinary backpressure: it returns false without issuing an ID or
    /// touching documents, markers, or roots. On any failure after the locks
    /// are acquired, the lease is released in reverse acquisition order and
    /// the original exception is re-thrown with its original stack; if lease
    /// cleanup also fails, an <see cref="AggregateException"/> with the
    /// original exception first is raised.
    /// </para>
    /// <para>
    /// A partial execution failure may leave roots, temporary entries, or
    /// markers on disk; a future recovery pass re-acquires the locks and
    /// reconciles. This coordinator performs no filesystem work itself, holds
    /// only its three dependencies, and never disposes them.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationBootstrapCoordinator
    {
        private readonly CaptureRunLockAcquisitionCoordinator _lockCoordinator;
        private readonly ICaptureRunInitializationIdSource _initializationIdSource;
        private readonly CaptureRunInitializationExecutionCoordinator _executionCoordinator;

        internal CaptureRunInitializationBootstrapCoordinator(
            CaptureRunLockAcquisitionCoordinator lockCoordinator,
            ICaptureRunInitializationIdSource initializationIdSource,
            CaptureRunInitializationExecutionCoordinator executionCoordinator)
        {
            if (lockCoordinator == null)
            {
                throw new ArgumentNullException(nameof(lockCoordinator));
            }

            if (initializationIdSource == null)
            {
                throw new ArgumentNullException(nameof(initializationIdSource));
            }

            if (executionCoordinator == null)
            {
                throw new ArgumentNullException(nameof(executionCoordinator));
            }

            _lockCoordinator = lockCoordinator;
            _initializationIdSource = initializationIdSource;
            _executionCoordinator = executionCoordinator;
        }

        internal bool TryInitialize(
            CaptureRunRootLayout rootLayout,
            out CaptureRunInitializationSession session)
        {
            session = null;

            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(rootLayout);

            CaptureRunLockLease lease;
            bool acquired = _lockCoordinator.TryAcquire(pathSet, out lease);

            if (!acquired)
            {
                return false;
            }

            try
            {
                if (lease == null)
                {
                    throw new InvalidOperationException("Lock acquisition returned true with a null lease.");
                }

                if (!lease.IsCreated)
                {
                    throw new InvalidOperationException("Lock acquisition returned an uncreated lease.");
                }

                if (!ReferenceEquals(lease.PathSet, pathSet))
                {
                    throw new InvalidOperationException("Lock lease path set does not match the requested path set.");
                }

                string runInitializationId = _initializationIdSource.Create();

                CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(rootLayout, runInitializationId);
                CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);

                CaptureRunInitializationExecutionReceipt executionReceipt = _executionCoordinator.Execute(batch);

                if (executionReceipt == null)
                {
                    throw new InvalidOperationException("Execution returned no receipt.");
                }

                if (!executionReceipt.IsValid)
                {
                    throw new InvalidOperationException("Execution returned an invalid receipt.");
                }

                if (!ReferenceEquals(executionReceipt.RootLayout, rootLayout))
                {
                    throw new InvalidOperationException("Execution receipt root layout does not match the input.");
                }

                if (!string.Equals(executionReceipt.RunInitializationId, runInitializationId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Execution receipt initialization ID does not match the issued ID.");
                }

                CaptureRunInitializationSession createdSession = new CaptureRunInitializationSession(lease, executionReceipt);

                lease = null;
                session = createdSession;
                return true;
            }
            catch (Exception ex)
            {
                ExceptionDispatchInfo captured = ExceptionDispatchInfo.Capture(ex);

                if (lease != null)
                {
                    try
                    {
                        lease.Dispose();
                    }
                    catch (Exception cleanupEx)
                    {
                        throw new AggregateException(
                            "Initialization failed and lock lease cleanup also failed.",
                            new Exception[] { ex, cleanupEx });
                    }
                }

                captured.Throw();
                throw;
            }
        }
    }
}
