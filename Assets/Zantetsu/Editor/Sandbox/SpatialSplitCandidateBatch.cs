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
    /// Runs the graph candidate (<see cref="SpatialSplitGraphCandidate"/>) over a capture session's samples, offline, and
    /// verifies every placement it finds with the reference decider's geometric check of the pairs across the sides.
    /// <para>
    /// Unity -batchmode -executeMethod Zantetsu.Editor.Sandbox.SpatialSplitCandidateBatch.Run -candidateSession &lt;session
    /// folder&gt; -candidateOut &lt;new folder&gt; [-candidateExclude &lt;holdout list&gt;] [-candidateOnly &lt;list of world TAB
    /// attempt&gt;] [-candidateStages A|AB|ABC] [-candidateSeconds &lt;limit a sample, 120&gt;] [-candidateLimit &lt;count&gt;]
    /// [-candidateNoVerify | -candidateVerifyExact] [-candidateCutAttempts &lt;8&gt;] [-candidateLeafTests &lt;50000000&gt;]
    /// </para>
    /// Every placement is verified by the reference decider's check of the pairs across its sides: by default with the
    /// reference's double certificates and the exact fallback (which share the E1/E2 error model with the candidate);
    /// with -candidateVerifyExact every such pair exactly (BigInteger; an exact box test skips the far ones), the check
    /// independent of that model; -candidateNoVerify skips it. One JSON line a sample into candidate.jsonl (the
    /// candidate's line, the live result's fields, the verification's fields), summary.txt at the end. Exit 0 only when
    /// the run is complete (every sample judged: no Undecided, no InvalidInput) AND every placement was verified valid
    /// (none invalid, none unfinished, none skipped); 3 otherwise; 1 when it could not run.
    /// </summary>
    public static class SpatialSplitCandidateBatch
    {
        public static void Run()
        {
            int code = 1;
            try
            {
                string session = Argument("-candidateSession");
                string output = Argument("-candidateOut");
                if (session == null || output == null || !Path.IsPathRooted(session) || !Path.IsPathRooted(output))
                {
                    throw new ArgumentException("-candidateSession and -candidateOut take absolute folders");
                }

                var settings = new CandidateSettings
                {
                    stages = (CandidateStages)Enum.Parse(typeof(CandidateStages), Argument("-candidateStages") ?? "ABC", true),
                    secondsLimit = double.Parse(Argument("-candidateSeconds") ?? "120", CultureInfo.InvariantCulture),
                    maxCutAttempts = int.Parse(Argument("-candidateCutAttempts") ?? "8", CultureInfo.InvariantCulture),
                    maxLeafTests = long.Parse(Argument("-candidateLeafTests") ?? "50000000", CultureInfo.InvariantCulture),
                };
                int limit = int.Parse(Argument("-candidateLimit") ?? "0", CultureInfo.InvariantCulture);
                bool verify = Array.FindIndex(Environment.GetCommandLineArgs(), a => string.Equals(a, "-candidateNoVerify", StringComparison.OrdinalIgnoreCase)) < 0;
                bool exact = Array.FindIndex(Environment.GetCommandLineArgs(), a => string.Equals(a, "-candidateVerifyExact", StringComparison.OrdinalIgnoreCase)) >= 0;
                string summary = RunSession(session, output, Argument("-candidateExclude"), Argument("-candidateOnly"), settings, limit, verify, out bool allGood, exact);
                UnityEngine.Debug.Log("SPLIT CANDIDATE:\n" + summary);
                code = allGood ? 0 : 3;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("SPLIT CANDIDATE could not run: " + e);
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

        internal static string RunSession(string sessionDirectory, string outputDirectory, string excludePath, string onlyPath, CandidateSettings settings, int limit, bool verify, out bool allGood,
            bool verifyExact = false)
        {
            string verifyMode = !verify ? "none" : verifyExact ? "exact-only" : "filtered";
            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            {
                throw new IOException("the output exists already: " + outputDirectory);
            }

            HashSet<(long, int)> excluded = SpatialSplitReferenceBatch.Lineages(excludePath);
            HashSet<(long, long)> only = SpatialSplitReferenceBatch.Attempts(onlyPath);
            List<SpatialSplitSampleLine> samples = SpatialSplitReferenceBatch.Lines<SpatialSplitSampleLine>(Path.Combine(sessionDirectory, "samples.jsonl"));
            var live = new Dictionary<(long, long), SpatialSplitResultLine>();
            foreach (SpatialSplitResultLine r in SpatialSplitReferenceBatch.Lines<SpatialSplitResultLine>(Path.Combine(sessionDirectory, "results.jsonl"))) live[(r.world, r.attempt)] = r;
            string sessionId = "";
            string sessionJson = Path.Combine(sessionDirectory, "session.json");
            if (File.Exists(sessionJson))
            {
                var s = JsonUtility.FromJson<SpatialSplitReferenceBatch.SessionIdOnly>(File.ReadAllText(sessionJson));
                sessionId = s?.sessionId ?? "";
            }

            Directory.CreateDirectory(outputDirectory);
            var verdicts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var stages = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var seconds = new List<double>();
            var verifySeconds = new List<double>();
            int skipped = 0, taken = 0, unreadable = 0, hashDiffers = 0, identityDiffers = 0, verified = 0, invalidPlacements = 0, unverified = 0, unfinished = 0;
            long peakManaged = 0;
            long begin = Stopwatch.GetTimestamp();
            CultureInfo inv = CultureInfo.InvariantCulture;
            using (var lines = new StreamWriter(Path.Combine(outputDirectory, "candidate.jsonl"), false, new UTF8Encoding(false)))
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
                        SpatialSplitReferenceBatch.Count(verdicts, "InvalidInput (unreadable)");
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
                        SpatialSplitReferenceBatch.Count(verdicts, "InvalidInput (identity)");
                        continue;
                    }

                    CandidateResult c = SpatialSplitGraphCandidate.Decide(sample, settings);
                    seconds.Add(c.seconds);
                    peakManaged = Math.Max(peakManaged, c.peakManagedBytes);
                    SpatialSplitReferenceBatch.Count(verdicts, c.verdict.ToString());
                    SpatialSplitReferenceBatch.Count(stages, c.verdict + " at " + (c.stage.Length == 0 ? "-" : c.stage));
                    var extra = new StringBuilder();
                    if (live.TryGetValue((entry.world, entry.attempt), out SpatialSplitResultLine r))
                    {
                        extra.Append("\"liveEnd\":").Append(SpatialSplitCaptureSink.Json(r.end)).Append(",\"liveOutcome\":").Append(SpatialSplitCaptureSink.Json(r.outcome))
                            .Append(",\"liveBent\":").Append(r.bent ? "true" : "false").Append(",\"liveMeshLed\":").Append(r.meshLed ? "true" : "false")
                            .Append(",\"liveCut\":").Append(r.cut ? "true" : "false").Append(",\"liveSearchObserved\":").Append(r.searchObserved ? "true" : "false");
                    }

                    if (c.verdict == CandidateVerdict.Separable)
                    {
                        if (verify)
                        {
                            SpatialSplitReferenceDecider.PlacementCheck check = SpatialSplitReferenceDecider.VerifyPlacement(sample, c.convexPositive, c.trianglePositive, settings.secondsLimit * (verifyExact ? 30.0 : 4.0), !verifyExact);
                            verifySeconds.Add(check.seconds);
                            if (check.valid) verified++; else if (check.unfinished) unfinished++; else invalidPlacements++;
                            if (extra.Length > 0) extra.Append(',');
                            extra.Append("\"verified\":").Append(check.valid ? "true" : "false").Append(",\"verifyUnfinished\":").Append(check.unfinished ? "true" : "false")
                                .Append(",\"verifyMode\":").Append(SpatialSplitCaptureSink.Json(verifyMode)).Append(",\"verifyReason\":").Append(SpatialSplitCaptureSink.Json(check.reason))
                                .Append(",\"verifyPairsAcross\":").Append(check.pairsAcross).Append(",\"verifyPairsExact\":").Append(check.pairsJudgedExactly)
                                .Append(",\"verifySeconds\":").Append(check.seconds.ToString("R", inv));
                        }
                        else
                        {
                            unverified++;
                        }
                    }

                    lines.WriteLine(SpatialSplitGraphCandidate.Json(c, identity, extra.ToString()));
                    if ((taken % 200) == 0)
                    {
                        lines.Flush();
                        UnityEngine.Debug.Log("SPLIT CANDIDATE: " + taken + " judged, " + ((Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency).ToString("F0", inv) + " s");
                    }
                }
            }

            seconds.Sort();
            verifySeconds.Sort();
            var text = new StringBuilder();
            text.AppendLine("spatial split graph candidate over the session " + Path.GetFileName(Path.GetFullPath(sessionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .AppendLine(SpatialSplitGraphCandidate.Version + "; stages " + settings.stages + "; time limit a sample " + settings.secondsLimit.ToString("R", inv) + " s; cut attempts " + settings.maxCutAttempts
                    + "; leaf tests " + settings.maxLeafTests + "; GJK iterations " + settings.gjkMaxIterations)
                .AppendLine("samples listed " + samples.Count + ", skipped (holdout lineages, or not in the list) " + skipped + ", taken " + taken + (limit > 0 ? " (limit " + limit + ")" : ""))
                .AppendLine("unreadable " + unreadable + ", hash differs " + hashDiffers + ", identity differs " + identityDiffers)
                .AppendLine("verdicts: " + SpatialSplitReferenceBatch.Join(verdicts))
                .AppendLine("verdicts by stage: " + SpatialSplitReferenceBatch.Join(stages))
                .AppendLine("verification of the placements: mode " + verifyMode + (verify ? " (" + (verifyExact ? "every pair across the sides exactly; far pairs skipped by an exact box test"
                    : "the reference's double certificates with the exact fallback: the same E1/E2 error model as the candidate's") + ")" : ""))
                .AppendLine("placements: verified valid " + verified + ", INVALID " + invalidPlacements + ", verification unfinished (time) " + unfinished + ", not verified (skipped) " + unverified)
                .AppendLine(seconds.Count == 0 ? "seconds a sample: none" : "seconds a sample: " + Quantiles(seconds, inv))
                .AppendLine(verifySeconds.Count == 0 ? "seconds a verification: none" : "seconds a verification: " + Quantiles(verifySeconds, inv))
                .AppendLine("most managed bytes seen in one sample's decision (GC.GetTotalMemory, garbage included) " + peakManaged);
            bool complete = taken > 0 && unreadable == 0 && hashDiffers == 0 && identityDiffers == 0 && !verdicts.ContainsKey("Undecided") && !verdicts.ContainsKey("InvalidInput");
            bool allVerified = verify && invalidPlacements == 0 && unfinished == 0 && unverified == 0;
            allGood = complete && allVerified;
            text.AppendLine("run complete (every sample taken was judged): " + (complete ? "yes" : "NO"))
                .AppendLine("every placement verified valid (none invalid, unfinished or skipped): " + (allVerified ? "yes" : "NO"));
            File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), text.ToString(), new UTF8Encoding(false));
            return text.ToString();
        }

        private static string Quantiles(List<double> sorted, CultureInfo inv) =>
            "median " + sorted[sorted.Count / 2].ToString("F3", inv) + ", p95 " + sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))].ToString("F3", inv)
            + ", p99 " + sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))].ToString("F3", inv) + ", max " + sorted[sorted.Count - 1].ToString("F3", inv)
            + ", sum " + SpatialSplitReferenceBatch.Sum(sorted).ToString("F1", inv);
    }
}
