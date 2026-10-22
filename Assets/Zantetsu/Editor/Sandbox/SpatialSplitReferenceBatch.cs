using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Editor.Sandbox
{
    /// <summary>
    /// The reference decider over a capture session, in the Editor: <c>-executeMethod
    /// Zantetsu.Editor.Sandbox.SpatialSplitReferenceBatch.Run -referenceSession &lt;session folder&gt; -referenceOut &lt;a new
    /// folder&gt; [-referenceExclude &lt;holdout list&gt;] [-referenceOnly &lt;list of world TAB attempt&gt;] [-referenceExactOnly]
    /// [-referenceSeconds &lt;per sample, default 120&gt;] [-referenceLimit &lt;at most this many samples&gt;]</c>. With -referenceExactOnly
    /// every pair and feature is judged exactly (the slow reference of the reference, for a fixed representative list). Every sample samples.jsonl lists is read whole, its hash checked against the index and its
    /// identity against the line, and decided (<see cref="SpatialSplitReferenceDecider"/>); a lineage (world, root
    /// fragment) named in the holdout list is skipped and counted. One JSON line a sample into reference.jsonl (flushed
    /// as it goes, with the live result's end and outcome beside it), then summary.txt. The session is only read. Exit
    /// code 0 where every sample was decided (Separable or NotSeparable), 3 where any was Undecided or InvalidInput, 1
    /// where the run could not be made.
    /// </summary>
    public static class SpatialSplitReferenceBatch
    {
        public static void Run()
        {
            int code = 1;
            try
            {
                string session = Argument("-referenceSession");
                string output = Argument("-referenceOut");
                if (session == null || output == null || !Path.IsPathRooted(session) || !Path.IsPathRooted(output))
                {
                    throw new ArgumentException("-referenceSession and -referenceOut take absolute folders");
                }

                double seconds = double.Parse(Argument("-referenceSeconds") ?? "120", CultureInfo.InvariantCulture);
                int limit = int.Parse(Argument("-referenceLimit") ?? "0", CultureInfo.InvariantCulture);
                bool exactOnly = Array.FindIndex(Environment.GetCommandLineArgs(), a => string.Equals(a, "-referenceExactOnly", StringComparison.OrdinalIgnoreCase)) >= 0;
                string summary = RunSession(session, output, Argument("-referenceExclude"), seconds, limit, out bool allDecided, Argument("-referenceOnly"), !exactOnly);
                UnityEngine.Debug.Log("SPLIT REFERENCE:\n" + summary);
                code = allDecided ? 0 : 3;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("SPLIT REFERENCE could not run: " + e);
            }

            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        private static string Argument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int at = Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < arguments.Length && !arguments[at + 1].StartsWith("-", StringComparison.Ordinal) ? arguments[at + 1] : null;
        }

        /// <summary>The lineages (world, root fragment) a holdout list names: tab-separated lines, '#' starting a comment.</summary>
        internal static HashSet<(long, int)> Lineages(string path)
        {
            var set = new HashSet<(long, int)>();
            if (path == null) return set;
            foreach (string line in File.ReadAllLines(path))
            {
                string text = line.Trim();
                if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = text.Split('\t');
                if (parts.Length >= 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long world)
                    && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int root))
                {
                    set.Add((world, root));
                }
            }

            return set;
        }

        internal static List<T> Lines<T>(string path)
        {
            var lines = new List<T>();
            if (!File.Exists(path)) return lines;
            foreach (string line in File.ReadAllLines(path))
            {
                string text = line.Trim();
                if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
                {
                    try
                    {
                        T value = JsonUtility.FromJson<T>(text);
                        if (value != null) lines.Add(value);
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }

            return lines;
        }

        /// <summary>The attempts (world, attempt) a list names: tab-separated lines, '#' starting a comment.</summary>
        internal static HashSet<(long, long)> Attempts(string path)
        {
            var set = new HashSet<(long, long)>();
            if (path == null) return set;
            foreach (string line in File.ReadAllLines(path))
            {
                string text = line.Trim();
                if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = text.Split('	');
                if (parts.Length >= 2 && long.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long world)
                    && long.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long attempt))
                {
                    set.Add((world, attempt));
                }
            }

            return set;
        }

        internal static string RunSession(string sessionDirectory, string outputDirectory, string excludePath, double secondsLimit, int limit, out bool allDecided,
            string onlyPath = null, bool filters = true)
        {
            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            {
                throw new IOException("the output exists already: " + outputDirectory);
            }

            HashSet<(long, int)> excluded = Lineages(excludePath);
            HashSet<(long, long)> only = Attempts(onlyPath);
            List<SpatialSplitSampleLine> samples = Lines<SpatialSplitSampleLine>(Path.Combine(sessionDirectory, "samples.jsonl"));
            var live = new Dictionary<(long, long), SpatialSplitResultLine>();
            foreach (SpatialSplitResultLine r in Lines<SpatialSplitResultLine>(Path.Combine(sessionDirectory, "results.jsonl"))) live[(r.world, r.attempt)] = r;
            string sessionId = "";
            string sessionJson = Path.Combine(sessionDirectory, "session.json");
            if (File.Exists(sessionJson))
            {
                var s = JsonUtility.FromJson<SessionIdOnly>(File.ReadAllText(sessionJson));
                sessionId = s?.sessionId ?? "";
            }

            Directory.CreateDirectory(outputDirectory);
            var verdicts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var seconds = new List<double>();
            int skipped = 0, taken = 0, unreadable = 0, hashDiffers = 0, identityDiffers = 0;
            long peakManaged = 0;
            long begin = Stopwatch.GetTimestamp();
            CultureInfo inv = CultureInfo.InvariantCulture;
            using (var lines = new StreamWriter(Path.Combine(outputDirectory, "reference.jsonl"), false, new UTF8Encoding(false)))
            {
                foreach (SpatialSplitSampleLine entry in samples)
                {
                    if (excluded.Contains((entry.world, entry.rootFragment)) || (onlyPath != null && !only.Contains((entry.world, entry.attempt))))
                    {
                        skipped++;
                        continue;
                    }

                    if (limit > 0 && taken >= limit) break;
                    taken++;
                    string identity = "\"session\":" + SpatialSplitCaptureSink.Json(sessionId) + ",\"world\":" + entry.world + ",\"attempt\":" + entry.attempt
                        + ",\"rootFragment\":" + entry.rootFragment + ",\"lineage\":" + SpatialSplitCaptureSink.Json(sessionId + "/" + entry.world + "/" + entry.rootFragment)
                        + ",\"file\":" + SpatialSplitCaptureSink.Json(entry.file) + ",\"sha256\":" + SpatialSplitCaptureSink.Json(entry.sha256)
                        + ",\"object\":" + SpatialSplitCaptureSink.Json(entry.@object);
                    SpatialSplitSample sample;
                    try
                    {
                        sample = SpatialSplitSample.Read(Path.Combine(sessionDirectory, entry.file));
                    }
                    catch (Exception e)
                    {
                        unreadable++;
                        lines.WriteLine("{" + identity + ",\"verdict\":\"InvalidInput\",\"reason\":" + SpatialSplitCaptureSink.Json("unreadable: " + e.GetType().Name + ": " + e.Message) + "}");
                        Count(verdicts, "InvalidInput (unreadable)");
                        continue;
                    }

                    string refusal = null;
                    if (!string.Equals(sample.sha256, entry.sha256, StringComparison.Ordinal)) refusal = "the content's hash differs from the index's";
                    else if (sample.world != entry.world || sample.attempt != entry.attempt) refusal = "the file's identity differs from the index's";
                    else if (!string.IsNullOrEmpty(sessionId) && !string.Equals(sample.sessionId, sessionId, StringComparison.Ordinal)) refusal = "the file's session differs";
                    if (refusal != null)
                    {
                        if (refusal.StartsWith("the content", StringComparison.Ordinal)) hashDiffers++; else identityDiffers++;
                        lines.WriteLine("{" + identity + ",\"verdict\":\"InvalidInput\",\"reason\":" + SpatialSplitCaptureSink.Json(refusal) + "}");
                        Count(verdicts, "InvalidInput (identity)");
                        continue;
                    }

                    ReferenceDecision d = SpatialSplitReferenceDecider.Decide(sample, secondsLimit, filters);
                    seconds.Add(d.seconds);
                    peakManaged = Math.Max(peakManaged, d.peakManagedBytes);
                    Count(verdicts, d.verdict.ToString());
                    string liveFields = "";
                    if (live.TryGetValue((entry.world, entry.attempt), out SpatialSplitResultLine r))
                    {
                        liveFields = "\"liveEnd\":" + SpatialSplitCaptureSink.Json(r.end) + ",\"liveOutcome\":" + SpatialSplitCaptureSink.Json(r.outcome)
                            + ",\"liveBent\":" + (r.bent ? "true" : "false") + ",\"liveMeshLed\":" + (r.meshLed ? "true" : "false") + ",\"liveCut\":" + (r.cut ? "true" : "false")
                            + ",\"liveSearchObserved\":" + (r.searchObserved ? "true" : "false");
                    }

                    lines.WriteLine(SpatialSplitReferenceDecider.Json(d, identity, liveFields));
                    if ((taken % 200) == 0)
                    {
                        lines.Flush();
                        UnityEngine.Debug.Log("SPLIT REFERENCE: " + taken + " decided, " + ((Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency).ToString("F0", inv) + " s");
                    }
                }
            }

            seconds.Sort();
            var text = new StringBuilder();
            text.AppendLine("spatial split reference decider over the session " + Path.GetFileName(Path.GetFullPath(sessionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .AppendLine(SpatialSplitReferenceDecider.Version + "; " + SpatialSplitReferenceDecider.Numeric)
                .AppendLine((filters ? "with the double certificates" : "EXACT ONLY: every pair and feature judged exactly") + "; time limit a sample " + secondsLimit.ToString("R", inv)
                    + " s; samples listed " + samples.Count + ", skipped (holdout lineages, or not in the list) " + skipped + ", taken " + taken
                    + (limit > 0 ? " (limit " + limit + ")" : ""))
                .AppendLine("unreadable " + unreadable + ", hash differs " + hashDiffers + ", identity differs " + identityDiffers)
                .AppendLine("verdicts: " + Join(verdicts))
                .AppendLine(seconds.Count == 0 ? "seconds a sample: none"
                    : "seconds a sample: median " + seconds[seconds.Count / 2].ToString("F3", inv) + ", p95 " + seconds[Math.Min(seconds.Count - 1, (int)(seconds.Count * 0.95))].ToString("F3", inv)
                        + ", max " + seconds[seconds.Count - 1].ToString("F3", inv) + ", sum " + Sum(seconds).ToString("F1", inv))
                .AppendLine("most managed bytes seen in one sample's decision (GC.GetTotalMemory, garbage included) " + peakManaged + "; the process's peak working set "
                    + Process.GetCurrentProcess().PeakWorkingSet64 + " bytes (0 where the runtime does not report it)");
            allDecided = taken > 0 && unreadable == 0 && hashDiffers == 0 && identityDiffers == 0
                && !verdicts.ContainsKey("Undecided") && !verdicts.ContainsKey("InvalidInput");
            text.AppendLine("every sample taken was decided: " + (allDecided ? "yes" : "NO"));
            File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), text.ToString(), new UTF8Encoding(false));
            return text.ToString();
        }

        [Serializable]
        internal sealed class SessionIdOnly
        {
            public string sessionId;
        }

        internal static void Count(SortedDictionary<string, int> counts, string key) => counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;

        internal static string Join(SortedDictionary<string, int> counts)
        {
            if (counts.Count == 0) return "none";
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in counts) parts.Add(kv.Key + " " + kv.Value);
            return string.Join(", ", parts);
        }

        internal static double Sum(List<double> values)
        {
            double s = 0.0;
            foreach (double v in values) s += v;
            return s;
        }
    }
}
