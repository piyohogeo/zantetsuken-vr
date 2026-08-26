using Unity.Profiling;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Catalog of fixed-name <see cref="ProfilerMarker"/> instances used by the
    /// domain trace and observability code. Markers are created once and are
    /// immutable; names never embed task IDs, slash IDs, or counts.
    /// </summary>
    public static class ZantetsuProfilerMarkers
    {
        public const string SlashCandidateSearchName = "Zantetsu.Slash.CandidateSearch";
        public const string SlashFrontAdvanceName = "Zantetsu.Slash.FrontAdvance";
        public const string SlashFrontSweepName = "Zantetsu.Slash.FrontSweep";
        public const string SlashTopologyValidateName = "Zantetsu.Slash.TopologyValidate";
        public const string FuturePredictPoseName = "Zantetsu.Future.PredictPose";
        public const string PhysicsPredictName = "Zantetsu.Physics.Predict";
        public const string MeshClassifyName = "Zantetsu.Mesh.Classify";
        public const string MeshBuildCapName = "Zantetsu.Mesh.BuildCap";
        public const string ConvexSliceName = "Zantetsu.Convex.Slice";
        public const string CommitValidateName = "Zantetsu.Commit.Validate";
        public const string CommitApplyName = "Zantetsu.Commit.Apply";
        public const string TraceDrainName = "Zantetsu.Trace.Drain";
        public const string CaptureCopyName = "Zantetsu.Capture.Copy";
        public const string CaptureEncodeName = "Zantetsu.Capture.Encode";

        public static readonly ProfilerMarker SlashCandidateSearch = new ProfilerMarker(SlashCandidateSearchName);
        public static readonly ProfilerMarker SlashFrontAdvance = new ProfilerMarker(SlashFrontAdvanceName);
        public static readonly ProfilerMarker SlashFrontSweep = new ProfilerMarker(SlashFrontSweepName);
        public static readonly ProfilerMarker SlashTopologyValidate = new ProfilerMarker(SlashTopologyValidateName);
        public static readonly ProfilerMarker FuturePredictPose = new ProfilerMarker(FuturePredictPoseName);
        public static readonly ProfilerMarker PhysicsPredict = new ProfilerMarker(PhysicsPredictName);
        public static readonly ProfilerMarker MeshClassify = new ProfilerMarker(MeshClassifyName);
        public static readonly ProfilerMarker MeshBuildCap = new ProfilerMarker(MeshBuildCapName);
        public static readonly ProfilerMarker ConvexSlice = new ProfilerMarker(ConvexSliceName);
        public static readonly ProfilerMarker CommitValidate = new ProfilerMarker(CommitValidateName);
        public static readonly ProfilerMarker CommitApply = new ProfilerMarker(CommitApplyName);
        public static readonly ProfilerMarker TraceDrain = new ProfilerMarker(TraceDrainName);
        public static readonly ProfilerMarker CaptureCopy = new ProfilerMarker(CaptureCopyName);
        public static readonly ProfilerMarker CaptureEncode = new ProfilerMarker(CaptureEncodeName);
    }
}
