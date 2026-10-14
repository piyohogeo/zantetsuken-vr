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
            bool ok = TryRun(BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone, out string report)
                      & TryRun(BuildTarget.Android, BuildTargetGroup.Android, out string android);
            Debug.Log("RELEASE COMPILE CHECK: " + report + "; " + android);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(ok ? 0 : 1);
            }
        }

        public static bool TryRun(BuildTarget target, BuildTargetGroup group, out string report)
        {
            string output = Path.Combine(Path.GetTempPath(), "zantetsu-release-compile-" + target + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            try
            {
                var settings = new ScriptCompilationSettings
                {
                    group = group,
                    target = target,
                    options = ScriptCompilationOptions.None,   // a Release Player: no DEVELOPMENT_BUILD
                };
                ScriptCompilationResult result = PlayerBuildInterface.CompilePlayerScripts(settings, output);
                int assemblies = result.assemblies != null ? result.assemblies.Count() : 0;
                bool ok = assemblies > 0 && result.assemblies.Any(a => a.StartsWith("Zantetsu.", StringComparison.Ordinal));
                report = target + ": " + (ok ? "compiled" : "NOT compiled") + " (" + assemblies + " assemblies, " + (result.typeDB != null ? "type DB" : "no type DB") + ")";
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
    }
}
