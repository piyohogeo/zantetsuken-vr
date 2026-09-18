using System;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Which code a capture was taken with. A capture that cannot be tied to a
    /// revision cannot be recomputed with confidence later, so this is saved
    /// with the run -- including whether the working tree had changes that no
    /// revision describes.
    /// <para>
    /// Only the Editor can answer this, because only there is the repository
    /// present; a player says so rather than guessing. Nothing here fails a
    /// save: an unknown revision is recorded as unknown.
    /// </para>
    /// </summary>
    internal static class SandboxSlashCaptureCommit
    {
        internal static void Describe(out string commit, out string dirty)
        {
#if UNITY_EDITOR
            if (!TryRunGit("rev-parse HEAD", out string head))
            {
                commit = "unknown (git did not answer)";
                dirty = "unknown (git did not answer)";
                return;
            }

            commit = head.Trim();
            if (commit.Length == 0)
            {
                commit = "unknown (git gave no revision)";
            }

            if (!TryRunGit("status --porcelain", out string status))
            {
                dirty = "unknown (git did not answer)";
                return;
            }

            string trimmed = status.Trim();
            if (trimmed.Length == 0)
            {
                dirty = "no";
                return;
            }

            int lines = trimmed.Split('\n').Length;
            dirty = "yes, " + lines + " path" + (lines == 1 ? string.Empty : "s")
                + " reported by git status --porcelain (tracked changes and untracked files together)";
#else
            commit = "unknown (not running in the Editor)";
            dirty = "unknown (not running in the Editor)";
#endif
        }

#if UNITY_EDITOR
        private static bool TryRunGit(string arguments, out string output)
        {
            output = string.Empty;
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = System.IO.Directory.GetCurrentDirectory(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill();
                        return false;
                    }

                    output = stdout.GetAwaiter().GetResult();
                    stderr.GetAwaiter().GetResult();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                // A machine without git on the path is a machine whose captures
                // say the revision is unknown, not one that cannot save.
                return false;
            }
        }
#endif
    }
}
