using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// Builds the cut world sandbox scene as a Windows x64 IL2CPP Development Player (DESIGN 3): one entry, naming
    /// the one scene, so that the same Player can be made again from a command line.
    /// <para>
    /// **It reports the settings; it does not decide them.** The scripting backend, the architecture and the
    /// graphics API are the project's own. This reads them, writes them into the build log, and **refuses to build**
    /// if they are not what this unit is about -- it never quietly changes a project setting to make a build pass.
    /// </para>
    /// <para>
    /// **Nothing it produces belongs in the repository.** The Player, its log and anything it captures go to a
    /// directory given on the command line, outside the project.
    /// </para>
    /// </summary>
    public static class CutWorldSandboxPlayerBuild
    {
        /// <summary>The argument that says where to put the Player: <c>-zantetsuPlayerOut &lt;directory&gt;</c>.</summary>
        private const string OutArgument = "-zantetsuPlayerOut";

        private const string DefaultOutDirectory = @"C:\log\zantetsuken-vr\Phase5Player\player";

        private const string ExecutableName = "CutWorldSandbox.exe";

        [MenuItem("Zantetsu/Build the cut world sandbox player (Windows x64, IL2CPP, Development)")]
        public static void BuildFromMenu()
        {
            Build(DefaultOutDirectory);
        }

        /// <summary>The entry for <c>-executeMethod</c>. Takes the output directory from the command line.</summary>
        public static void BuildFromCommandLine()
        {
            string directory = DefaultOutDirectory;
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], OutArgument, StringComparison.OrdinalIgnoreCase))
                {
                    directory = arguments[i + 1];
                }
            }

            bool built = Build(directory);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(built ? 0 : 1);
            }
        }

        /// <summary>
        /// Builds the Player into <paramref name="directory"/>, after saying what it is building with. Returns
        /// whether the build succeeded; everything it found is in the log either way.
        /// </summary>
        public static bool Build(string directory, string scene = null, string[] diagnosticDefines = null)
        {
            scene ??= CutWorldSandboxSceneBuilder.ScenePathInProject;
            if (!File.Exists(scene))
            {
                Debug.LogError("PLAYER BUILD: the scene " + scene + " is not there. Build it first.");
                return false;
            }

            var target = BuildTarget.StandaloneWindows64;
            var targetGroup = BuildTargetGroup.Standalone;
            var named = UnityEditor.Build.NamedBuildTarget.Standalone;

            ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(named);
            var settings = new StringBuilder("PLAYER BUILD SETTINGS:");
            settings.Append(" scene=").Append(scene);
            settings.Append(" target=").Append(target);
            settings.Append(" backend=").Append(backend);
            settings.Append(" il2cppConfiguration=").Append(PlayerSettings.GetIl2CppCompilerConfiguration(named));
            settings.Append(" il2cppCodeGeneration=").Append(PlayerSettings.GetIl2CppCodeGeneration(named));
            settings.Append(" stripping=").Append(PlayerSettings.GetManagedStrippingLevel(named));
            settings.Append(" apiCompatibility=").Append(PlayerSettings.GetApiCompatibilityLevel(named));
            settings.Append(" graphicsApis=");
            foreach (UnityEngine.Rendering.GraphicsDeviceType api in PlayerSettings.GetGraphicsAPIs(target))
            {
                settings.Append(api).Append(' ');
            }

            settings.Append("autoGraphicsApi=").Append(PlayerSettings.GetUseDefaultGraphicsAPIs(target));
            Debug.Log(settings.ToString());

            // The project's own setting, read and checked -- not set here. A build with the wrong backend would not
            // be the thing this unit is about, so it stops instead of changing the project.
            if (backend != ScriptingImplementation.IL2CPP)
            {
                Debug.LogError(
                    "PLAYER BUILD: the Standalone scripting backend is " + backend
                    + ", not IL2CPP. This entry does not change project settings; change it deliberately and run again.");
                return false;
            }

            Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, ExecutableName);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = executable,
                target = target,
                targetGroup = targetGroup,
                extraScriptingDefines = diagnosticDefines ?? Array.Empty<string>(),

                // Development, so that the Player writes its own log and its stack traces are readable. Nothing
                // here asks for a profiler connection or script debugging.
                options = BuildOptions.Development,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log(
                "PLAYER BUILD RESULT: result=" + summary.result + " output=" + summary.outputPath
                + " size=" + summary.totalSize + " bytes errors=" + summary.totalErrors
                + " warnings=" + summary.totalWarnings + " time=" + summary.totalTime);

            foreach (BuildStep step in report.steps)
            {
                foreach (BuildStepMessage message in step.messages)
                {
                    if (message.type == LogType.Error || message.type == LogType.Exception)
                    {
                        Debug.Log("PLAYER BUILD ERROR: [" + step.name + "] " + message.content);
                    }
                }
            }

            return summary.result == BuildResult.Succeeded;
        }
    }
}
