using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Atomically associates a capture frame record with a render target lease:
    /// the lease is registered into the
    /// <see cref="CaptureFrameRenderTargetLeaseRegistry"/> before the record is
    /// scheduled, and rolled back if the record cannot be scheduled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On entry the lease is owned by the caller. On a <c>true</c> result the
    /// lease's logical ownership transfers to the lease registry (and the render
    /// target pool keeps the slot rented). On <c>false</c> the lease is rolled
    /// back and ownership returns to the caller, who is responsible for
    /// returning it to the pool. On an exception the same rollback is attempted;
    /// when it succeeds the caller regains ownership.
    /// </para>
    /// <para>
    /// This type never calls <c>CaptureFrameRenderTargetPool.Return</c> and does
    /// not clear the queue or the registries. If the rollback invariant is
    /// violated (the lease cannot be removed, or the removed lease is not
    /// identical to the registered one), the failure is surfaced as an
    /// <see cref="InvalidOperationException"/> and no lease is guessed or
    /// returned; the state is left fail-closed.
    /// </para>
    /// <para>
    /// Does not own, dispose, or clear the record scheduler, the lease registry,
    /// the pool, or any record, and does not record trace events or generate
    /// capture frame IDs. Main-thread only and <b>not</b> thread-safe.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRenderTargetRecordScheduler
    {
        private readonly CaptureFrameRecordScheduler _recordScheduler;
        private readonly CaptureFrameRenderTargetLeaseRegistry _leaseRegistry;

        public CaptureFrameRenderTargetRecordScheduler(
            CaptureFrameRecordScheduler recordScheduler,
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry)
        {
            if (recordScheduler == null)
            {
                throw new ArgumentNullException(nameof(recordScheduler));
            }

            if (leaseRegistry == null)
            {
                throw new ArgumentNullException(nameof(leaseRegistry));
            }

            _recordScheduler = recordScheduler;
            _leaseRegistry = leaseRegistry;
        }

        /// <summary>
        /// Registers the lease for the record's request and then schedules the
        /// record. Returns <c>true</c> only when both the lease registration
        /// and the record schedule succeed.
        /// </summary>
        public bool TrySchedule(CaptureFrameRecord record, in CaptureFrameRenderTargetLease lease)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            CaptureFrameRequest request = record.Request;

            if (!_leaseRegistry.TryRegister(request, lease))
            {
                // Lease registry full: the record scheduler is not touched and
                // the lease remains owned by the caller.
                return false;
            }

            bool scheduled;
            try
            {
                scheduled = _recordScheduler.TrySchedule(record);
            }
            catch (Exception schedulingException)
            {
                try
                {
                    RollbackLease(request, lease);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(schedulingException, rollbackException);
                }

                ExceptionDispatchInfo.Capture(schedulingException).Throw();
                return false;
            }

            if (!scheduled)
            {
                RollbackLease(request, lease);
                return false;
            }

            return true;
        }

        private void RollbackLease(in CaptureFrameRequest request, in CaptureFrameRenderTargetLease expectedLease)
        {
            if (!_leaseRegistry.TryRemove(request, out CaptureFrameRenderTargetLease removedLease))
            {
                throw new InvalidOperationException("Rollback failed: no render target lease is registered for the scheduled capture frame.");
            }

            if (!removedLease.IdenticalTo(expectedLease))
            {
                throw new InvalidOperationException("Rollback failed: the removed render target lease does not match the registered lease.");
            }
        }
    }
}
