using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the draft admission coordinator and the draft scheduler into a
    /// single submission path. A draft is admitted first; once admitted it is
    /// permanently committed to the append-only registry, so it is published
    /// through <c>out</c> before scheduling is attempted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before admission succeeds the <c>out</c> draft is null. Once admission
    /// succeeds the <c>out</c> draft is the admitted draft even if scheduling
    /// returns <c>false</c> or throws. When the caller receives a non-null
    /// draft it must not run a new admission; instead it passes the same draft
    /// to <see cref="CaptureFrameRenderTargetDraftScheduler.TrySchedule"/> or to
    /// a future cancellation path. Re-running admission would create a draft
    /// with a different ID.
    /// </para>
    /// <para>
    /// On scheduling backpressure the returned draft is retried by passing the
    /// same draft and lease directly to the draft scheduler, never by calling
    /// this coordinator's <see cref="TrySubmit"/> again.
    /// </para>
    /// <para>
    /// This coordinator does not re-implement registry reserve, commit, or
    /// cancel, does not operate the request queue or lease registry, does not
    /// call <c>CaptureFrameRenderTargetPool.Return</c>, does not change draft
    /// status or release pending slots, does not roll back IDs, and does not
    /// generate trace events or call the factory directly. It holds no draft or
    /// lease internally, disposes and clears nothing, and performs no Unity
    /// static API access, time lookup, file I/O, hashing, logging, or LINQ.
    /// </para>
    /// <para>
    /// Main-thread only, not thread-safe, and not
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameRenderTargetDraftSubmissionCoordinator
    {
        private readonly CaptureFrameDraftAdmissionCoordinator _admissionCoordinator;
        private readonly CaptureFrameRenderTargetDraftScheduler _draftScheduler;

        internal CaptureFrameRenderTargetDraftSubmissionCoordinator(
            CaptureFrameDraftAdmissionCoordinator admissionCoordinator,
            CaptureFrameRenderTargetDraftScheduler draftScheduler)
        {
            if (admissionCoordinator == null)
            {
                throw new ArgumentNullException(nameof(admissionCoordinator));
            }

            if (draftScheduler == null)
            {
                throw new ArgumentNullException(nameof(draftScheduler));
            }

            if (!ReferenceEquals(admissionCoordinator.Registry, draftScheduler.Registry))
            {
                throw new ArgumentException("The admission coordinator and draft scheduler must share the same registry.", nameof(draftScheduler));
            }

            _admissionCoordinator = admissionCoordinator;
            _draftScheduler = draftScheduler;
        }

        internal CaptureFrameDraftSubmissionStatus TrySubmit(
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
            in CaptureFrameRenderTargetLease lease,
            out CaptureFrameDraft draft)
        {
            draft = null;

            bool admitted = _admissionCoordinator.TryAdmit(
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
                commitPathId,
                out CaptureFrameDraft acceptedDraft);

            if (!admitted)
            {
                if (acceptedDraft != null)
                {
                    throw new InvalidOperationException("Admission failed but returned a non-null draft.");
                }

                return CaptureFrameDraftSubmissionStatus.AdmissionRejected;
            }

            if (acceptedDraft == null)
            {
                throw new InvalidOperationException("Admission succeeded but returned a null draft.");
            }

            // Publish the admitted draft before scheduling: it is already
            // committed to the append-only registry, so the caller must be able
            // to identify it even if scheduling backpressures or fails.
            draft = acceptedDraft;

            // Fail-closed verification against the registry as the source of
            // truth; no partial request comparison is re-implemented here.
            if (!ReferenceEquals(acceptedDraft.Run, _admissionCoordinator.Registry.Run))
            {
                throw new InvalidOperationException("The admitted draft run does not match the registry run.");
            }

            if (!_admissionCoordinator.Registry.TryGet(acceptedDraft.Request, out CaptureFrameDraft registeredDraft, out CaptureFrameDraftStatus status))
            {
                throw new InvalidOperationException("The admitted draft is not registered in the registry.");
            }

            if (!ReferenceEquals(registeredDraft, acceptedDraft))
            {
                throw new InvalidOperationException("The registered draft is not the admitted draft instance.");
            }

            if (status != CaptureFrameDraftStatus.Pending)
            {
                throw new InvalidOperationException("The admitted draft is not pending.");
            }

            bool scheduled = _draftScheduler.TrySchedule(acceptedDraft, lease);
            return scheduled
                ? CaptureFrameDraftSubmissionStatus.Scheduled
                : CaptureFrameDraftSubmissionStatus.SchedulingBackpressured;
        }
    }
}
