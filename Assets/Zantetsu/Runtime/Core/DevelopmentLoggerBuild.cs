#if UNITY_EDITOR && DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Zantetsu.Core
{
    // Editor-only even though colocated with the runtime assembly. No generated Assets or
    // project settings are needed: the build context carries this file into the Player.
    internal sealed class DevelopmentLoggerBuild : BuildPlayerProcessor
    {
        public override int callbackOrder => 0;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            if ((context.BuildPlayerOptions.options & BuildOptions.Development) == 0) return;
            try
            {
                string commit = ReadCommit();
                if (!DevelopmentLoggerLifecycle.IsCommit(commit)) return;
                string directory = Path.Combine("Library", "DevelopmentLogger");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, DevelopmentLoggerLifecycle.CommitFileName);
                File.WriteAllText(path, commit, new UTF8Encoding(false));
                context.AddAdditionalPathToStreamingAssets(path, DevelopmentLoggerLifecycle.CommitFileName);
            }
            catch (Exception) { /* Missing diagnostics must not fail a Player build. */ }
        }

        internal static string ReadCommit()
        {
            try
            {
                var start = new ProcessStartInfo("git", "rev-parse --verify HEAD")
                {
                    WorkingDirectory = Path.GetDirectoryName(Application.dataPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null) return null;
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000)) { process.Kill(); return null; }
                    stderr.GetAwaiter().GetResult();
                    return process.ExitCode == 0 ? stdout.GetAwaiter().GetResult().Trim() : null;
                }
            }
            catch (Exception) { return null; }
        }
    }
}
#endif
