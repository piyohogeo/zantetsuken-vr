#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    public static unsafe partial class SpatialSplitMethodEval
    {
        [Serializable]
        private sealed class CaptureListLine
        {
            public string session;
            public long world, attempt;
            public string sha256, boneSha256, topologySha256, category, piece, model;
            public int fragment, epoch;
            public bool firstOfPiece;
        }

        /// <summary>
        /// NPC-PAIR-EVAL-2: the three methods over the attempts a play's capture saved (NPC-ATTEMPT-CAPTURE-1), as its fixed list names
        /// them: each line's sample, bones' sidecar and topology table read from the session folder and checked before anything runs
        /// of it -- the three files' SHA-256 against the list's, the session, the attempt, the fragment and the epoch in all three,
        /// the list's category against the sample's generation and split source, the list's piece against the table's geometry, the
        /// table covering every vertex the sample names, every vertex of the saved lineage -- and decided with the saved table as it
        /// stands. An input carries no pointed point and no intent: those are not taken (the analysis says so).
        /// <para>
        /// One input at a time: read, restored, run once in every method to warm (no line), run <paramref name="repeats"/> times in
        /// every method (the order (input + repeat) mod 3), analysed, given back -- so that one input's data is held at once. The
        /// reading, the restoring and the writing are outside every time. Into a new folder: inputs.jsonl, baseline.jsonl (the bones
        /// alone over the saved input beside the live line), runs.jsonl, analysis.jsonl, losses.jsonl (a placement with a loss, in
        /// full), refused.jsonl (an input that did not pass its checks: nothing run of it), summary.txt.
        /// </para>
        /// <paramref name="exitCode"/>: 0 every listed input was taken and every run completed, 3 not, 1 nothing was run.
        /// </summary>
        public static string RunCaptureList(string sessionDirectory, string listPath, string outputDirectory, int repeats, out int exitCode, int first = 0, int count = int.MaxValue)
        {
            exitCode = 1;
            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)) throw new IOException("the output exists already: " + outputDirectory);
            if (repeats < 1) throw new ArgumentOutOfRangeException(nameof(repeats));
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            var utf8 = new UTF8Encoding(false);
            string sessionId = ReadSessionId(sessionDirectory);
            var lines = new List<CaptureListLine>();
            foreach (string raw in File.ReadAllLines(listPath))
            {
                string t = raw.Trim().TrimStart('﻿');
                if (t.Length == 0 || !t.StartsWith("{", StringComparison.Ordinal)) continue;
                lines.Add(JsonUtility.FromJson<CaptureListLine>(t));
            }

            var live = new Dictionary<(long, long), SpatialSplitBoneResultLine>();
            string livePath = Path.Combine(sessionDirectory, "bone-results.jsonl");
            if (File.Exists(livePath))
            {
                foreach (string raw in File.ReadAllLines(livePath))
                {
                    string t = raw.Trim();
                    if (t.Length == 0 || !t.StartsWith("{", StringComparison.Ordinal)) continue;
                    SpatialSplitBoneResultLine l = JsonUtility.FromJson<SpatialSplitBoneResultLine>(t);
                    live[(l.world, l.attempt)] = l;
                }
            }

            string listHash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var s = new StringBuilder(64);
                foreach (byte b in sha.ComputeHash(File.ReadAllBytes(listPath))) s.Append(b.ToString("x2", inv));
                listHash = s.ToString();
            }

            text.Append("NPC-PAIR-EVAL-2 pairwise combination over captured attempts: session ").Append(sessionId).Append("; list ").Append(listPath).Append(" (").Append(lines.Count)
                .Append(" lines, sha256 ").Append(listHash).Append("); live lines ").Append(live.Count).Append("; from line ").Append(first).Append('\n');
            if (lines.Count == 0)
            {
                text.Append("NOT RUN: the list is empty\n");
                return text.ToString();
            }

            Directory.CreateDirectory(outputDirectory);
            long p0 = Stopwatch.GetTimestamp();
            bool graphPrepared = SpatialSplitGraph.IsPrepared || SpatialSplitGraph.Prepare();
            double prepareGraph = Since(p0);
            int taken = 0, refused = 0, failed = 0, measured = 0, warmFailed = 0, analysed = 0, analysisFailed = 0, withLoss = 0;
            int baseSame = 0, baseDiffer = 0, baseNoLive = 0;
            var baseVerdicts = new Dictionary<string, int>();
            long started = Stopwatch.GetTimestamp();
            using (var pool = new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Pc, SpatialSplitReplayPoolLoad.Level, 0, SpatialSplitReplayOptions.DefaultCompetitorWork, SpatialSplitReplayPool.DefaultBlockerWork))
            using (var inputsFile = new StreamWriter(Path.Combine(outputDirectory, "inputs.jsonl"), false, utf8))
            using (var baseline = new StreamWriter(Path.Combine(outputDirectory, "baseline.jsonl"), false, utf8))
            using (var runsFile = new StreamWriter(Path.Combine(outputDirectory, "runs.jsonl"), false, utf8))
            using (var analysisFile = new StreamWriter(Path.Combine(outputDirectory, "analysis.jsonl"), false, utf8))
            using (var lossesFile = new StreamWriter(Path.Combine(outputDirectory, "losses.jsonl"), false, utf8))
            using (var refusedFile = new StreamWriter(Path.Combine(outputDirectory, "refused.jsonl"), false, utf8))
            {
                text.Append("platform ").Append(Application.platform).Append(' ').Append(Application.unityVersion).Append("; ").Append(Application.isEditor ? "Editor" : UnityEngine.Debug.isDebugBuild ? "Development Player" : "Player")
#if ENABLE_IL2CPP
                    .Append(", IL2CPP")
#else
                    .Append(", Mono")
#endif
                    .Append("; Burst enabled ").Append(Unity.Burst.BurstCompiler.IsEnabled).Append("; processors ").Append(SystemInfo.processorCount).Append(' ').Append(SystemInfo.processorType)
                    .Append("; pool pc (Background ").Append(pool.Capacity).Append("), no competitor; timeline ").Append(CutSpatialSplit.TimelineObserve ? "on" : "off")
                    .Append(", dispatch observation ").Append(Zantetsu.MeshCut.DispatchObservation.Enabled ? "on" : "off").Append('\n');
                text.Append("methods: ").Append(string.Join(", ", PairMethodSet)).Append("; each input ").Append(PairWarm ? "warmed once in every method" : "NOT warmed (a re-decision, no time is read)").Append(", then ").Append(repeats)
                    .Append(" repeats; order by (input + repeat) mod 3; no clock inside the graph bodies in any method (measure 0); a fallback's graph stage after the combination ")
                    .Append(CutSpatialSplit.PairReuse ? "reuses the combination's decision (NPC-PAIR-REUSE-1)" : "runs from the start (" + CutSpatialSplit.PairReuseOffArgument + ")").Append('\n');
                text.Append("once per process: graph preparation ").Append(Ms(prepareGraph, inv)).Append(" ms (prepared ").Append(graphPrepared).Append(")\n");
                int last = (int)Math.Min((long)lines.Count, (long)first + count);
                for (int index = Math.Max(0, first); index < last; index++)
                {
                    CaptureListLine l = lines[index];
                    string name = l.world + "/" + l.attempt;
                    Input input = null;
                    string why = null;
                    NpcPieceTopologySample table = null;
                    try
                    {
                        // ---- read and checked (not timed)
                        if (!string.Equals(l.session, sessionId, StringComparison.Ordinal)) why = "the list's session is not the folder's";
                        SpatialSplitSample s = why == null ? SpatialSplitSample.Read(Path.Combine(sessionDirectory, SpatialSplitCaptureSink.SampleFileName(l.world, l.attempt))) : null;
                        SpatialSplitBoneSample b = why == null ? SpatialSplitBoneSample.Read(Path.Combine(sessionDirectory, SpatialSplitBoneSample.FileName(l.world, l.attempt))) : null;
                        table = why == null ? NpcPieceTopologySample.Read(Path.Combine(sessionDirectory, NpcPieceTopologySample.FileName(l.world, l.attempt))) : null;
                        if (why == null)
                        {
                            string category = s.splitSource != 0 ? "split-child" : s.generation <= 1 ? "first-cut" : "recut";
                            string piece = s.world + ":" + s.fragment + ":" + s.epoch + ":" + table.geometry;
                            string check = table.Check();
                            if (!string.Equals(s.sha256, l.sha256, StringComparison.Ordinal)) why = "the sample's SHA-256 is not the list's";
                            else if (!string.Equals(b.sha256, l.boneSha256, StringComparison.Ordinal)) why = "the bones' SHA-256 is not the list's";
                            else if (!string.Equals(table.sha256, l.topologySha256, StringComparison.Ordinal)) why = "the topology's SHA-256 is not the list's";
                            else if (s.world != l.world || s.attempt != l.attempt || b.world != l.world || b.attempt != l.attempt || table.world != l.world || table.pick != l.attempt) why = "a file is of another attempt";
                            else if (s.sessionId != sessionId || b.sessionId != sessionId || table.sessionId != sessionId) why = "a file is of another session";
                            else if (s.fragment != l.fragment || b.fragment != l.fragment || table.fragment != l.fragment) why = "a file is of another fragment";
                            else if (s.epoch != l.epoch || b.epoch != l.epoch || table.epoch != l.epoch) why = "a file is of another epoch";
                            else if (!string.Equals(category, l.category, StringComparison.Ordinal)) why = "the sample's generation and split source say " + category + ", the list " + l.category;
                            else if (!string.Equals(piece, l.piece, StringComparison.Ordinal)) why = "the topology's geometry is not the list's piece";
                            else if (check != null) why = "the topology: " + check;
                            else if (b.convexBones.Length != s.convexes.Length) why = "the bones name " + b.convexBones.Length + " convexes, the sample has " + s.convexes.Length;
                            else if (table.vertexIds.Length != s.vertexIds.Length) why = "the topology has " + table.vertexIds.Length + " vertices, the sample names " + s.vertexIds.Length;
                            else
                            {
                                foreach (uint id in s.vertexIds)
                                {
                                    if (table.TopologyOf(id) < 0) { why = "the topology has no entry for vertex " + id; break; }
                                    if (NpcPiecePickSet.LocalOf(b, id) < 0) { why = "vertex " + id + " is not of the saved lineage"; break; }
                                }
                            }
                        }

                        if (why == null)
                        {
                            input = new Input { index = index, category = l.category ?? "", sample = s, bones = b, pick = checked((int)l.attempt), topologySource = "saved" };
                            live.TryGetValue((l.world, l.attempt), out input.live);
                            var pick = new NpcPiecePickSet.Pick { number = input.pick, sample = s, bones = b, topology = table };
                            input.topologyIds = NpcPiecePickSet.SavedTopologyOfLocal(pick, out input.topologyCount);

                            // ---- the bones alone over the saved input, beside the live line (not timed)
                            NpcBoneSplitRecord d = NpcPiecePickSet.Decide(pick, out byte[] sides, out byte[] _);
                            int c = b.convexBones.Length, tri = s.indices.Length / 3;
                            string hash = d.verdict == NpcBoneSplitVerdict.Separable ? NpcPiecePickSet.SidesHash(sides, c, tri) : "";
                            SpatialSplitBoneResultLine v = input.live;
                            string agreement = "no live line";
                            if (v != null)
                            {
                                bool same = string.Equals(v.verdict, d.verdict.ToString(), StringComparison.Ordinal) && string.Equals(v.sidesHash ?? "", hash, StringComparison.Ordinal)
                                    && v.clusters == d.clusters && v.groups == d.groups && v.fullGroups == d.fullGroups && v.meshComponents == d.meshComponents
                                    && v.positiveConvexes == d.positiveConvexes && v.negativeConvexes == d.negativeConvexes && v.positiveTriangles == d.positiveTriangles && v.negativeTriangles == d.negativeTriangles
                                    && v.connectionsKept == d.connectionsKept && v.connectionsDropped == d.connectionsDropped && v.connectionsProtected == d.connectionsProtected;
                                agreement = same ? "agrees" : "DIFFERS";
                                if (same) baseSame++; else baseDiffer++;
                            }
                            else baseNoLive++;

                            baseVerdicts[d.verdict.ToString()] = baseVerdicts.TryGetValue(d.verdict.ToString(), out int had) ? had + 1 : 1;
                            baseline.Write("{\"index\":" + index + ",\"attempt\":" + l.attempt + ",\"verdict\":\"" + d.verdict + "\",\"reason\":" + SpatialSplitCaptureSink.Json(d.reason ?? "") + ",\"clusters\":" + d.clusters
                                + ",\"groups\":" + d.groups + ",\"fullGroups\":" + d.fullGroups + ",\"meshComponents\":" + d.meshComponents + ",\"connectionsKept\":" + d.connectionsKept
                                + ",\"connectionsDropped\":" + d.connectionsDropped + ",\"connectionsProtected\":" + d.connectionsProtected + ",\"positiveTriangles\":" + d.positiveTriangles
                                + ",\"negativeTriangles\":" + d.negativeTriangles + ",\"sidesHash\":\"" + hash + "\",\"live\":\"" + (v != null ? v.verdict : "") + "\",\"agreement\":\"" + agreement + "\"}\n");
                            Components(input, out int bySlot, out int byTopology);
                            inputsFile.Write("{\"index\":" + index + ",\"pick\":" + input.pick + ",\"world\":" + s.world + ",\"attempt\":" + l.attempt + ",\"fragment\":" + s.fragment + ",\"rootFragment\":" + s.rootFragment
                                + ",\"splitSource\":" + s.splitSource + ",\"generation\":" + s.generation + ",\"epoch\":" + s.epoch + ",\"category\":\"" + l.category + "\",\"model\":\"" + (l.model ?? "")
                                + "\",\"convexes\":" + c + ",\"triangles\":" + tri + ",\"namedVertices\":" + s.vertexIds.Length + ",\"bones\":" + b.nodeCount
                                + ",\"meshComponentsByRenderVertex\":" + bySlot + ",\"meshComponentsByTopology\":" + byTopology
                                + ",\"graphSearch\":" + B(b.graphSearch) + ",\"graphFirst\":" + B(b.graphFirst) + ",\"bentSearch\":" + B(s.bentSearch) + ",\"cutConvexes\":" + B(s.cutConvexes)
                                + ",\"anchors\":" + (s.anchors != null ? s.anchors.Length : 0) + ",\"hit\":\"not taken\",\"intent\":\"not taken\""
                                + ",\"liveVerdict\":\"" + (v != null ? v.verdict : "") + "\",\"liveEnd\":\"" + (v != null ? v.end : "") + "\",\"livePath\":\"" + (v != null ? v.path ?? "" : "") + "\"}\n");
                            MakeNative(input);
                        }
                    }
                    catch (Exception e)
                    {
                        why = e.GetType().Name + ": " + e.Message;
                        if (input != null) FreeNative(input);
                        input = null;
                    }

                    if (input == null)
                    {
                        refused++;
                        refusedFile.Write("{\"index\":" + index + ",\"world\":" + l.world + ",\"attempt\":" + l.attempt + ",\"why\":" + SpatialSplitCaptureSink.Json(why ?? "") + "}\n");
                        continue;
                    }

                    taken++;
                    try
                    {
                        // ---- warmed, then measured
                        foreach (string method in PairMethods(index))
                        {
                            if (!PairWarm) break;
                            Run w = RunOne(input, method, pool);
                            if (!w.ok) warmFailed++;
                        }

                        var firstRuns = new Dictionary<string, Run>();
                        for (int repeat = 0; repeat < repeats; repeat++)
                        {
                            string[] order = PairMethods(index + repeat);
                            for (int position = 0; position < order.Length; position++)
                            {
                                Run r = RunOne(input, order[position], pool);
                                measured++;
                                if (!r.ok) failed++;
                                runsFile.Write(PairLine(input, repeat, position, r, inv));
                                if (!firstRuns.ContainsKey(r.method)) firstRuns[r.method] = r;
                            }
                        }

                        // ---- what each placement means (not timed)
                        foreach (string method in PairMethodSet)
                        {
                            if (!firstRuns.TryGetValue(method, out Run r)) continue;
                            string body = "null", error = null;
                            if (r.ok && r.triangleSides != null && r.convexSides != null)
                            {
                                try
                                {
                                    SpatialSplitPairAnalysis a = SpatialSplitPairAnalysis.Of(input.sample, input.bones, input.topologyIds, new float3(float.NaN), r.convexSides, r.triangleSides);
                                    body = a.Json();
                                    analysed++;
                                    if (a.HasLoss)
                                    {
                                        withLoss++;
                                        lossesFile.Write("{\"index\":" + index + ",\"attempt\":" + l.attempt + ",\"method\":\"" + method + "\",\"placement\":\"" + r.placement + "\",\"detail\":" + a.LossJson() + "}\n");
                                    }
                                }
                                catch (Exception e)
                                {
                                    error = e.GetType().Name + ": " + e.Message;
                                    analysisFailed++;
                                }
                            }

                            analysisFile.Write("{\"index\":" + index + ",\"pick\":" + input.pick + ",\"method\":\"" + method + "\",\"end\":" + (r.end != null ? "\"" + r.end + "\"" : "null") + ",\"placement\":\"" + r.placement
                                + "\",\"pairVerdict\":" + (r.pair != null ? "\"" + r.pair.verdict + "\"" : "null") + ",\"partition\":\"" + PartitionHash(r.convexSides, r.triangleSides)
                                + "\",\"needPositive\":" + NeedJson(r.needPositive) + ",\"needNegative\":" + NeedJson(r.needNegative)
                                + ",\"needFailure\":" + (r.needFailure != null ? SpatialSplitCaptureSink.Json(r.needFailure) : "null")
                                + ",\"analysisError\":" + (error != null ? SpatialSplitCaptureSink.Json(error) : "null") + ",\"analysis\":" + body + "}\n");
                        }
                    }
                    finally
                    {
                        FreeNative(input);
                    }

                    if ((taken % 100) == 0)
                    {
                        runsFile.Flush(); analysisFile.Flush(); inputsFile.Flush(); baseline.Flush(); lossesFile.Flush(); refusedFile.Flush();
                        UnityEngine.Debug.Log("SPLIT PAIR EVAL: " + taken + " inputs done (" + measured + " runs, failed " + failed + ", refused " + refused + ") after " + Since(started).ToString("F0", inv) + " s");
                    }
                }

                text.Append("inputs taken ").Append(taken).Append(", refused ").Append(refused).Append("; warm-up runs failed ").Append(warmFailed).Append("; measured ").Append(measured).Append(" runs, failed ").Append(failed)
                    .Append("; in ").Append(Since(started).ToString("F0", inv)).Append(" s\n");
                text.Append("the bones alone over the saved inputs: ");
                foreach (KeyValuePair<string, int> v in baseVerdicts) text.Append(v.Key).Append(' ').Append(v.Value).Append("; ");
                text.Append("against the live lines: agree ").Append(baseSame).Append(", differ ").Append(baseDiffer).Append(", no live line ").Append(baseNoLive).Append('\n');
                text.Append("analysis: ").Append(analysed).Append(" placements read (failed ").Append(analysisFailed).Append("), with a loss ").Append(withLoss).Append('\n');
                text.Append(pool.Describe()).Append('\n');
            }

            File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), text.ToString(), utf8);
            exitCode = refused == 0 && failed == 0 && analysisFailed == 0 && baseDiffer == 0 ? 0 : 3;
            return text.ToString();
        }
    }
}
#endif
