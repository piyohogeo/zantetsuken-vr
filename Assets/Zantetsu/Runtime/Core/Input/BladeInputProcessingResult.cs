namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Result of a blade input processing step. Reference-free value type.
    /// Presence flags are derived from <see cref="Status"/>; there is no
    /// independently settable presence state.
    /// </summary>
    public readonly struct BladeInputProcessingResult
    {
        public readonly BladeInputProcessingStatus Status;
        public readonly BladeTrackingTransition TrackingTransition;
        public readonly EvaluatedBladePose EvaluatedPose;
        public readonly BladeMotionSample Motion;
        public readonly BladeEdgeGateDecision GateDecision;

        public bool HasEvaluatedPose =>
            Status == BladeInputProcessingStatus.WindowAccumulating
            || Status == BladeInputProcessingStatus.GateAccepted
            || Status == BladeInputProcessingStatus.GateRejected;

        public bool HasMotion =>
            Status == BladeInputProcessingStatus.GateAccepted
            || Status == BladeInputProcessingStatus.GateRejected;

        public bool HasGateDecision =>
            Status == BladeInputProcessingStatus.GateAccepted
            || Status == BladeInputProcessingStatus.GateRejected;

        public bool IsGateAccepted => Status == BladeInputProcessingStatus.GateAccepted;

        internal BladeInputProcessingResult(
            BladeInputProcessingStatus status,
            BladeTrackingTransition trackingTransition,
            EvaluatedBladePose evaluatedPose,
            BladeMotionSample motion,
            BladeEdgeGateDecision gateDecision)
        {
            Status = status;
            TrackingTransition = trackingTransition;
            EvaluatedPose = evaluatedPose;
            Motion = motion;
            GateDecision = gateDecision;
        }
    }
}
