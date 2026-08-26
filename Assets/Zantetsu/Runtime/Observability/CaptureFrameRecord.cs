using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable record of one captured frame, combining a run reference, the
    /// capture request, frame timing, and the three head/controller poses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run information (test run, test case, build, scene, random seed, capture
    /// profile, manifest hash) is sourced from <see cref="Run"/>, which is the
    /// source of truth. The record never duplicates those strings per frame.
    /// </para>
    /// <para>
    /// <see cref="CommitPathId"/> is a positive, run-scoped dictionary ID; the
    /// actual commit path name is resolved through a caller-supplied
    /// run-scoped dictionary, so no string is stored per frame.
    /// </para>
    /// <para>
    /// Head, left-controller, and right-controller poses are stored exactly as
    /// supplied: an unavailable pose stays unavailable and is never completed
    /// to the identity pose. <c>OpenXRFrameId == 0</c> is allowed for
    /// Unity-only capture.
    /// </para>
    /// <para>
    /// The record owns no PNG bytes, no file path, and no serializer. All
    /// values are supplied by the caller from the same capture point.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRecord
    {
        public CaptureFrameRecord(
            CaptureRunReference run,
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
                throw new ArgumentException("Request test run ID must match the run reference test run ID.", nameof(request));
            }

            Run = run;
            Request = request;
            Timing = timing;
            HeadPose = headPose;
            LeftControllerPose = leftControllerPose;
            RightControllerPose = rightControllerPose;
            CommitPathId = commitPathId;
        }

        public CaptureRunReference Run { get; }

        public CaptureFrameRequest Request { get; }

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

        public string RunManifestContentSha256 => Run.RunManifestContentSha256;
    }
}
