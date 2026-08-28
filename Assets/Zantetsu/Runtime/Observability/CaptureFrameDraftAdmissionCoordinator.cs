using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The sole admission path for live capture frame drafts. It reserves
    /// entry and pending capacity before any capture frame ID is issued, so a
    /// capacity rejection never produces a draft with a positive ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixed order is: reserve capacity, record a zero-ID admission
    /// rejection trace only when capacity is exhausted, issue the ID from the
    /// factory only after a successful reservation, commit the draft, and
    /// cancel only the reservation on any failure. On success the reservation
    /// ownership transfers to the registry and is never cancelled by this
    /// coordinator.
    /// </para>
    /// <para>
    /// This coordinator does not roll back or reset IDs, remove or clear draft
    /// entries, release pending slots, transition Stage/Drop, register a
    /// request queue or render texture lease, or dispose the logger, factory,
    /// or registry. It generates no success or dropped trace events and
    /// performs no Unity static API access, time or frame lookup, file I/O,
    /// hash computation, LINQ, or logging.
    /// </para>
    /// <para>
    /// It is for the main thread only, is not thread-safe, and does not
    /// implement <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameDraftAdmissionCoordinator
    {
        private readonly CaptureFrameDraftFactory _draftFactory;
        private readonly CaptureFrameDraftRegistry _draftRegistry;
        private readonly CaptureFrameTraceObserver _traceObserver;

        internal CaptureFrameDraftAdmissionCoordinator(
            CaptureFrameDraftFactory draftFactory,
            CaptureFrameDraftRegistry draftRegistry,
            CaptureFrameTraceObserver traceObserver)
        {
            if (draftFactory == null)
            {
                throw new ArgumentNullException(nameof(draftFactory));
            }

            if (draftRegistry == null)
            {
                throw new ArgumentNullException(nameof(draftRegistry));
            }

            if (traceObserver == null)
            {
                throw new ArgumentNullException(nameof(traceObserver));
            }

            if (!ReferenceEquals(draftFactory.Run, draftRegistry.Run))
            {
                throw new ArgumentException("The draft factory and registry must share the same run.", nameof(draftRegistry));
            }

            _draftFactory = draftFactory;
            _draftRegistry = draftRegistry;
            _traceObserver = traceObserver;
        }

        /// <summary>The draft registry shared with the admission path.</summary>
        internal CaptureFrameDraftRegistry Registry => _draftRegistry;

        internal bool TryAdmit(
            long timestamp,
            long unityFrameId,
            long fixedStepId,
            int threadId,
            long openXRFrameId,
            long slashId,
            long frontEdgeId,
            long objectId,
            uint objectGeneration,
            long taskId,
            in CaptureFrameTiming timing,
            in CapturePoseSample headPose,
            in CapturePoseSample leftControllerPose,
            in CapturePoseSample rightControllerPose,
            int commitPathId,
            out CaptureFrameDraft acceptedDraft)
        {
            acceptedDraft = null;

            CaptureFrameDraftReservation reservation;
            CaptureFrameAdmissionRejectKind rejectKind;
            if (!_draftRegistry.TryReserve(out reservation, out rejectKind))
            {
                // Build the zero-ID admission rejection context from the call
                // arguments; TestRunId comes only from the registry run.
                CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                    timestamp,
                    unityFrameId,
                    fixedStepId,
                    threadId,
                    0,
                    openXRFrameId,
                    _draftRegistry.Run.TestRunId,
                    slashId,
                    frontEdgeId,
                    objectId,
                    objectGeneration,
                    taskId);

                _traceObserver.RecordAdmissionRejected(context, rejectKind);
                return false;
            }

            CaptureFrameDraft draft = null;
            try
            {
                draft = _draftFactory.Create(
                    timestamp,
                    unityFrameId,
                    fixedStepId,
                    threadId,
                    openXRFrameId,
                    slashId,
                    frontEdgeId,
                    objectId,
                    objectGeneration,
                    taskId,
                    timing,
                    headPose,
                    leftControllerPose,
                    rightControllerPose,
                    commitPathId);

                _draftRegistry.Commit(reservation, draft);
            }
            catch (Exception ex)
            {
                Exception cleanupFailure = null;
                try
                {
                    _draftRegistry.Cancel(reservation);
                }
                catch (Exception cancelEx)
                {
                    cleanupFailure = cancelEx;
                }

                if (cleanupFailure == null)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }

                throw new AggregateException(ex, cleanupFailure);
            }

            acceptedDraft = draft;
            return true;
        }
    }
}
