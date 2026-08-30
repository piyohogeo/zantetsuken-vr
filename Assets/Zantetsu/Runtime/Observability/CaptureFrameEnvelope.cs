using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Codec-independent meaning of one capture frame. It deliberately contains
    /// no encoded bytes, file path, content hash, or encoder setting.
    /// </summary>
    internal sealed class CaptureFrameEnvelope
    {
        private readonly CaptureFrameRequest _request;
        private readonly CaptureFrameTiming _timing;
        private readonly CapturePoseSample _headPose;
        private readonly CapturePoseSample _leftControllerPose;
        private readonly CapturePoseSample _rightControllerPose;
        private readonly int _commitPathId;
        private readonly int _captureProfileId;
        private readonly CaptureColorSpace _colorSpace;
        private readonly long _testCaseId;
        private readonly string _buildId;
        private readonly string _sceneId;
        private readonly long _randomSeed;

        internal CaptureFrameEnvelope(
            in CaptureFrameRequest request,
            in CaptureFrameTiming timing,
            in CapturePoseSample headPose,
            in CapturePoseSample leftControllerPose,
            in CapturePoseSample rightControllerPose,
            int commitPathId,
            int captureProfileId,
            CaptureColorSpace colorSpace,
            long testCaseId,
            string buildId,
            string sceneId,
            long randomSeed)
        {
            if (!request.IsValid || request.TraceContext.TestRunId <= 0 || request.TraceContext.CaptureFrameId <= 0)
            {
                throw new ArgumentException("Request must identify a valid capture frame.", nameof(request));
            }

            if (!timing.IsValid)
            {
                throw new ArgumentException("Timing must be valid.", nameof(timing));
            }

            if (commitPathId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commitPathId));
            }

            if (captureProfileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureProfileId));
            }

            if (colorSpace != CaptureColorSpace.Srgb && colorSpace != CaptureColorSpace.Linear)
            {
                throw new ArgumentOutOfRangeException(nameof(colorSpace));
            }

            if (testCaseId <= 0) throw new ArgumentOutOfRangeException(nameof(testCaseId));
            if (string.IsNullOrEmpty(buildId)) throw new ArgumentException("Build ID must not be empty.", nameof(buildId));
            if (string.IsNullOrEmpty(sceneId)) throw new ArgumentException("Scene ID must not be empty.", nameof(sceneId));

            _request = request;
            _timing = timing;
            _headPose = headPose;
            _leftControllerPose = leftControllerPose;
            _rightControllerPose = rightControllerPose;
            _commitPathId = commitPathId;
            _captureProfileId = captureProfileId;
            _colorSpace = colorSpace;
            _testCaseId = testCaseId;
            _buildId = buildId;
            _sceneId = sceneId;
            _randomSeed = randomSeed;
        }

        internal static CaptureFrameEnvelope FromDraft(CaptureFrameDraft draft, CaptureColorSpace colorSpace)
        {
            if (draft == null)
            {
                throw new ArgumentNullException(nameof(draft));
            }

            return new CaptureFrameEnvelope(
                draft.Request,
                draft.Timing,
                draft.HeadPose,
                draft.LeftControllerPose,
                draft.RightControllerPose,
                draft.CommitPathId,
                draft.CaptureProfileId,
                colorSpace,
                draft.TestCaseId,
                draft.BuildId,
                draft.SceneId,
                draft.RandomSeed);
        }

        internal CaptureFrameRequest Request => _request;
        internal CaptureFrameTraceContext TraceContext => _request.TraceContext;
        internal CaptureFrameTiming Timing => _timing;
        internal CapturePoseSample HeadPose => _headPose;
        internal CapturePoseSample LeftControllerPose => _leftControllerPose;
        internal CapturePoseSample RightControllerPose => _rightControllerPose;
        internal long TestRunId => _request.TraceContext.TestRunId;
        internal long CaptureFrameId => _request.TraceContext.CaptureFrameId;
        internal long UnityFrameId => _request.TraceContext.UnityFrameId;
        internal long OpenXRFrameId => _request.TraceContext.OpenXRFrameId;
        internal long SlashId => _request.TraceContext.SlashId;
        internal long ObjectId => _request.TraceContext.ObjectId;
        internal uint ObjectGeneration => _request.TraceContext.ObjectGeneration;
        internal long TaskId => _request.TraceContext.TaskId;
        internal CaptureSource CaptureSource => _request.Source;
        internal CaptureEye Eye => _request.Eye;
        internal CaptureImageRect ImageRect => _request.ImageRect;
        internal CaptureFramePixelLayout PixelLayout => _request.PixelLayout;
        internal int CommitPathId => _commitPathId;
        internal int CaptureProfileId => _captureProfileId;
        internal CaptureColorSpace ColorSpace => _colorSpace;
        internal long TestCaseId => _testCaseId;
        internal string BuildId => _buildId;
        internal string SceneId => _sceneId;
        internal long RandomSeed => _randomSeed;
    }
}
