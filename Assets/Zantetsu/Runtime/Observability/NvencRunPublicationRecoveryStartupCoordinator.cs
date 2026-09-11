using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The application-side owner of Phase 0.11 NVENC recovery: it opens a
    /// Capture Run through the existing initialization entry and, when that
    /// entry reports a publication recovery, composes and starts exactly one
    /// recovery worker from the existing factory and holds that worker's
    /// lifecycle until its terminal is collected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type exists because no production owner started a Run yet: the
    /// initialization entry coordinator and the recovery worker factory had no
    /// caller. It is a plain coordinator - no MonoBehaviour, no host, no
    /// factory interface, no dependency bundle, and no service locator - and it
    /// adds no classification, receipt, or status model of its own. The two
    /// collaborators it holds are the exact entry coordinator and the exact
    /// worker factory, and the one process state and one verification buffer
    /// pool the process shares are the factory's; nothing here creates a
    /// second one or re-implements the factory's Running admission.
    /// </para>
    /// <para>
    /// Every entry is main-thread. <see cref="TryOpen"/> performs the lock
    /// acquisition and the initialization recovery inspection synchronously,
    /// as the entry coordinator always has, and no claim is made here about
    /// how long a filesystem or OS call takes; what it does not wait for is
    /// the publication recovery worker it started.
    /// <see cref="TryOpen"/> returns false on ordinary lock contention without
    /// composing anything. A session-ready or collision outcome is handed back
    /// exactly as the entry produced it - the same outcome and the same
    /// ownership lease, with no recovery type in between - and stays the
    /// caller's to own. A publication recovery outcome is passed to the factory
    /// once, the returned worker is started once, and the lease that outcome
    /// was correlated with becomes the worker's: it is never disposed here.
    /// One active recovery is held at a time, so a Run can never be given two
    /// workers, and a second open while one is active is refused.
    /// </para>
    /// <para>
    /// Once started, the worker is observed only through its own non-waiting
    /// reads. <see cref="TryCollectRecoveryTerminal"/> collects the terminal at
    /// most once, and only after the worker has physically stopped with one;
    /// it then disposes that worker and frees the slot. A worker parked for a
    /// release retry is left parked - nothing here turns it into a success or a
    /// failure - and only an explicit
    /// <see cref="TryRequestRecoveryReleaseRetry"/> resumes it, forwarded to
    /// the worker that decides whether the request is admissible at all. This
    /// unit adds no retry policy, no attempt counter, no deadline, no
    /// automatic re-entry, and no forced unlock. A faulted worker therefore keeps its
    /// slot, its worker instance, and whatever lock its lease still holds:
    /// neither a released lock nor a terminal result is inferred from a fault.
    /// </para>
    /// <para>
    /// If the factory refuses to compose - the process is no longer Running -
    /// or if the worker could not be started, ownership never reached the
    /// worker: the unstarted worker is disposed, the lease the entry produced
    /// is released, and the original exception is re-thrown with its original
    /// stack. A cleanup failure never replaces it; it is attached to an
    /// <see cref="AggregateException"/> whose first exception is the original.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryStartupCoordinator
    {
        private readonly CaptureRunInitializationEntryCoordinator _entryCoordinator;
        private readonly NvencRunPublicationRecoveryWorkerFactory _workerFactory;

        private NvencRunPublicationRecoveryWorkerService _worker;
        private CaptureRunInitializationOpenOutcome _recoveryOpenOutcome;
        private CaptureRunInitializationSessionOwnershipLease _recoveryOwnershipLease;

        internal NvencRunPublicationRecoveryStartupCoordinator(
            CaptureRunInitializationEntryCoordinator entryCoordinator,
            NvencRunPublicationRecoveryWorkerFactory workerFactory)
        {
            _entryCoordinator = entryCoordinator
                ?? throw new ArgumentNullException(nameof(entryCoordinator));
            _workerFactory = workerFactory
                ?? throw new ArgumentNullException(nameof(workerFactory));
        }

        /// <summary>
        /// True while this owner holds a started recovery worker whose terminal
        /// has not been collected.
        /// </summary>
        internal bool HasActiveRecovery => _worker != null;

        /// <summary>
        /// The exact open outcome the active recovery is running for, or null
        /// when no recovery is active.
        /// </summary>
        internal CaptureRunInitializationOpenOutcome ActiveRecoveryOpenOutcome =>
            _recoveryOpenOutcome;

        /// <summary>
        /// The exact ownership lease the active recovery owns, or null when no
        /// recovery is active. It is exposed for correlation and observation
        /// only: while a worker owns it, it is released by that worker's own
        /// release stage and by nothing else.
        /// </summary>
        internal CaptureRunInitializationSessionOwnershipLease ActiveRecoveryOwnershipLease =>
            _recoveryOwnershipLease;

        /// <summary>
        /// The active recovery worker's own state, or
        /// <see cref="NvencRunPublicationRecoveryWorkerState.NotStarted"/> when
        /// no recovery is active. One non-waiting read.
        /// </summary>
        internal NvencRunPublicationRecoveryWorkerState RecoveryWorkerState
        {
            get
            {
                NvencRunPublicationRecoveryWorkerService worker = _worker;
                return worker != null
                    ? worker.State
                    : NvencRunPublicationRecoveryWorkerState.NotStarted;
            }
        }

        /// <summary>
        /// Opens the Run through the existing initialization entry and, for a
        /// publication recovery outcome only, composes and starts one recovery
        /// worker.
        /// </summary>
        /// <remarks>
        /// Returns false on ordinary lock contention, leaving both out
        /// parameters null and composing nothing. On success the outcome is
        /// always the exact one the entry produced. The ownership lease is
        /// handed back only while it remains the caller's - a session-ready or
        /// collision outcome; for a started recovery it is the worker's, so
        /// null is returned in its place.
        /// </remarks>
        internal bool TryOpen(
            CaptureRunRootLayout rootLayout,
            int maximumRootEntryCount,
            out CaptureRunInitializationOpenOutcome outcome,
            out CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            outcome = null;
            ownershipLease = null;

            // One active recovery at a time, so no Run is ever given a second
            // worker. Checked before the lock is touched.
            if (_worker != null)
            {
                throw new InvalidOperationException(
                    "A recovery worker is already active; its terminal must be collected before another Run is opened.");
            }

            if (!_entryCoordinator.TryOpen(
                    rootLayout,
                    maximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome entryOutcome,
                    out CaptureRunInitializationSessionOwnershipLease entryLease))
            {
                return false;
            }

            if (entryOutcome.Status
                != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired)
            {
                // The existing path keeps exactly what the entry produced; no
                // recovery type is involved.
                outcome = entryOutcome;
                ownershipLease = entryLease;
                return true;
            }

            StartRecovery(entryOutcome, entryLease);

            outcome = entryOutcome;
            return true;
        }

        /// <summary>
        /// Non-waiting read of the failure the active recovery worker retained
        /// while parked for a release retry or after a fault. It is that
        /// worker's own exception; nothing is classified or wrapped here.
        /// </summary>
        internal bool TryGetRecoveryFailure(out Exception failure)
        {
            NvencRunPublicationRecoveryWorkerService worker = _worker;
            if (worker == null)
            {
                failure = null;
                return false;
            }

            return worker.TryGetFailure(out failure);
        }

        /// <summary>
        /// Forwards one explicit release-retry request to the active recovery
        /// worker. The worker is the only authority on whether the request is
        /// admissible - it alone checks the parked state and the lease's
        /// partial release, and it alone performs the state transition - so
        /// nothing is latched, counted, or deadlined here.
        /// </summary>
        internal bool TryRequestRecoveryReleaseRetry()
        {
            NvencRunPublicationRecoveryWorkerService worker = _worker;
            return worker != null && worker.TryRequestReleaseRetry();
        }

        /// <summary>
        /// Collects the active recovery's terminal result at most once, and
        /// only once that worker has physically stopped with one. On success
        /// the worker is disposed and the slot is freed, so the next Run may be
        /// opened. Returns false in every other state - no recovery active,
        /// still running, parked for a release retry, faulted, or already
        /// collected - and never guesses a result.
        /// </summary>
        internal bool TryCollectRecoveryTerminal(
            out NvencRunPublicationRecoveryTerminalResult terminal)
        {
            NvencRunPublicationRecoveryWorkerService worker = _worker;
            if (worker == null)
            {
                terminal = default;
                return false;
            }

            // The worker's own collection is the single definition of "stopped
            // with a verified terminal, at most once".
            if (!worker.TryCollectTerminal(out terminal))
            {
                return false;
            }

            _worker = null;
            _recoveryOpenOutcome = null;
            _recoveryOwnershipLease = null;

            // The lease was released by the worker's release stage; disposing
            // the worker only finishes the worker itself.
            worker.Dispose();
            return true;
        }

        /// <summary>
        /// Composes one worker for that exact outcome and lease and starts it
        /// once.
        /// </summary>
        /// <remarks>
        /// A <c>Start</c> that throws leaves that worker instance unstarted by
        /// the worker's own contract, and only in that case - as in a refused
        /// composition - does this owner clean up the unstarted worker and the
        /// lease. Once <c>Start</c> returns normally, every later release of
        /// that lease is the recovery graph's.
        /// </remarks>
        private void StartRecovery(
            CaptureRunInitializationOpenOutcome entryOutcome,
            CaptureRunInitializationSessionOwnershipLease entryLease)
        {
            NvencRunPublicationRecoveryWorkerService worker = null;

            try
            {
                // The process-state admission is the factory's; a refusal
                // arrives here as its exception.
                worker = _workerFactory.Create(entryOutcome, entryLease);

                if (worker == null)
                {
                    throw new InvalidOperationException(
                        "The recovery worker factory returned no worker.");
                }

                // The slot is taken before the start, so this Run is never
                // offered a second worker while this one exists.
                _worker = worker;
                _recoveryOpenOutcome = entryOutcome;
                _recoveryOwnershipLease = entryLease;

                worker.Start();
            }
            catch (Exception ex)
            {
                _worker = null;
                _recoveryOpenOutcome = null;
                _recoveryOwnershipLease = null;

                ReleaseUnstarted(worker, entryLease, ex);
            }
        }

        /// <summary>
        /// Releases what never became the worker's: the unstarted worker and
        /// the lease the entry produced. The original exception is re-thrown
        /// with its original stack, and a cleanup failure is reported beside it
        /// rather than in its place.
        /// </summary>
        private static void ReleaseUnstarted(
            NvencRunPublicationRecoveryWorkerService worker,
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            Exception original)
        {
            Exception workerFailure = null;
            Exception leaseFailure = null;

            if (worker != null)
            {
                try
                {
                    worker.Dispose();
                }
                catch (Exception ex)
                {
                    workerFailure = ex;
                }
            }

            try
            {
                ownershipLease.Dispose();
            }
            catch (Exception ex)
            {
                leaseFailure = ex;
            }

            if (workerFailure == null && leaseFailure == null)
            {
                ExceptionDispatchInfo.Capture(original).Throw();
            }

            if (workerFailure != null && leaseFailure != null)
            {
                throw new AggregateException(original, workerFailure, leaseFailure);
            }

            throw new AggregateException(original, workerFailure ?? leaseFailure);
        }
    }
}
