using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Gates lease-aware capture frame submission behind
    /// <see cref="CaptureFrameCadenceSelector.TrySelect"/> and delegates only
    /// selected frames, together with the caller's rented render target lease,
    /// to <see cref="CaptureFrameRenderTargetRecordSubmissionCoordinator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cadence selector always runs first. When it returns <c>false</c> the
    /// result is <see cref="CaptureFrameCadencedSubmissionStatus.NotSelected"/>
    /// with <paramref name="acceptedRecord"/> left <c>null</c> and no other
    /// dependency touched; no capture frame ID is consumed and the lease is
    /// neither validated nor registered, so it stays owned by the caller.
    /// </para>
    /// <para>
    /// On entry the lease is owned by the caller. On
    /// <see cref="CaptureFrameCadencedSubmissionStatus.Submitted"/> its
    /// ownership has transferred to the lease registry and the caller must not
    /// return it. On
    /// <see cref="CaptureFrameCadencedSubmissionStatus.Backpressured"/> the
    /// lease has already been rolled back by the scheduler and returns to the
    /// caller, who is responsible for returning it to the pool. On a selector
    /// exception the submission coordinator is never touched and the lease
    /// stays with the caller. On a submission exception the lease returns to
    /// the caller only when the scheduler's rollback succeeded; a rollback
    /// invariant violation fails closed and the lease is never guessed or
    /// returned.
    /// </para>
    /// <para>
    /// Re-entering the same timestamp after a selection returns
    /// <see cref="CaptureFrameCadencedSubmissionStatus.NotSelected"/> and
    /// performs no additional ID issuance or drop trace. The selection state is
    /// <b>not</b> rolled back after a backpressure or exception: the selected
    /// timestamp and the factory's issued ID stay consumed. A caller that must
    /// restart selection explicitly calls
    /// <see cref="CaptureFrameCadenceSelector.Reset"/> on the injected selector.
    /// </para>
    /// <para>
    /// Defensively, if the submission coordinator violates its contract by
    /// returning <c>true</c> with a <c>null</c> record or <c>false</c> with a
    /// non-null record, this type fails closed with
    /// <see cref="InvalidOperationException"/>; the already-performed cadence
    /// selection, ID issuance, and registry changes are not rolled back.
    /// </para>
    /// <para>
    /// Owns, disposes, clears, and retains nothing beyond the injected selector
    /// and submission coordinator, and never calls
    /// <c>CaptureFrameRenderTargetPool.Return</c> or clears the queue or
    /// registries. Main-thread only and <b>not</b> thread-safe. Not a
    /// MonoBehaviour, singleton, or <see cref="IDisposable"/>, and performs no
    /// Unity static API access, time lookup, ID generation, logging, or file
    /// I/O.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRenderTargetCadencedSubmissionCoordinator
    {
        private readonly CaptureFrameCadenceSelector _cadenceSelector;
        private readonly CaptureFrameRenderTargetRecordSubmissionCoordinator _submissionCoordinator;

        public CaptureFrameRenderTargetCadencedSubmissionCoordinator(
            CaptureFrameCadenceSelector cadenceSelector,
            CaptureFrameRenderTargetRecordSubmissionCoordinator submissionCoordinator)
        {
            if (cadenceSelector == null)
            {
                throw new ArgumentNullException(nameof(cadenceSelector));
            }

            if (submissionCoordinator == null)
            {
                throw new ArgumentNullException(nameof(submissionCoordinator));
            }

            _cadenceSelector = cadenceSelector;
            _submissionCoordinator = submissionCoordinator;
        }

        public CaptureFrameCadencedSubmissionStatus TrySubmit(
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
            out CaptureFrameRecord acceptedRecord)
        {
            acceptedRecord = null;

            if (!_cadenceSelector.TrySelect(timing))
            {
                return CaptureFrameCadencedSubmissionStatus.NotSelected;
            }

            bool submitted = _submissionCoordinator.TrySubmit(
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
                lease,
                out CaptureFrameRecord record);

            if (submitted)
            {
                if (record == null)
                {
                    throw new InvalidOperationException("Submission coordinator returned true with a null record.");
                }

                acceptedRecord = record;
                return CaptureFrameCadencedSubmissionStatus.Submitted;
            }

            if (record != null)
            {
                throw new InvalidOperationException("Submission coordinator returned false with a non-null record.");
            }

            return CaptureFrameCadencedSubmissionStatus.Backpressured;
        }
    }
}
