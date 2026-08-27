using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Gates capture frame submission behind
    /// <see cref="CaptureFrameCadenceSelector.TrySelect"/> and delegates only
    /// selected frames to
    /// <see cref="CaptureFrameRecordSubmissionCoordinator"/>, so frames outside
    /// the 30/45 fps cadence perform no record generation, no ID issuance, no
    /// queue registration, and no trace recording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cadence selector always runs first. When it returns <c>false</c> the
    /// result is <see cref="CaptureFrameCadencedSubmissionStatus.NotSelected"/>
    /// with <paramref name="acceptedRecord"/> left <c>null</c> and no other
    /// dependency touched; no capture frame ID is consumed.
    /// </para>
    /// <para>
    /// When the frame is selected it is forwarded with the exact same arguments
    /// to the submission coordinator. A successful submission surfaces the same
    /// record instance the registry retains as
    /// <see cref="CaptureFrameCadencedSubmissionStatus.Submitted"/>; normal
    /// backpressure becomes
    /// <see cref="CaptureFrameCadencedSubmissionStatus.Backpressured"/> with a
    /// <c>null</c> record. The selection timestamp has already been recorded by
    /// the selector and the factory's issued ID has already been consumed in
    /// the backpressure case; drop trace and counters remain the scheduler's
    /// contract.
    /// </para>
    /// <para>
    /// Re-entering the same timestamp after a selection returns
    /// <see cref="CaptureFrameCadencedSubmissionStatus.NotSelected"/> and
    /// performs no additional ID issuance or drop trace.
    /// </para>
    /// <para>
    /// If the cadence selector throws, the submission coordinator is never
    /// touched, no ID is consumed, <paramref name="acceptedRecord"/> stays
    /// <c>null</c>, and the exception propagates unchanged. If the submission
    /// coordinator throws, its exception propagates unchanged, the selected
    /// timestamp is not rolled back, the issued ID is not reused, and registry
    /// rollback remains the scheduler's own responsibility.
    /// </para>
    /// <para>
    /// This operation is <b>not</b> a transaction: a submission failure after a
    /// successful cadence selection does not roll the cadence state back. The
    /// coordinator adds no <c>Reset</c>; a caller that must restart selection
    /// explicitly calls <see cref="CaptureFrameCadenceSelector.Reset"/> on the
    /// injected selector.
    /// </para>
    /// <para>
    /// Defensively, if the submission coordinator violates its contract by
    /// returning <c>true</c> with a <c>null</c> record or <c>false</c> with a
    /// non-null record, this type fails closed with
    /// <see cref="InvalidOperationException"/>. Even then, the already-performed
    /// cadence selection and submission side effects are not rolled back.
    /// </para>
    /// <para>
    /// Owns, disposes, clears, and retains nothing: it holds no reference to a
    /// record, queue, registry, or logger beyond the injected selector and
    /// submission coordinator, and generates no capture frame IDs. Main-thread
    /// only and <b>not</b> thread-safe. Not a MonoBehaviour, singleton, or
    /// <see cref="IDisposable"/>, and performs no Unity static API access, file
    /// I/O, logging, or additional trace.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameCadencedSubmissionCoordinator
    {
        private readonly CaptureFrameCadenceSelector _cadenceSelector;
        private readonly CaptureFrameRecordSubmissionCoordinator _submissionCoordinator;

        public CaptureFrameCadencedSubmissionCoordinator(
            CaptureFrameCadenceSelector cadenceSelector,
            CaptureFrameRecordSubmissionCoordinator submissionCoordinator)
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
            out CaptureFrameRecord acceptedRecord)
        {
            acceptedRecord = null;

            // 1. Cadence selection always runs first. If the frame is not
            // selected, no other dependency is touched and no ID is consumed.
            if (!_cadenceSelector.TrySelect(timing))
            {
                return CaptureFrameCadencedSubmissionStatus.NotSelected;
            }

            // 2. Delegate the selected frame with the exact same arguments.
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
