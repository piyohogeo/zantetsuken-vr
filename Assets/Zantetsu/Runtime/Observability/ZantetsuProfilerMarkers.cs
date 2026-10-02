using Unity.Profiling;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-name marker for the active GPU readback path.
    /// </summary>
    public static class ZantetsuProfilerMarkers
    {
        public const string CaptureCopyName = "Zantetsu.Capture.Copy";
        public static readonly ProfilerMarker CaptureCopy = new ProfilerMarker(CaptureCopyName);
    }
}
