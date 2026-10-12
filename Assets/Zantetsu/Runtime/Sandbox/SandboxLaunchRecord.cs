using System;
using UnityEngine;
using UnityEngine.Profiling;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Zantetsu.MeshCut;
#endif

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// One line in the Player's log at every start, check or no check (TL 2026-10-07, D-211): what a measurement is to
    /// be read with. Whether the Player is a Development build; whether the numeric input-contract diagnosis runs
    /// (on, off by its argument, or not compiled); the Profiler's settings; the build's id, which the build's own
    /// record ties to the source commit and to what was uncommitted on it; and the command line, which carries the
    /// additional observation asked for. It reads settings: it changes nothing and measures nothing.
    /// </summary>
    public static class SandboxLaunchRecord
    {
        public const string Prefix = "ZANTETSU LAUNCH: ";

        /// <summary>The Profiler's arguments a comparison must hold the same: a run with either is not compared with one without.</summary>
        public const string DeepProfilingArgument = "-deepprofiling";
        public const string ProfilerEnableArgument = "-profiler-enable";

        public static string Describe() => Describe(Environment.GetCommandLineArgs());

        public static string Describe(string[] arguments)
        {
            bool Has(string name) => Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) >= 0;
#if DEVELOPMENT_BUILD
            const string build = "Development";
#elif UNITY_EDITOR
            const string build = "Editor";
#else
            const string build = "Release (not a Development build)";
#endif
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            string numeric = VpNumericDiagnosis.Describe();
#else
            string numeric = "not compiled (a non-Development Player)"
                + (Has("-zantetsuDisableNumericDiagnostics") ? "; -zantetsuDisableNumericDiagnostics was given and does nothing here" : "");
#endif
            return "build=" + build + " (isDebugBuild=" + Debug.isDebugBuild + ")"
                + "; numeric diagnosis=" + numeric
                + "; profiler: supported=" + Profiler.supported + ", enabled=" + Profiler.enabled
                + ", binary log=" + (Profiler.enableBinaryLog ? "on (" + Profiler.logFile + ")" : "off")
                + ", allocation callstacks=" + Profiler.enableAllocationCallstacks
                + ", " + DeepProfilingArgument + "=" + (Has(DeepProfilingArgument) ? "given" : "not given")
                + ", " + ProfilerEnableArgument + "=" + (Has(ProfilerEnableArgument) ? "given" : "not given")
                + "; graphics=" + SystemInfo.graphicsDeviceType + "; body route asked: " + DescribeBodyRoute(arguments)
                + "; clipped instances asked: " + (Zantetsu.Rendering.VpGpuCullSetup.ReadSeparateClip(arguments) ? "apart (the default)" : "with the selection (" + Zantetsu.Rendering.VpGpuCullSetup.ClipTogetherArgument + ")")
                + "; command compaction asked: " + DescribeCommandCompaction(arguments)
                + "; buildGuid=" + Application.buildGUID + ", version=" + Application.version + ", unity=" + Application.unityVersion
                + "; scenario: " + SandboxScenario.Describe(arguments)
                + "; Prop Slash parts: " + SandboxPropSlashPlayerCheck.DescribeParts(arguments)
                + "; command line: " + string.Join(" ", arguments);
        }

        /// <summary>
        /// What the launch asks of the body's route and what that selects on this device (D-215): the request, and the
        /// route with its reason. This is the launch's side; the route a world actually took, with whether the plugin
        /// could be had, is that world's own "VP BODY ROUTE:" line.
        /// </summary>
        public static string DescribeBodyRoute(string[] arguments)
        {
            Zantetsu.Rendering.VpBodyRouteRequest request = Zantetsu.Rendering.VpNativeDrawSetup.ReadRequest(arguments);
            Zantetsu.Rendering.VpNativeDrawSetup.Selection selection =
                Zantetsu.Rendering.VpNativeDrawSetup.Select(request, SystemInfo.graphicsDeviceType, Application.isEditor, Application.platform);
            return request + " -> " + (selection.Native ? "VP3C-NATIVE" : "Unity's calls") + " (" + selection.Reason + ")";
        }

        /// <summary>
        /// What the launch asks of the command compaction on this device (DESIGN 4.5.10, 4.5.11): the Vulkan route
        /// compacts unless the fixed draw is asked for; the Direct3D 12 route draws fixed unless the compaction is
        /// asked for (D-219).
        /// This is the launch's side; whether a route took it, and why not when it did not, is that route's own
        /// "VP COMMAND COMPACTION:" line.
        /// </summary>
        public static string DescribeCommandCompaction(string[] arguments)
        {
            bool vulkan = Zantetsu.Rendering.VpNativeDrawPlugin.Vulkan;
            bool asked = vulkan
                ? Zantetsu.Rendering.VpNativeDrawPlugin.ReadIndirectCountRequest(arguments)
                : Zantetsu.Rendering.VpNativeDrawPlugin.ReadDirect3D12IndirectCountRequest(arguments);
            bool fixedGiven = Array.IndexOf(arguments, Zantetsu.Rendering.VpNativeDrawPlugin.FixedCommandsArgument) >= 0;
            bool compactGiven = Array.IndexOf(arguments, Zantetsu.Rendering.VpNativeDrawPlugin.CompactCommandsArgument) >= 0;
            return (asked ? "yes" : "no") + " ("
                + (fixedGiven ? Zantetsu.Rendering.VpNativeDrawPlugin.FixedCommandsArgument
                    : compactGiven ? Zantetsu.Rendering.VpNativeDrawPlugin.CompactCommandsArgument + (vulkan ? ", the default anyway" : "")
                    : vulkan ? "the Vulkan route's default" : "the Direct3D 12 route's default: the fixed draw")
                + (Array.IndexOf(arguments, Zantetsu.Rendering.VpCommandCompaction.VerifyArgument) >= 0 ? "; " + Zantetsu.Rendering.VpCommandCompaction.VerifyArgument + " given: a check run" : "")
                + ")";
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Write()
        {
            if (!Application.isEditor)
            {
                Debug.Log(Prefix + Describe());
            }
        }
    }
}
