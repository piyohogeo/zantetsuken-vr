using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Zantetsu.Core.Input;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// The capture's real save path, checked on a short known input before a
    /// device is used.
    /// <para>
    /// The permanent tests deliberately write to a temporary root: a test suite
    /// must not leave runs under the operator's own capture directory. So the
    /// one thing they cannot check is the thing the operator depends on -- that
    /// <see cref="SandboxSlashCapture.RootDirectory"/> itself can be written,
    /// read back, and not written over. This does that, once, on demand, and
    /// says what happened.
    /// </para>
    /// <para>
    /// It records no device input. The sweep below is a straight vertical
    /// synthetic one, fast enough to latch, with an untracked sample in the
    /// middle so that refused input is in the file too.
    /// </para>
    /// </summary>
    public static class SlashCapturePathCheck
    {
        private const double SampleInterval = 0.011;
        private static readonly Vector3 Step = new Vector3(0f, -0.12f, 0f);

        [MenuItem("Zantetsu/Checks/Slash capture: check the save path (no device)")]
        public static void Check()
        {
            CheckCore();
        }

        private static bool CheckCore()
        {
            var report = new List<string>
            {
                "Slash capture save path, checked on a short known input. No device, no play mode.",
                "root: " + SandboxSlashCapture.RootDirectory,
                string.Empty,
            };

            bool allWell = true;

            // 1 and 2: it saves to the real root, reads back, and the current
            // method recomputed from the file matches what was saved beside it.
            allWell &= RunOnce(report, "pathcheck", out string first);

            // 4: a second run of the same name does not write into the first.
            allWell &= RunOnce(report, "pathcheck", out string second);
            if (first.Length > 0 && second.Length > 0)
            {
                bool distinct = !string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
                report.Add("  two runs of the same name got different directories: " + distinct);
                report.Add("    " + first);
                report.Add("    " + second);
                allWell &= distinct;
            }

            // 3: a root that cannot be written is reported as a failure.
            allWell &= FailureIsVisible(report);

            report.Add(string.Empty);
            report.Add("every check as expected: " + allWell);
            string text = string.Join(Environment.NewLine, report);
            if (allWell)
            {
                Debug.Log("SlashCapturePathCheck:" + Environment.NewLine + text);
            }
            else
            {
                Debug.LogError("SlashCapturePathCheck:" + Environment.NewLine + text);
            }
            return allWell;
        }

        /// <summary>The batch entry point, so the check can run without the menu.</summary>
        public static void CheckAndExit()
        {
            try
            {
                EditorApplication.Exit(CheckCore() ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogError("SlashCapturePathCheck: " + exception);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Recomputes the newest saved run from its own files and reports
        /// whether the result matches what was recorded beside it. This is the
        /// check a device recording has to pass before it is used for anything:
        /// a file that cannot be recomputed cannot be compared against later.
        /// </summary>
        [MenuItem("Zantetsu/Checks/Slash capture: recompute the newest saved run")]
        public static void VerifyNewestRun()
        {
            VerifyNewestRunCore();
        }

        private static bool VerifyNewestRunCore()
        {
            var report = new List<string>
            {
                "Recompute of a saved slash run, from its own input.csv and conditions.txt.",
                "checked at: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };

            bool matched = false;
            string chosen = string.Empty;
            try
            {
                if (!TryFindNewestRun(out chosen, out string why))
                {
                    report.Add("NO RUN TO CHECK: " + why);
                }
                else
                {
                    report.Add("run: " + chosen);
                    foreach (string name in new[] { "input.csv", "conditions.txt", "current.csv" })
                    {
                        string path = Path.Combine(chosen, name);
                        report.Add("  " + name + ": " + (File.Exists(path)
                            ? new FileInfo(path).Length + " bytes, " + File.ReadAllLines(path).Length + " lines"
                            : "MISSING"));
                    }

                    foreach (string line in File.ReadAllLines(Path.Combine(chosen, "conditions.txt")))
                    {
                        if (line.StartsWith("updates captured:", StringComparison.Ordinal)
                            || line.StartsWith("wave observations:", StringComparison.Ordinal)
                            || line.StartsWith("capture ended by:", StringComparison.Ordinal)
                            || line.StartsWith("ending detail:", StringComparison.Ordinal)
                            || line.StartsWith("note:", StringComparison.Ordinal)
                            || line.StartsWith("commit:", StringComparison.Ordinal))
                        {
                            report.Add("  " + line);
                        }
                    }

                    matched = Recomputes(chosen, report);
                    report.Add("the current method recomputed from the saved files matches current.csv: " + matched);
                }
            }
            catch (Exception exception)
            {
                report.Add("the check itself threw: " + exception.GetType().Name + ": " + exception.Message);
            }

            string text = string.Join(Environment.NewLine, report);
            try
            {
                string directory = Path.Combine(SandboxSlashCapture.RootDirectory, "Checks");
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(
                        directory,
                        "recompute-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-"
                        + (chosen.Length > 0 ? new DirectoryInfo(chosen).Name : "none")
                        + "-" + Guid.NewGuid().ToString("N") + ".txt"),
                    text + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                text += Environment.NewLine + "(the report file could not be written: " + exception.Message + ")";
                matched = false;
            }

            if (matched)
            {
                Debug.Log("SlashCaptureRecompute:" + Environment.NewLine + text);
            }
            else
            {
                Debug.LogError("SlashCaptureRecompute:" + Environment.NewLine + text);
            }
            return matched;
        }

        /// <summary>The batch entry point for the recompute check.</summary>
        public static void VerifyNewestRunAndExit()
        {
            try
            {
                EditorApplication.Exit(VerifyNewestRunCore() ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogError("SlashCaptureRecompute: " + exception);
                EditorApplication.Exit(1);
            }
        }

        // The newest directory that actually holds a run. Checks/ and
        // Recovered/ are not runs and are skipped by the input.csv test rather
        // than by name.
        private static bool TryFindNewestRun(out string newest, out string why)
        {
            newest = string.Empty;
            why = string.Empty;
            if (!Directory.Exists(SandboxSlashCapture.RootDirectory))
            {
                why = SandboxSlashCapture.RootDirectory + " does not exist";
                return false;
            }

            DateTime best = DateTime.MinValue;
            foreach (string candidate in Directory.GetDirectories(SandboxSlashCapture.RootDirectory))
            {
                string input = Path.Combine(candidate, "input.csv");
                if (!File.Exists(input))
                {
                    continue;
                }

                DateTime written = File.GetLastWriteTimeUtc(input);
                if (written > best)
                {
                    best = written;
                    newest = candidate;
                }
            }

            if (newest.Length == 0)
            {
                why = "no directory under " + SandboxSlashCapture.RootDirectory + " holds an input.csv";
                return false;
            }

            return true;
        }

        private static bool RunOnce(List<string> report, string runId, out string directory)
        {
            directory = string.Empty;
            GameObject rig = null;
            GameObject blade = null;
            try
            {
                SandboxSlashCapture capture = Sweep(runId, out int updates, out rig, out blade);
                if (!capture.TrySave(out directory, out string failure))
                {
                    report.Add("SAVE FAILED for run id " + runId + ": " + failure);
                    return false;
                }

                report.Add("saved " + updates + " updates to " + directory);

                foreach (string name in new[] { "input.csv", "conditions.txt", "current.csv" })
                {
                    string path = Path.Combine(directory, name);
                    report.Add(
                        "  " + name + ": " + (File.Exists(path)
                            ? new FileInfo(path).Length + " bytes, " + File.ReadAllLines(path).Length + " lines"
                            : "MISSING"));
                    if (!File.Exists(path))
                    {
                        return false;
                    }
                }

                report.Add("  comparison/ present: " + Directory.Exists(Path.Combine(directory, "comparison")));

                bool matched = Recomputes(directory, report);
                report.Add("  the current method recomputed from input.csv matches current.csv: " + matched);
                return matched;
            }
            finally
            {
                if (blade != null)
                {
                    UnityEngine.Object.DestroyImmediate(blade);
                }

                if (rig != null)
                {
                    UnityEngine.Object.DestroyImmediate(rig);
                }
            }
        }

        private static bool FailureIsVisible(List<string> report)
        {
            string file = Path.Combine(
                Path.GetTempPath(), "zantetsu-slash-capture-not-a-directory-" + Guid.NewGuid().ToString("N") + ".txt");
            GameObject rig = null;
            GameObject blade = null;
            try
            {
                File.WriteAllText(file, "not a directory");
                SandboxSlashCapture capture = Sweep("cannotwrite", out _, out rig, out blade);
                capture.Root = Path.Combine(file, "SlashSpan");
                bool saved = capture.TrySave(out string directory, out string failure);
                report.Add("a root that cannot be written reports a failure: " + !saved);
                report.Add("  reason given: " + (failure.Length > 0 ? failure : "(none, which is itself wrong)"));
                report.Add("  directory named by the failed save: " + (directory.Length == 0 ? "(none)" : directory));
                return !saved && failure.Length > 0 && directory.Length == 0;
            }
            catch (Exception exception)
            {
                report.Add("the failure check itself threw: " + exception.GetType().Name + ": " + exception.Message);
                return false;
            }
            finally
            {
                if (blade != null)
                {
                    UnityEngine.Object.DestroyImmediate(blade);
                }

                if (rig != null)
                {
                    UnityEngine.Object.DestroyImmediate(rig);
                }

                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Leaving one temporary file behind is not a failed check.
                }
            }
        }

        private static SandboxSlashCapture Sweep(string runId, out int updates, out GameObject rig, out GameObject blade)
        {
            rig = new GameObject("Slash Capture Path Check Rig");
            blade = new GameObject("Katana");
            SandboxRightHandKatana katana = rig.AddComponent<SandboxRightHandKatana>();
            katana.Katana = blade.transform;

            var capture = new SandboxSlashCapture();
            capture.Begin(runId, katana.CaptureConditions);
            capture.Note = "save path check, synthetic input, no device";
            katana.Capture = capture;

            double time = 0.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            updates = 0;
            for (int i = 0; i < 6; i++)
            {
                katana.TryRecordSample(Tracked(++updates, time, position));
                time += SampleInterval;
                position += Step;
            }

            katana.TryRecordSample(
                new BladePoseSample(++updates, time, Vector3.zero, Quaternion.identity, BladeTrackingState.None));
            time += SampleInterval;
            position = new Vector3(0f, 1.4f, 0.3f);

            for (int i = 0; i < 6; i++)
            {
                katana.TryRecordSample(Tracked(++updates, time, position));
                time += SampleInterval;
                position += Step;
            }

            for (int i = 0; i < 40; i++)
            {
                katana.TryRecordSample(Tracked(++updates, time, position));
                time += SampleInterval;
            }

            capture.Stop();
            katana.Capture = null;
            return capture;
        }

        // Feeds the saved input into a fresh katana and checks the waves come
        // out where current.csv says. This is the whole point of the capture:
        // if this does not hold, the file cannot be compared against later.
        private static bool Recomputes(string directory, List<string> report)
        {
            string[] input = File.ReadAllLines(Path.Combine(directory, "input.csv"));
            string[] recorded = File.ReadAllLines(Path.Combine(directory, "current.csv"));
            var rig = new GameObject("Slash Capture Recompute Rig");
            var blade = new GameObject("Katana");
            try
            {
                SandboxRightHandKatana katana = rig.AddComponent<SandboxRightHandKatana>();
                katana.Katana = blade.transform;

                // The run's own conditions, not this katana's defaults: a
                // recomputation under different tuning is not a recomputation
                // of that run.
                if (!SandboxSlashCapture.TryReadConditions(
                        Path.Combine(directory, "conditions.txt"), out SandboxSlashCapture.Conditions saved,
                        out string readFailure))
                {
                    report.Add("  the saved conditions could not be read: " + readFailure);
                    return false;
                }

                if (!katana.TryApplyCaptureConditions(saved, out string mismatch))
                {
                    report.Add("  the saved conditions could not be applied: " + mismatch);
                    return false;
                }

                var seen = new List<string>();
                for (int i = 1; i < input.Length; i++)
                {
                    string[] f = input[i].Split(',');
                    var sample = new BladePoseSample(
                        long.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture),
                        new Vector3(Parse(f[3]), Parse(f[4]), Parse(f[5])),
                        new Quaternion(Parse(f[6]), Parse(f[7]), Parse(f[8]), Parse(f[9])),
                        (BladeTrackingState)int.Parse(f[10], System.Globalization.CultureInfo.InvariantCulture));
                    bool accepted = katana.TryRecordSample(
                        sample, new Vector3(Parse(f[13]), Parse(f[14]), Parse(f[15])));
                    if (accepted != (f[16] == "1"))
                    {
                        report.Add("  update " + f[0] + " was accepted " + accepted + ", the file says " + f[16]);
                        return false;
                    }

                    for (int w = 0; w < katana.WaveCount; w++)
                    {
                        if (!katana.TryGetWave(w, out double latchedAt, out Plane _, out Vector3 origin,
                                out Vector3 travel, out Vector3 span, out float acceptedSpan, out _, out _,
                                out Vector3 segmentStart, out Vector3 segmentEnd))
                        {
                            continue;
                        }

                        seen.Add(Key(f[0], w, latchedAt, origin, travel, span, acceptedSpan, segmentStart, segmentEnd));
                    }
                }

                var expected = new List<string>();
                for (int i = 1; i < recorded.Length; i++)
                {
                    string[] f = recorded[i].Split(',');
                    expected.Add(Key(
                        f[0],
                        int.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture),
                        new Vector3(Parse(f[8]), Parse(f[9]), Parse(f[10])),
                        new Vector3(Parse(f[11]), Parse(f[12]), Parse(f[13])),
                        new Vector3(Parse(f[14]), Parse(f[15]), Parse(f[16])),
                        Parse(f[17]),
                        new Vector3(Parse(f[24]), Parse(f[25]), Parse(f[26])),
                        new Vector3(Parse(f[27]), Parse(f[28]), Parse(f[29]))));
                }

                report.Add("  wave observations: recomputed " + seen.Count + ", recorded " + expected.Count);
                if (seen.Count != expected.Count)
                {
                    return false;
                }

                for (int i = 0; i < seen.Count; i++)
                {
                    if (seen[i] != expected[i])
                    {
                        report.Add("  first difference at row " + i);
                        report.Add("    recomputed " + seen[i]);
                        report.Add("    recorded   " + expected[i]);
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(blade);
                UnityEngine.Object.DestroyImmediate(rig);
            }
        }

        private static string Key(
            string update, int wave, double latchedAt, Vector3 origin, Vector3 travel, Vector3 span,
            float acceptedSpan, Vector3 segmentStart, Vector3 segmentEnd)
        {
            var text = new StringBuilder(160);
            text.Append(update).Append('|').Append(wave).Append('|').Append(Round(latchedAt)).Append('|')
                .Append(Round(origin)).Append('|').Append(Round(travel)).Append('|').Append(Round(span)).Append('|')
                .Append(Round(acceptedSpan)).Append('|').Append(Round(segmentStart)).Append('|')
                .Append(Round(segmentEnd));
            return text.ToString();
        }

        private static BladePoseSample Tracked(long frameId, double time, Vector3 position)
        {
            return new BladePoseSample(
                frameId, time, position, Quaternion.identity,
                BladeTrackingState.Position | BladeTrackingState.Rotation);
        }

        private static float Parse(string text)
        {
            return float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Round(float value)
        {
            return value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Round(double value)
        {
            return value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Round(Vector3 value)
        {
            return Round(value.x) + ";" + Round(value.y) + ";" + Round(value.z);
        }
    }
}
