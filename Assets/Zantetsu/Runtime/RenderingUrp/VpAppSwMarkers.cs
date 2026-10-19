using Unity.Profiling;

namespace Zantetsu.Rendering.Urp
{
    /// <summary>
    /// The CPU sections of the AppSW motion pass's extra distance culling (2026-10-19, for the city walk's A/B timing):
    /// four markers, separate and nested only where said, so that no one of them is taken for the whole culling cost.
    /// <list type="bullet">
    /// <item><see cref="UnityCullName"/>: <c>ScriptableRenderContext.Cull</c> alone, the main thread's time in the call
    /// (its culling jobs run on workers; their time is not this).</item>
    /// <item><see cref="RecordName"/>: the experiment's whole recording of the AppSW pass -- the plane and the parameters,
    /// the Cull above (nested inside it), the stock pass's update and the recording of its renderer list. Only the
    /// experiment has it: without the experiment URP records the same pass inside its own code, unmeasured here.</item>
    /// <item><see cref="NativeCullRecordName"/>: the plugin route's extra GPU culling of the motion pass, the CPU time of
    /// recording its commands (one call a batch culled). Not the GPU's time of the culling.</item>
    /// <item><see cref="MotionClipKeepName"/>: the plugin route's CPU selection of the motion pass's clip records -- by the
    /// motion's distance conditions with the experiment, by the camera's ordinary judgement without it (the same section
    /// in both, so that the difference is the distance's).</item>
    /// </list>
    /// </summary>
    public static class VpAppSwMarkers
    {
        public const string UnityCullName = "Zantetsu.AppSwDistance.UnityCull";
        public const string RecordName = "Zantetsu.AppSwDistance.Record";
        public const string NativeCullRecordName = "Zantetsu.AppSwDistance.NativeCullRecord";
        public const string MotionClipKeepName = "Zantetsu.AppSwDistance.MotionClipKeep";

        /// <summary>The four, in the order above (the walk's timeline records them in this order).</summary>
        public static readonly string[] Names = { UnityCullName, RecordName, NativeCullRecordName, MotionClipKeepName };

        internal static readonly ProfilerMarker UnityCull = new ProfilerMarker(UnityCullName);
        internal static readonly ProfilerMarker Record = new ProfilerMarker(RecordName);
        internal static readonly ProfilerMarker NativeCullRecord = new ProfilerMarker(NativeCullRecordName);
        internal static readonly ProfilerMarker MotionClipKeep = new ProfilerMarker(MotionClipKeepName);

        /// <summary>Makes the four markers exist (a recorder started before its marker is made may not find it).</summary>
        public static int Ensure() => UnityCull.Handle != System.IntPtr.Zero && Record.Handle != System.IntPtr.Zero
            && NativeCullRecord.Handle != System.IntPtr.Zero && MotionClipKeep.Handle != System.IntPtr.Zero ? Names.Length : 0;
    }
}
