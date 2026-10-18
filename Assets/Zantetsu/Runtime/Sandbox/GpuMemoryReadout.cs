using System.Globalization;
using UnityEngine;
using UnityEngine.Profiling;
using Zantetsu.Core;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// DIAGNOSIS ONLY (unit 8-B/8-C, 2026-10-17): the engine-side memory indicators a release player exposes --
    /// texture memory, total allocated and reserved, the Mono heap -- and the display's own GPU uploads by call site
    /// (<see cref="Zantetsu.Rendering.VpUploadMeter"/>), one set of lines every 10 s and one when the XR session's
    /// state is seen changed at Update (a change seen by this frame's Update, not every transition the runtime made).
    /// These indicators cover only part of what the engine holds on the GPU (`GetAllocatedMemoryForGraphicsDriver` is
    /// 0 in a release player), so a growth that does not show here is "not seen in these indicators", no more. Reads
    /// only; allocates nothing of its own but the lines. On only with the Player argument <see cref="Argument"/>
    /// (on Quest through the intent extra); a normal launch creates nothing and counts nothing.
    /// </summary>
    public sealed class GpuMemoryReadout : MonoBehaviour
    {
        public const string Prefix = "GPU MEMORY: ";
        public const string Argument = Zantetsu.Rendering.VpUploadMeter.Argument;   // one argument: the readout and the upload counting
        public const float IntervalSeconds = 10f;

        public static GpuMemoryReadout Instance { get; private set; }

        public static bool AskedFor(string[] arguments) => arguments != null && System.Array.IndexOf(arguments, Argument) >= 0;

        /// <summary>The native draw plugin's library, as the process's maps name it.</summary>
        public const string PluginLibrary = "libZantetsuVpVulkan.so";

        /// <summary>
        /// Whether the plugin's library is mapped in THIS process (unit 8-C, 2026-10-17): read from the process's own
        /// /proc/self/maps, which a process may read of itself (another process's is refused to the shell). A reading
        /// only -- nothing here loads a library, so no plugin call (TryLoad, DescribeState) is made. A failed read, or a
        /// text that is not a Unity process's maps, is "unknown", never 0.
        /// </summary>
        public static string DescribeMappedLibraries()
        {
            try
            {
                return DescribeMappedLibraries(System.IO.File.ReadAllLines("/proc/self/maps"));
            }
            catch (System.Exception e)
            {
                return "pluginMap=unknown (read failed: " + e.GetType().Name + ")";
            }
        }

        /// <summary>The same from a maps text: the lines naming the plugin, when the text has a Unity process's library and enough lines to be a whole maps.</summary>
        public static string DescribeMappedLibraries(string[] lines)
        {
            if (lines == null) return "pluginMap=unknown (no text)";
            int unity = 0, plugin = 0;
            foreach (string line in lines)
            {
                if (line == null) continue;
                if (line.Contains("libunity.so")) unity++;
                if (line.Contains(PluginLibrary)) plugin++;
            }
            if (lines.Length < 100 || unity == 0)
                return "pluginMap=unknown (lines=" + lines.Length + " unityLines=" + unity + ": not a whole maps of a Unity process)";
            return "pluginMap=" + plugin + " (lines=" + lines.Length + " unityLines=" + unity + " read=ok)";
        }

        public static GpuMemoryReadout InstallIfAsked()
        {
            if (!AskedFor(System.Environment.GetCommandLineArgs())) return null;
            if (Instance != null) return Instance;
            var host = new GameObject("Zantetsu GPU memory readout");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            Instance = host.AddComponent<GpuMemoryReadout>();
            return Instance;
        }

        private float _next;
        private int _sessionState = -1;
        private int _changes;

        /// <summary>
        /// DIAGNOSIS ONLY (unit 10-B's load check, 2026-10-18): with this argument as well as <see cref="Argument"/>,
        /// a file of <see cref="MeasurementResetFile"/> in the app's persistent data folder (put there by adb) makes the
        /// spatial split of every cut world forget what it measured of a cut publication, once per file (the file is
        /// deleted): so that, in the same process after a preparation cut, the next cut publication is unmeasured
        /// again. Looked for once a second. Nothing of it without the argument.
        /// </summary>
        public const string MeasurementResetArgument = "-zantetsuSplitMeasurementResetFile";
        public const string MeasurementResetFile = "split-measurement-reset";

        private bool _resetAsked;
        private float _nextResetLook;

        private void Start()
        {
            _resetAsked = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), MeasurementResetArgument) >= 0;
            if (_resetAsked) Debug.Log(Prefix + "measurement reset: on -- a file '" + MeasurementResetFile + "' in " + Application.persistentDataPath + " makes the split forget its cut publication's measurement (diagnosis only)");
        }

        private void LookForMeasurementReset()
        {
            if (!_resetAsked || Time.realtimeSinceStartup < _nextResetLook) return;
            _nextResetLook = Time.realtimeSinceStartup + 1f;
            string path = System.IO.Path.Combine(Application.persistentDataPath, MeasurementResetFile);
            try
            {
                if (!System.IO.File.Exists(path)) return;
                System.IO.File.Delete(path);
            }
            catch (System.Exception e)
            {
                Debug.Log(Prefix + "measurement reset: the file could not be read or deleted (" + e.GetType().Name + "); nothing was reset");
                return;
            }

            foreach (Zantetsu.PhysicsCut.CutWorldRoot world in Object.FindObjectsByType<Zantetsu.PhysicsCut.CutWorldRoot>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                Zantetsu.PhysicsCut.CutSpatialSplit split = world.SpatialSplit;
                Debug.Log(Prefix + "measurement reset f=" + Time.frameCount + " world=" + world.name + " "
                    + (split == null ? "no split" : split.ForgetCutPublicationMeasurementForDiagnosis() + "; attempt now " + split.AttemptSerial + ", retries so far " + split.PublishRetries));
            }
        }

        private void Update()
        {
            LookForMeasurementReset();
            int state = XrSessionStateReport.State;
            bool changed = state != _sessionState;
            if (changed) { _sessionState = state; _changes++; }
            if (!changed && Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + IntervalSeconds;
            Line(changed ? "session " + XrSessionStateReport.Name : "periodic");
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);

        private void Line(string why)
        {
            Debug.Log(Prefix + why + " f=" + Time.frameCount + " t=" + Time.realtimeSinceStartup.ToString("F1", CultureInfo.InvariantCulture)
                + " sessionChanges=" + _changes + " stateChanges=" + XrSessionStateReport.Changes + " restarts=" + XrSessionStateReport.Restarts + " (READY " + XrSessionStateReport.ReadyCount + " times, counted at the source)"
                + " | graphicsDriverMB=" + Mb(Profiler.GetAllocatedMemoryForGraphicsDriver()) + "(0 in a Release player)"
                + " textureCurrentMB=" + Mb((long)Texture.currentTextureMemory) + " textureTotalMB=" + Mb((long)Texture.totalTextureMemory)
                + " textureNonStreamingMB=" + Mb((long)Texture.nonStreamingTextureMemory) + " textureNonStreamingCount=" + Texture.nonStreamingTextureCount
                + " totalAllocatedMB=" + Mb(Profiler.GetTotalAllocatedMemoryLong()) + " totalReservedMB=" + Mb(Profiler.GetTotalReservedMemoryLong())
                + " monoHeapMB=" + Mb(Profiler.GetMonoHeapSizeLong()) + " tempAllocMB=" + Mb(Profiler.GetTempAllocatorSize())
                + " | eye=" + UnityEngine.XR.XRSettings.eyeTextureWidth + "x" + UnityEngine.XR.XRSettings.eyeTextureHeight + " msaa=" + QualitySettings.antiAliasing);
            // The engine's skinned renderers (unit 8-C): how many, how many vertices they skin a frame, how many are
            // enabled and visible -- Unity's GPU skinning writes a frame's vertices for each, outside the display's own uploads.
            SkinnedMeshRenderer[] skinned = Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            long vertices = 0, visibleVertices = 0; int enabledCount = 0, visibleCount = 0;
            foreach (SkinnedMeshRenderer r in skinned)
            {
                int v = r.sharedMesh != null ? r.sharedMesh.vertexCount : 0;
                vertices += v;
                if (r.enabled && r.gameObject.activeInHierarchy) { enabledCount++; if (r.isVisible) { visibleCount++; visibleVertices += v; } }
            }
            Debug.Log(Prefix + "skinned " + why + " f=" + Time.frameCount + " renderers=" + skinned.Length + " enabled=" + enabledCount + " visible=" + visibleCount
                + " vertices=" + vertices + " visibleVertices=" + visibleVertices + " skinWeights=" + QualitySettings.skinWeights);
            // The stop stage and its tallies (unit 8-C): what was issued and what was skipped since launch.
            Debug.Log(Prefix + "stop " + why + " f=" + Time.frameCount + " " + Zantetsu.Rendering.VpGpuStop.Describe());
            // The plugin's library in this process, from the process's own maps (a reading; loads nothing).
            Debug.Log(Prefix + "libraries " + why + " f=" + Time.frameCount + " " + DescribeMappedLibraries());
            // The spatial split's tallies of every cut world (TL observation 2026-10-17: the split did not work in one play
            // and did in the next; unresolved): the split's own event lines are written at the world's end, and the
            // world's end-of-run statistics only under ZANTETSU_OBSERVE, so a Release Player on the device shows neither.
            // A reading only; it changes nothing of the split.
            foreach (Zantetsu.PhysicsCut.CutWorldRoot world in Object.FindObjectsByType<Zantetsu.PhysicsCut.CutWorldRoot>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                Zantetsu.PhysicsCut.CutSpatialSplit split = world.SpatialSplit;
                // Three lines (2026-10-18: one line was cut at the device log's limit, about 1,024 characters, and the
                // convex cut's part of it was never seen).
                Debug.Log(Prefix + "split " + why + " f=" + Time.frameCount + " world=" + world.name + " " + (split == null ? "none (the split is off or not made)" : split.Describe(false)));
                if (split != null)
                {
                    Debug.Log(Prefix + "split-bent " + why + " f=" + Time.frameCount + " world=" + world.name + " " + split.DescribeBent());
                    Debug.Log(Prefix + "split-cut " + why + " f=" + Time.frameCount + " world=" + world.name + " " + split.DescribeConvexCut());
                    // The runs of held budget comparisons, by attempt and stage (unit 10-B): the one going on (or the last), the one before, the longest.
                    Debug.Log(Prefix + "hold " + why + " f=" + Time.frameCount + " world=" + world.name + " attempt now " + split.AttemptSerial
                        + " | now: " + split.CurrentHoldStreak.Describe());
                    Debug.Log(Prefix + "hold-before " + why + " f=" + Time.frameCount + " world=" + world.name + " " + split.PreviousHoldStreak.Describe());
                    Debug.Log(Prefix + "hold-longest " + why + " f=" + Time.frameCount + " world=" + world.name + " " + split.LongestHoldStreak.Describe());
                    // The retry of a publication past the budget (unit 10-B, D-245): how often, put off how often, what is measured now, the last one.
                    Debug.Log(Prefix + "split-retry " + why + " f=" + Time.frameCount + " world=" + world.name
                        + " after " + split.RetryAfterHolds + " comparisons and " + split.RetryAfterHeldSeconds.ToString("R") + " s (the product's "
                        + Zantetsu.PhysicsCut.CutSpatialSplit.DefaultRetryAfterHolds + " and " + Zantetsu.PhysicsCut.CutSpatialSplit.DefaultRetryAfterHeldSeconds.ToString("R") + ")"
                        + "; retries " + split.PublishRetries + ", put off " + split.PublishRetriesPutOff
                        + "; measured (seeds until " + Zantetsu.PhysicsCut.CutSpatialSplit.PublicationSamplesBeforeUse + "): publication " + split.PublishSamples
                        + " + collection after " + split.CollectAfterSamples + ", cut publication " + split.CutPublishSamples + " + collection after " + split.CutCollectAfterSamples
                        + ", collections after a publication not read " + split.CollectionsAfterPublicationMissed
                        + "; expected publication " + (split.ExpectedPublishSeconds * 1000.0).ToString("F3") + " ms, cut publication "
                        + (split.ExpectedCutPublishSeconds * 1000.0).ToString("F3") + " ms");
                    Debug.Log(Prefix + "split-retry-last " + why + " f=" + Time.frameCount + " world=" + world.name + " " + split.LastRetry.Describe());
                    // The split's last budget comparison with the very values it used (unit 10-B, 2026-10-18: on the Quest,
                    // after a sleep, every turn was held BudgetShort; which of the terms moved is what this tells), then the
                    // physics step's own last decision as auxiliary information. Readings only; nothing is changed by it.
                    Debug.Log(Prefix + "budget " + why + " f=" + Time.frameCount + " world=" + world.name + " splitLastHold=" + split.LastHold
                        + " | " + split.LastBudgetDecision.Describe()
                        + " | step (auxiliary): budgetS=" + Zantetsu.PhysicsCut.CutPhysicsStep.MainBudgetSeconds.ToString("G4")
                        + " lastRemainingS=" + Zantetsu.PhysicsCut.CutPhysicsStep.LastDecisionRemainingSeconds.ToString("G4")
                        + " lastExpectedS=" + Zantetsu.PhysicsCut.CutPhysicsStep.LastDecisionExpectedSeconds.ToString("G4")
                        + " lastSimulateS=" + Zantetsu.PhysicsCut.CutPhysicsStep.LastSimulateSeconds.ToString("G4")
                        + " lastStepped=" + Zantetsu.PhysicsCut.CutPhysicsStep.LastDecisionStepped
                        + " skippedForCostInARow=" + Zantetsu.PhysicsCut.CutPhysicsStep.SkippedForCostInARow
                        + " verificationSteps=" + Zantetsu.PhysicsCut.CutPhysicsStep.VerificationSteps
                        + " lastSimulatedFrame=" + Zantetsu.PhysicsCut.CutPhysicsStep.LastSimulatedFrame
                        + " lastDecidedFrame=" + Zantetsu.PhysicsCut.CutPhysicsStep.LastDecidedFrame);
                }
            }
            // The display's own GPU uploads since the last line, by call site (unit 8-C): every site that uploaded, one line each.
            foreach (string line in Zantetsu.Rendering.VpUploadMeter.Snapshot())
            {
                Debug.Log(Prefix + "uploads " + why + " f=" + Time.frameCount + " " + line);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
