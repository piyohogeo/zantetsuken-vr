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
    /// [-candidateEngine M0|N0|N1] [-candidateMeasure 0|1|2] [-candidateNearest sort|select] [-candidateFlow ek|dinic] [-candidatePairs all|sweep]
    /// </para>
    /// -candidatePairs (native engines only; default all): the candidate enumeration by every pair (the baseline) or by the sweep along x.
    /// -candidateFlow (native engines only; default ek): the minimum cut's maximum flow by Edmonds-Karp (the baseline) or Dinic.
    /// -candidateNearest (native engines only; default select): how the three nearest triangle-only seeds are found -- the
    /// full insertion sort (the baseline) or the linear top-3 selection.
    /// -candidateMeasure (native engines only; default 0): 1 adds the search's exclusive section times to each line (the clock is
    /// read at the section boundaries only), 2 the diagnostic counts instead (its times are not a measurement).
    /// -candidateEngine (default M0, the managed candidate) runs stage AB through the native port instead: N0 as managed
    /// code, N1 through Burst (the jobs are warmed up once before the first sample; each line then carries the engine's
    /// timings and whether Burst actually ran). The native port decides AB only.
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
                var engine = (CandidateEngine)Enum.Parse(typeof(CandidateEngine), Argument("-candidateEngine") ?? "M0", true);
                settings.measure = int.Parse(Argument("-candidateMeasure") ?? "0", CultureInfo.InvariantCulture);
                string nearest = Argument("-candidateNearest") ?? "select";
                if (nearest != "sort" && nearest != "select") throw new ArgumentException("-candidateNearest takes sort or select");
                settings.nearestSelection = nearest == "select";
                string flow = Argument("-candidateFlow") ?? "ek";
                if (flow != "ek" && flow != "dinic") throw new ArgumentException("-candidateFlow takes ek or dinic");
                settings.flowDinic = flow == "dinic";
                string pairs = Argument("-candidatePairs") ?? "all";
                if (pairs != "all" && pairs != "sweep") throw new ArgumentException("-candidatePairs takes all or sweep");
                settings.pairsSweep = pairs == "sweep";
                settings.hashPrepare = Array.FindIndex(Environment.GetCommandLineArgs(), a => string.Equals(a, "-candidateHashPrepare", StringComparison.OrdinalIgnoreCase)) >= 0;
                if (settings.measure < 0 || settings.measure > 2) throw new ArgumentException("-candidateMeasure takes 0, 1 or 2");
                if (settings.measure != 0 && engine == CandidateEngine.M0) throw new ArgumentException("-candidateMeasure needs a native engine (N0 or N1)");
                string summary = RunSession(session, output, Argument("-candidateExclude"), Argument("-candidateOnly"), settings, limit, verify, out bool allGood, exact, engine);
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
            bool verifyExact = false, CandidateEngine engine = CandidateEngine.M0)
        {
            string verifyMode = !verify ? "none" : verifyExact ? "exact-only" : "filtered";
            if (engine != CandidateEngine.M0 && settings.stages != CandidateStages.AB) throw new ArgumentException("the native engines decide AB only (-candidateStages AB)");
            int burstRan = 0, nativeSamples = 0;
            string warmedUpOn = "none";
            if (engine != CandidateEngine.M0) SpatialSplitGraphCandidateNative.Warmup(settings.measure, settings.nearestSelection, settings.flowDinic, settings.pairsSweep);
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

                    // The warm-up: the first sample taken is decided once more before it is timed, so that the JIT of the managed
                    // code paths (M0, N0) and Burst's compilation (N1) fall outside every timed decision; recorded in the summary.
                    if (taken == 1)
                    {
                        if (engine == CandidateEngine.M0) SpatialSplitGraphCandidate.Decide(sample, settings); else SpatialSplitGraphCandidateNative.Decide(sample, settings, engine == CandidateEngine.N1, out _);
                        warmedUpOn = entry.world + "/" + entry.attempt;
                    }

                    SpatialSplitGraphCandidateNative.NativeRun nativeRun = null;
                    CandidateResult c = engine == CandidateEngine.M0 ? SpatialSplitGraphCandidate.Decide(sample, settings)
                        : SpatialSplitGraphCandidateNative.Decide(sample, settings, engine == CandidateEngine.N1, out nativeRun);
                    if (nativeRun != null)
                    {
                        nativeSamples++;
                        if (nativeRun.burstRan) burstRan++;
                    }

                    seconds.Add(c.seconds);
                    peakManaged = Math.Max(peakManaged, c.peakManagedBytes);
                    SpatialSplitReferenceBatch.Count(verdicts, c.verdict.ToString());
                    SpatialSplitReferenceBatch.Count(stages, c.verdict + " at " + (c.stage.Length == 0 ? "-" : c.stage));
                    var extra = new StringBuilder();
                    extra.Append("\"engineRequested\":").Append(SpatialSplitCaptureSink.Json(engine.ToString()));
                    if (nativeRun != null) extra.Append(',').Append(SpatialSplitGraphCandidateNative.Json(nativeRun));
                    if (live.TryGetValue((entry.world, entry.attempt), out SpatialSplitResultLine r))
                    {
                        extra.Append(",\"liveEnd\":").Append(SpatialSplitCaptureSink.Json(r.end)).Append(",\"liveOutcome\":").Append(SpatialSplitCaptureSink.Json(r.outcome))
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
                            extra.Append(",\"verified\":").Append(check.valid ? "true" : "false").Append(",\"verifyUnfinished\":").Append(check.unfinished ? "true" : "false")
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
                .AppendLine("engine " + engine + (engine == CandidateEngine.M0 ? " (the managed candidate)" : " (" + SpatialSplitGraphCandidateNative.Version + "); Burst enabled in this Editor: "
                    + Unity.Burst.BurstCompiler.IsEnabled + "; samples that ran through Burst: " + burstRan + " of " + nativeSamples
                    + (engine == CandidateEngine.N1 && burstRan != nativeSamples ? " -- NOT ALL THROUGH BURST" : engine == CandidateEngine.N0 && burstRan != 0 ? " -- N0 RAN THROUGH BURST" : ""))
                    + "; warmed up once on the first sample taken (" + warmedUpOn + ") before its timed decision" + (engine != CandidateEngine.M0 ? ", after the jobs' warm-up on a tiny input" : "")
                    + "; Burst safety checks: " + (Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks ? "on" : "off") + ", Burst compilation: " + (Unity.Burst.BurstCompiler.Options.EnableBurstCompilation ? "on" : "off")
                    + (engine == CandidateEngine.M0 ? "" : "; nearest " + (settings.nearestSelection ? "select (the top-3 selection)" : "sort (the full insertion sort, the baseline)") + "; flow " + (settings.flowDinic ? "dinic" : "ek (Edmonds-Karp, the baseline)") + "; pairs " + (settings.pairsSweep ? "sweep (along x)" : "all (every pair, the baseline)") + (settings.hashPrepare ? "; prepare hash ON (a verification run: its times are not a measurement)" : "") + "; measure " + settings.measure + (settings.measure == 0 ? " (no clock read in the jobs)" : settings.measure == 1 ? " (section times: " + SpatialSplitGraphCandidateNative.ClockDescription() + ")" : " (diagnostic counts; not a timing run)")))
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
