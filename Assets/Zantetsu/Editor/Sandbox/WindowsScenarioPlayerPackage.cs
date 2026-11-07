using System;
using System.Diagnostics;
using System.IO;
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
        public static IDisposable GraphicsApis()
        {
            return new GraphicsScope();
        }

        private sealed class GraphicsScope : IDisposable
        {
            private readonly bool automatic;
            private readonly GraphicsDeviceType[] apis;
            private bool disposed;

            public GraphicsScope()
            {
                automatic = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
                apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
                try
                {
                    PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
                        new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 });
                }
                catch { Dispose(); throw; }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, apis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, automatic);
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
