using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Builds correlated <see cref="CaptureFrameRecord"/> instances so callers
    /// never assemble <c>CaptureFrameId</c> or <c>TestRunId</c> by hand. The
    /// fixed capture settings are pinned at construction; every
    /// <see cref="Create"/> call issues a fresh ID and fills the rest of the
    /// correlation context from its arguments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CaptureFrameId</c> is obtained only from
    /// <see cref="CaptureFrameIdSequence.Next"/> and <c>TestRunId</c> only from
    /// <see cref="CaptureRunReference.TestRunId"/>; the caller cannot supply
    /// either. The same issued ID is used for the
    /// <see cref="CaptureFrameTraceContext.CaptureFrameId"/>, the
    /// <see cref="CaptureFrameRequest"/>, and the <see cref="CaptureFrameRecord"/>.
    /// </para>
    /// <para>
    /// An ID is consumed as soon as <see cref="CaptureFrameIdSequence.Next"/>
    /// succeeds. If the subsequent request or record construction fails, that
    /// ID is not reused; the next successful <see cref="Create"/> continues
    /// from the following value.
    /// </para>
    /// <para>
    /// Poses are stored exactly as supplied: an unavailable pose stays
    /// unavailable and is never completed to the identity pose.
    /// </para>
    /// <para>
    /// The fixed capture settings (source, eye, image rectangle, array index,
    /// and pixel format) are validated once at construction by delegating to
    /// the <see cref="CaptureFrameRequest"/> constructor, and per-call record
    /// validation is delegated to the <see cref="CaptureFrameRecord"/>
    /// constructor. No validation rule is re-implemented here.
    /// </para>
    /// <para>
    /// This factory owns and disposes nothing: the run reference, the ID
    /// sequence, and every produced record are caller-owned. It is intended
    /// for the main thread only and is not thread-safe. It performs no queue
    /// registration, trace enqueue, file I/O, Unity static API access, time or
    /// frame-counter lookup, logging, singleton state, or MonoBehaviour work.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRecordFactory
    {
        private readonly CaptureRunReference _run;
        private readonly CaptureFrameIdSequence _captureFrameIds;
        private readonly CaptureSource _source;
        private readonly CaptureEye _eye;
        private readonly CaptureImageRect _imageRect;
        private readonly int _arrayIndex;
        private readonly CapturePixelFormat _pixelFormat;

        public CaptureFrameRecordFactory(
            CaptureRunReference run,
            CaptureFrameIdSequence captureFrameIds,
            CaptureSource source,
            CaptureEye eye,
            in CaptureImageRect imageRect,
            int arrayIndex,
            CapturePixelFormat pixelFormat)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }

            if (captureFrameIds == null)
            {
                throw new ArgumentNullException(nameof(captureFrameIds));
            }

            // Validate the fixed capture settings once at construction by
            // delegating to the CaptureFrameRequest constructor, the single
            // source of truth. The request constructor does not inspect the
            // correlation context, so a default context is sufficient.
            _ = new CaptureFrameRequest(default, source, eye, imageRect, arrayIndex, pixelFormat);

            _run = run;
            _captureFrameIds = captureFrameIds;
            _source = source;
            _eye = eye;
            _imageRect = imageRect;
            _arrayIndex = arrayIndex;
            _pixelFormat = pixelFormat;
        }

        public CaptureFrameRecord Create(
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
            int commitPathId)
        {
            // CaptureFrameId comes only from the sequence. Once issued it is
            // consumed even if the record construction below fails.
            long captureFrameId = _captureFrameIds.Next();

            // TestRunId comes only from the run reference, never from the
            // caller.
            long testRunId = _run.TestRunId;

            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                timestamp,
                unityFrameId,
                fixedStepId,
                threadId,
                captureFrameId,
                openXRFrameId,
                testRunId,
                slashId,
                frontEdgeId,
                objectId,
                objectGeneration,
                taskId);

            CaptureFrameRequest request = new CaptureFrameRequest(
                context,
                _source,
                _eye,
                _imageRect,
                _arrayIndex,
                _pixelFormat);

            return new CaptureFrameRecord(
                _run,
                request,
                timing,
                headPose,
                leftControllerPose,
                rightControllerPose,
                commitPathId);
        }
    }
}
