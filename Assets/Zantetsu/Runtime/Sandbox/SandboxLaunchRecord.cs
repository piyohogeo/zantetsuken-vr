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
                + "; buildGuid=" + Application.buildGUID + ", version=" + Application.version + ", unity=" + Application.unityVersion
                + "; command line: " + string.Join(" ", arguments);
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
