using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// The scenario distribution includes both Windows renderers and the pre-Unity settings entry.
    /// Older diagnostic build entries keep their project settings, including the D3D11 capture profile.
    /// </summary>
    public static class WindowsScenarioPlayerPackage
    {
        public static IDisposable BuildSettings(string version = null)
        {
            if (version != null && !Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z"))
                throw new ArgumentException("The Windows Player version must have three numeric parts and an optional prerelease or build suffix.", nameof(version));
            return new BuildSettingsScope(version);
        }

        private sealed class BuildSettingsScope : IDisposable
        {
            private readonly bool automatic;
            private readonly GraphicsDeviceType[] apis;
            private readonly bool showSplashScreen;
            private readonly string bundleVersion;
            private readonly string additionalIl2CppArgs;
            private readonly bool versionOverridden;
            private bool disposed;

            public BuildSettingsScope(string version)
            {
                automatic = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
                apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
                showSplashScreen = PlayerSettings.SplashScreen.show;
                bundleVersion = PlayerSettings.bundleVersion;
                additionalIl2CppArgs = PlayerSettings.GetAdditionalIl2CppArgs();
                versionOverridden = version != null;
                try
                {
                    PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
                        new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
                    PlayerSettings.SplashScreen.show = false;
                    // IL2CPP accumulates repeated linker-flags options. Keep the caller's flags and hide the build machine's PDB path.
                    PlayerSettings.SetAdditionalIl2CppArgs(additionalIl2CppArgs + " --linker-flags=/PDBALTPATH:GameAssembly.pdb");
                    if (versionOverridden)
                    {
                        PlayerSettings.bundleVersion = version;
                        UnityEngine.Debug.Log("WINDOWS PLAYER BUILD: version " + bundleVersion + " -> " + version + " for this build.");
                    }
                    UnityEngine.Debug.Log("WINDOWS PLAYER BUILD: startup splash disabled.");
                }
                catch { Dispose(); throw; }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try
                {
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, apis);
                    PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, automatic);
                }
                finally
                {
                    PlayerSettings.SetAdditionalIl2CppArgs(additionalIl2CppArgs);
                    if (versionOverridden) PlayerSettings.bundleVersion = bundleVersion;
                    PlayerSettings.SplashScreen.show = showSplashScreen;
                }
            }
        }

        /// <summary>Package the non-XR launcher without changing the Player's executable name or the existing tools.</summary>
        public static void BuildLauncher(string directory)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                throw new NotSupportedException("The Windows scenario package must be assembled on Windows.");
            string script = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../Tools/WindowsLauncher/Build-Launcher.ps1"));
            if (!File.Exists(script)) throw new FileNotFoundException("The launcher build script is missing.", script);
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell/v1.0/powershell.exe");
            var start = new ProcessStartInfo(shell,
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script
                + "\" -OutputDirectory \"" + Path.Combine(Path.GetFullPath(directory), ".") + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new IOException("Could not start the launcher build.");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000))
                {
                    process.Kill();
                    throw new TimeoutException("The launcher build did not finish within 120 seconds.");
                }
                string stdout = output.GetAwaiter().GetResult(), stderr = error.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                    throw new IOException("The launcher build failed (" + process.ExitCode + "). " + stdout + "\n" + stderr);
                UnityEngine.Debug.Log("SCENARIO LAUNCHER: " + stdout + stderr);
            }
        }
    }
}
