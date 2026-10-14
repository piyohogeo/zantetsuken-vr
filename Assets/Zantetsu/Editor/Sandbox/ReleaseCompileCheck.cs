using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEngine;

namespace Zantetsu.Editor.Sandbox
{
    /// <summary>
    /// Compiles the Player's scripts as a RELEASE Player would -- no DEVELOPMENT_BUILD, no UNITY_EDITOR -- without building
    /// one (DESIGN 21.18): the code the Release excludes by <c>#if</c> is what a Player build would compile, and a mistake
    /// in it shows here, not at the build. Menu "Zantetsu > Play statistics > Compile the scripts as a Release Player", or
    /// <c>-executeMethod Zantetsu.Editor.Sandbox.ReleaseCompileCheck.Run</c> (exit code 0 where every assembly compiled,
    /// 1 where any did not). The output goes to a temporary folder and is deleted.
    /// </summary>
    public static class ReleaseCompileCheck
    {
        [MenuItem("Zantetsu/Play statistics/Compile the scripts as a Release Player")]
        public static void Run()
        {
            // A product Release build: it compiles, and the compiled assemblies hold nothing of the statistics'
            // verification build (its types are under a symbol no product build defines).
            bool ok = TryRun(BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone, out string report, null, out bool windowsVerify)
                      & TryRun(BuildTarget.Android, BuildTargetGroup.Android, out string android, null, out bool androidVerify);
            ok &= !windowsVerify && !androidVerify;
            Debug.Log("RELEASE COMPILE CHECK: " + report + "; " + android);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
        }

        /// <summary>
        /// The same check with the symbol of the statistics' VERIFICATION build (DESIGN 21.18), which the Editor never
        /// compiles otherwise: <c>-executeMethod Zantetsu.Editor.Sandbox.ReleaseCompileCheck.RunStatsVerify</c>. The
        /// project's own defines are not changed.
        /// </summary>
        [MenuItem("Zantetsu/Play statistics/Compile the scripts as the statistics' verification build")]
        public static void RunStatsVerify()
        {
            // With the symbol the types must be there: that also shows the symbol reached the compilation.
            bool ok = TryRun(BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone, out string report, new[] { "ZANTETSU_STATS_VERIFY" }, out bool verify);
            ok &= verify;
            Debug.Log("RELEASE COMPILE CHECK (ZANTETSU_STATS_VERIFY): " + report);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
        }

        public static bool TryRun(BuildTarget target, BuildTargetGroup group, out string report) => TryRun(target, group, out report, null, out _);

        public static bool TryRun(BuildTarget target, BuildTargetGroup group, out string report, string[] extraDefines) => TryRun(target, group, out report, extraDefines, out _);

        /// <summary>The type names of the statistics' verification build, as the compiled assemblies' metadata holds them.</summary>
        private static readonly string[] VerificationTypeNames = { "PlayStatisticsVerify", "PlayVerifyTally" };

        public static bool TryRun(BuildTarget target, BuildTargetGroup group, out string report, string[] extraDefines, out bool verificationTypes)
        {
            verificationTypes = false;
            string output = Path.Combine(Path.GetTempPath(), "zantetsu-release-compile-" + target + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            try
            {
                var settings = new ScriptCompilationSettings
                {
                    group = group,
                    target = target,
                    options = ScriptCompilationOptions.None,   // a Release Player: no DEVELOPMENT_BUILD
                    extraScriptingDefines = extraDefines ?? Array.Empty<string>(),
                };
                ScriptCompilationResult result = PlayerBuildInterface.CompilePlayerScripts(settings, output);
                int assemblies = result.assemblies != null ? result.assemblies.Count() : 0;
                bool ok = assemblies > 0 && result.assemblies.Any(a => a.StartsWith("Zantetsu.", StringComparison.Ordinal));
                report = target + ": " + (ok ? "compiled" : "NOT compiled") + " (" + assemblies + " assemblies, " + (result.typeDB != null ? "type DB" : "no type DB") + ")";
                if (ok)
                {
                    report += "; " + FindVerificationTypes(output, out verificationTypes);
                }

                return ok;
            }
            catch (Exception exception)
            {
                report = target + ": compile failed: " + exception.Message;
                return false;
            }
            finally
            {
                try { Directory.Delete(output, true); } catch (Exception) { }
            }
        }

        // The compiled Zantetsu assemblies, searched for the verification types' names as metadata strings (the name
        // and its terminating zero, so that a longer name is not taken for it).
        private static string FindVerificationTypes(string output, out bool present)
        {
            present = false;
            int searched = 0;
            var found = new System.Collections.Generic.List<string>();
            foreach (string assembly in Directory.GetFiles(output, "Zantetsu.*.dll", SearchOption.AllDirectories))
            {
                searched++;
                byte[] bytes = File.ReadAllBytes(assembly);
                foreach (string name in VerificationTypeNames)
                {
                    byte[] pattern = System.Text.Encoding.UTF8.GetBytes(name + "\0");
                    if (IndexOf(bytes, pattern) >= 0)
                    {
                        present = true;
                        found.Add(Path.GetFileName(assembly) + ":" + name);
                    }
                }
            }

            return present
                ? "verification types PRESENT in " + string.Join(", ", found) + " (of " + searched + " Zantetsu assemblies)"
                : "verification types absent from " + searched + " Zantetsu assemblies";
        }

        private static int IndexOf(byte[] bytes, byte[] pattern)
        {
            for (int i = 0; i <= bytes.Length - pattern.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && bytes[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }

            return -1;
        }
    }
}
