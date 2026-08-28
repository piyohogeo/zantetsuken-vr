using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable model of one live captured frame draft, held before the run's
    /// final manifest exists. Run-scoped values are sourced from
    /// <see cref="Run"/>, frame-scoped values from <see cref="Request"/> and its
    /// trace context, and <see cref="TraceContext"/> is built exactly once from
    /// the request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type holds no <c>TraceRunManifest</c>, <c>CaptureRunReference</c>,
    /// or run manifest content hash, no draft status, drop reason, or drop
    /// trace emission state, and no PNG bytes, staging entry, file path,
    /// receipt, render texture lease, or readback result. It never casts itself
    /// to a final <c>CaptureFrameRecord</c> and never fabricates a provisional
    /// manifest or hash.
    /// </para>
    /// <para>
    /// Unavailable poses are stored exactly as supplied and never completed to
    /// the identity pose. This type owns and disposes nothing.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameDraft
    {
        internal CaptureFrameDraft(
            CaptureDraftRunContext run,
            in CaptureFrameRequest request,
            in CaptureFrameTiming timing,
            in CapturePoseSample headPose,
            in CapturePoseSample leftControllerPose,
            in CapturePoseSample rightControllerPose,
            int commitPathId)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }

            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            if (!timing.IsValid)
            {
                throw new ArgumentException("Timing must be valid.", nameof(timing));
            }

            if (commitPathId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commitPathId), commitPathId, "Commit path ID must be greater than zero.");
            }

            if (request.TraceContext.CaptureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.TraceContext.CaptureFrameId, "Capture frame ID must be greater than zero.");
            }

            if (request.TraceContext.UnityFrameId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.TraceContext.UnityFrameId, "Unity frame ID must not be negative.");
            }

            if (request.TraceContext.OpenXRFrameId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.TraceContext.OpenXRFrameId, "OpenXR frame ID must not be negative.");
            }

            if (request.TraceContext.TestRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.TraceContext.TestRunId, "Test run ID must be greater than zero.");
            }

            if (request.TraceContext.TestRunId != run.TestRunId)
            {
                throw new ArgumentException("Request test run ID must match the run test run ID.", nameof(request));
            }

            Run = run;
            Request = request;
            TraceContext = new CaptureFrameDraftTraceContext(request.TraceContext);
            Timing = timing;
            HeadPose = headPose;
            LeftControllerPose = leftControllerPose;
            RightControllerPose = rightControllerPose;
            CommitPathId = commitPathId;
        }

        public CaptureDraftRunContext Run { get; }

        public CaptureFrameRequest Request { get; }

        public CaptureFrameDraftTraceContext TraceContext { get; }

        public CaptureFrameTiming Timing { get; }

        public CapturePoseSample HeadPose { get; }

        public CapturePoseSample LeftControllerPose { get; }

        public CapturePoseSample RightControllerPose { get; }

        public int CommitPathId { get; }

        public long CaptureFrameId => Request.TraceContext.CaptureFrameId;

        public long UnityFrameId => Request.TraceContext.UnityFrameId;

        public long OpenXRFrameId => Request.TraceContext.OpenXRFrameId;

        public long TestRunId => Request.TraceContext.TestRunId;

        public long TestCaseId => Run.TestCaseId;

        public string BuildId => Run.BuildId;

        public string SceneId => Run.SceneId;

        public long RandomSeed => Run.RandomSeed;

        public long SlashId => Request.TraceContext.SlashId;

        public long FrontEdgeId => Request.TraceContext.FrontEdgeId;

        public long ObjectId => Request.TraceContext.ObjectId;

        public uint ObjectGeneration => Request.TraceContext.ObjectGeneration;

        public long TaskId => Request.TraceContext.TaskId;

        public CaptureSource Source => Request.Source;

        public CaptureEye Eye => Request.Eye;

        public CaptureImageRect ImageRect => Request.ImageRect;

        public int ArrayIndex => Request.ArrayIndex;

        public int CaptureProfileId => Run.CaptureProfileId;

        /// <summary>
        /// Delegates to <see cref="CaptureFrameRequest.IdenticalTo"/> so a
        /// registry can match a held request without re-implementing partial
        /// comparison.
        /// </summary>
        internal bool HasIdenticalRequest(in CaptureFrameRequest request)
        {
            return Request.IdenticalTo(request);
        }
    }
}
