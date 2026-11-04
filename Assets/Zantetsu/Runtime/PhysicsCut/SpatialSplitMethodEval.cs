#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    [Serializable]
    internal sealed class SpatialSplitMethodEvalListLine
    {
        public string session;
        public long world, attempt;
        public string sha256, boneSha256, category;
    }

    [Serializable]
    internal sealed class SpatialSplitBoneResultLine
    {
        public long world, attempt;
        public string verdict, sidesHash, end;
        public int clusters, groups, fullGroups, meshComponents, positiveConvexes, negativeConvexes, positiveTriangles, negativeTriangles;
    }

    /// <summary>
    /// NPC-BONE-EVAL-1: the comparison of the two additional splits of an NPC's fragment on one fixed data set, in one process: the
    /// method as before (従来方式: the plane, the graph, the bent partition and the mesh-led plane in the product's order, with the
    /// settings the issue had) and the bone-aware one (Bone-aware alone, no fallback), each run from its own copy of the same captured
    /// input (the sample and its bones' sidecar, both checked against the list's SHA-256 before anything runs). Every input is run once
    /// in both methods to warm them (no line), then every input <c>repeats</c> times in both, the leading method alternating by the
    /// input's position and flipping with each repeat. One run's times, from the existing clocks and one boundary stamp:
    /// <list type="bullet">
    /// <item>prepare (Main): the issue's own part -- the attempt, its block, the submesh and convex tables, the classification block, the
    /// arena, and the method's own (the graph's switch and sides; the bones' input and sides). Not in it: the storage lease, the shape's
    /// work hold, the ledger's anchors, the dispatcher's enqueue (the world's, the same for both) and the replay's own making of the
    /// vertices, the indices and the shape (its stand-in for the storage).</item>
    /// <item>decision (the Background worker): Begin's start to the placement known (all of Begin when nothing is shared out).</item>
    /// <item>distribution (the worker): the sharing out of the triangles, the anchors, the classification and the mass kernel.</item>
    /// <item>cut meshes (Main) and bake (the worker): only for a placement that cuts convexes (the method as before), the next stages
    /// before a publication; the hull check and the publication itself are not run.</item>
    /// </list>
    /// The total is their sum. A run that failed has no times (never 0). Main thread; the pool replay's real Background pool, no competitor.
    /// </summary>
    public static unsafe class SpatialSplitMethodEval
    {
        public const string Legacy = "legacy", Bone = "bone", BoneFallback = "bone-fallback";

        // The methods of one position: the two alternating, then -- asked for -- the bones with the fallback (NPC-BONE-FALLBACK-1) last.
        private static string[] Methods(int parity, bool boneFallback)
        {
            if (boneFallback) return parity % 2 == 0 ? new[] { Legacy, Bone, BoneFallback } : new[] { Bone, Legacy, BoneFallback };
            return parity % 2 == 0 ? new[] { Legacy, Bone } : new[] { Bone, Legacy };
        }
        public const int DefaultRepeats = 3;

        private sealed class Input
        {
            internal int index;
            internal string category;
            internal SpatialSplitSample sample;
            internal SpatialSplitBoneSample bones;
            internal NativeArray<ulong> baseBones;
            internal NativeArray<uint2>[] origins;
            internal NpcVertexChunk[] chunks;
            internal SpatialSplitBoneResultLine live;
        }

        private sealed class Run
        {
            internal string method, error, end, detail, sidesHash, sidesHashEngine, graphVerdict, searchOutcome, boneVerdict, boneReason;
            internal bool ok, cut, graphLed, bent, meshLed, graphBurstRan, kernelManaged, warm, fellBack;
            internal double prepare = double.NaN, decision = double.NaN, distribution = double.NaN, meshes = double.NaN, bake = double.NaN, worker = double.NaN;
            internal long blockBytes, classificationBytes, sidesBytes, workerNativeBytes = -1, prepareManagedBytes = -1;
            internal int positiveTriangles, negativeTriangles, positiveConvexes, negativeConvexes, clusters, groups, fullGroups, meshComponents;
            internal int pieceBones = -1, unboundGroups = -1;
            internal long removedWeights = -1;
            internal bool? liveMatch;
        }

        /// <summary>
        /// Runs the list's inputs (each line: session, world, attempt, sha256, boneSha256, category) from the session folder into a new
        /// output folder (runs.jsonl, inputs.jsonl, summary.txt); the summary. <paramref name="exitCode"/>: 0 every run completed and every
        /// bone-aware run agreed with the live decision, 3 not, 1 the inputs could not be taken as listed (nothing was run).
        /// </summary>
        public static string RunList(string sessionDirectory, string listPath, string outputDirectory, int repeats, out int exitCode, bool boneFallback = false)
        {
            exitCode = 1;
            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)) throw new IOException("the output exists already: " + outputDirectory);
            if (repeats < 1) throw new ArgumentOutOfRangeException(nameof(repeats));
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            string sessionId = ReadSessionId(sessionDirectory);
            var lines = new List<SpatialSplitMethodEvalListLine>();
            foreach (string raw in File.ReadAllLines(listPath))
            {
                string t = raw.Trim();
                if (t.Length == 0 || !t.StartsWith("{", StringComparison.Ordinal)) continue;
                lines.Add(JsonUtility.FromJson<SpatialSplitMethodEvalListLine>(t));
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

            // 1. The inputs, read and checked whole before anything runs: the list's session, each file's SHA-256, the sidecar's identity.
            var inputs = new List<Input>();
            var refusals = new List<string>();
            var seen = new HashSet<(long, long)>();
            foreach (SpatialSplitMethodEvalListLine l in lines)
            {
                string name = l.world + "/" + l.attempt;
                if (!string.Equals(l.session, sessionId, StringComparison.Ordinal)) { refusals.Add(name + ": the list's session " + l.session + " is not the folder's " + sessionId); continue; }
                if (!seen.Add((l.world, l.attempt))) { refusals.Add(name + ": listed twice"); continue; }
                try
                {
                    SpatialSplitSample s = SpatialSplitSample.Read(Path.Combine(sessionDirectory, SpatialSplitCaptureSink.SampleFileName(l.world, l.attempt)));
                    SpatialSplitBoneSample b = SpatialSplitBoneSample.Read(Path.Combine(sessionDirectory, SpatialSplitBoneSample.FileName(l.world, l.attempt)));
                    if (!string.Equals(s.sha256, l.sha256, StringComparison.Ordinal)) { refusals.Add(name + ": the sample's SHA-256 " + s.sha256 + " is not the list's"); continue; }
                    if (!string.Equals(b.sha256, l.boneSha256, StringComparison.Ordinal)) { refusals.Add(name + ": the bones' SHA-256 " + b.sha256 + " is not the list's"); continue; }
                    if (b.world != s.world || b.attempt != s.attempt || b.fragment != s.fragment || b.epoch != s.epoch || !string.Equals(b.sessionId, sessionId, StringComparison.Ordinal))
                    {
                        refusals.Add(name + ": the bones' sidecar is of another attempt");
                        continue;
                    }

                    if (b.convexBones.Length != s.convexes.Length) { refusals.Add(name + ": the bones name " + b.convexBones.Length + " convexes, the sample has " + s.convexes.Length); continue; }
                    var input = new Input { index = inputs.Count, category = l.category ?? "", sample = s, bones = b };
                    live.TryGetValue((l.world, l.attempt), out input.live);
                    MakeNative(input);
                    inputs.Add(input);
                }
                catch (Exception e)
                {
                    refusals.Add(name + ": " + e.GetType().Name + ": " + e.Message);
                }
            }

            text.Append("NPC-BONE-EVAL-1 method comparison: session ").Append(sessionId).Append("; list ").Append(listPath).Append(" (").Append(lines.Count).Append(" lines); inputs taken ")
                .Append(inputs.Count).Append("; refused ").Append(refusals.Count).Append('\n');
            foreach (string r in refusals) text.Append("  REFUSED ").Append(r).Append('\n');
            if (refusals.Count > 0 || inputs.Count == 0 || inputs.Count != lines.Count)
            {
                text.Append("NOT RUN: the inputs are not the list's as fixed (nothing measured)\n");
                foreach (Input i in inputs) FreeNative(i);
                return text.ToString();
            }

            Directory.CreateDirectory(outputDirectory);
            using (var inputsFile = new StreamWriter(Path.Combine(outputDirectory, "inputs.jsonl"), false, new UTF8Encoding(false)))
            {
                foreach (Input i in inputs)
                {
                    inputsFile.Write("{\"index\":" + i.index + ",\"world\":" + i.sample.world + ",\"attempt\":" + i.sample.attempt + ",\"category\":" + SpatialSplitCaptureSink.Json(i.category)
                        + ",\"sha256\":\"" + i.sample.sha256 + "\",\"boneSha256\":\"" + i.bones.sha256 + "\",\"fragment\":" + i.sample.fragment + ",\"rootFragment\":" + i.sample.rootFragment
                        + ",\"splitSource\":" + i.sample.splitSource + ",\"generation\":" + i.sample.generation + ",\"convexes\":" + i.sample.convexes.Length + ",\"triangles\":" + (i.sample.indices.Length / 3)
                        + ",\"bones\":" + i.bones.nodeCount + ",\"chunks\":" + i.bones.chunkStarts.Length + ",\"lineageVertices\":" + i.bones.localCount
                        + ",\"graphSearch\":" + B(i.bones.graphSearch) + ",\"graphFirst\":" + B(i.bones.graphFirst) + ",\"bentSearch\":" + B(i.sample.bentSearch) + ",\"cutConvexes\":" + B(i.sample.cutConvexes)
                        + ",\"anchors\":" + (i.sample.anchors != null ? i.sample.anchors.Length : 0) + ",\"live\":" + (i.live != null ? "\"" + i.live.verdict + "/" + i.live.end + "\"" : "null") + "}\n");
                }
            }

            // 2. The once-per-process work, apart: the graph's main-thread preparation (the product's contract) and the pool's start.
            long p0 = Stopwatch.GetTimestamp();
            bool graphPrepared = SpatialSplitGraph.IsPrepared || SpatialSplitGraph.Prepare();
            double prepareGraph = Since(p0);
            long p1 = Stopwatch.GetTimestamp();
            var runs = new List<(Input input, int repeat, int position, Run run)>();
            int failed = 0, liveDiffers = 0, liveUnknown = 0;
            using (var pool = new SpatialSplitReplayPool(SpatialSplitReplayPoolShape.Pc, SpatialSplitReplayPoolLoad.Level, 0, SpatialSplitReplayOptions.DefaultCompetitorWork, SpatialSplitReplayPool.DefaultBlockerWork))
            {
                double poolStart = Since(p1);
                text.Append("platform ").Append(Application.platform).Append(' ').Append(Application.unityVersion).Append("; ").Append(Application.isEditor ? "Editor" : UnityEngine.Debug.isDebugBuild ? "Development Player" : "Player")
#if ENABLE_IL2CPP
                    .Append(", IL2CPP")
#else
                    .Append(", Mono")
#endif
                    .Append("; Burst enabled ").Append(Unity.Burst.BurstCompiler.IsEnabled).Append("; processors ").Append(SystemInfo.processorCount).Append(' ').Append(SystemInfo.processorType)
                    .Append("; pool pc (Background ").Append(pool.Capacity).Append("), load L0 (no competitor); timeline ").Append(CutSpatialSplit.TimelineObserve ? "on" : "off")
                    .Append(", dispatch observation ").Append(Zantetsu.MeshCut.DispatchObservation.Enabled ? "on" : "off").Append(", work log ").Append(Zantetsu.MeshCut.DispatchObservation.WorkLog ? "on" : "off").Append('\n');
                text.Append(ProbeManagedCounter()).Append('\n');
                text.Append("methods: legacy, bone").Append(boneFallback ? ", bone-fallback (NPC-BONE-FALLBACK-1: the bones, then the method as before where they say NotSeparable)" : "").Append('\n');
                text.Append("once per process: graph preparation ").Append(Ms(prepareGraph, inv)).Append(" ms (prepared ").Append(graphPrepared).Append("), pool start ").Append(Ms(poolStart, inv)).Append(" ms\n");

                // 3. The warm-up: every input once in both methods (no line); the first runs' times kept apart.
                long w0 = Stopwatch.GetTimestamp();
                int warmFailed = 0;
                var firstRun = new Dictionary<string, Run>();
                foreach (Input i in inputs)
                {
                    foreach (string method in Methods(i.index, boneFallback))
                    {
                        Run r = RunOne(i, method, pool);
                        r.warm = true;
                        if (!r.ok) warmFailed++;
                        if (!firstRun.ContainsKey(method)) firstRun[method] = r;
                    }
                }

                text.Append("warm-up: ").Append(inputs.Count).Append(" inputs x ").Append(boneFallback ? 3 : 2).Append(" methods in ").Append(Ms(Since(w0), inv)).Append(" ms (failed ").Append(warmFailed).Append("); the first run of each: ");
                foreach (KeyValuePair<string, Run> f in firstRun) text.Append(f.Key).Append(" total ").Append(Ms(Total(f.Value), inv)).Append(" ms (input 0); ");
                text.Append('\n');

                // 4. The measurement: every input `repeats` times; the leader alternates with the input's position and flips with each repeat.
                using (var file = new StreamWriter(Path.Combine(outputDirectory, "runs.jsonl"), false, new UTF8Encoding(false)))
                {
                    for (int repeat = 0; repeat < repeats; repeat++)
                    {
                        foreach (Input i in inputs)
                        {
                            string[] order = Methods(i.index + repeat, boneFallback);
                            for (int position = 0; position < order.Length; position++)
                            {
                                Run r = RunOne(i, order[position], pool);
                                if (!r.ok) failed++;
                                if (r.method == Bone && r.ok)
                                {
                                    if (r.liveMatch == null) liveUnknown++;
                                    else if (r.liveMatch == false) liveDiffers++;
                                }

                                file.Write(Line(i, repeat, position, r, inv));
                                runs.Add((i, repeat, position, r));
                            }
                        }
                    }
                }

                text.Append("measured: ").Append(runs.Count).Append(" runs (").Append(inputs.Count).Append(" inputs x ").Append(repeats).Append(" repeats x ").Append(boneFallback ? 3 : 2).Append(" methods); failed ").Append(failed)
                    .Append("; bone-aware runs agreeing with the live decision: ").Append(runs.FindAll(x => x.run.method == Bone && x.run.liveMatch == true).Count)
                    .Append(", differing ").Append(liveDiffers).Append(", no live line ").Append(liveUnknown).Append('\n');
                text.Append(pool.Describe()).Append('\n');
            }

            // 5. Outside the measurement: the existing replay (SpatialSplitReplay.Run, this thread, the span input, the graph as the issue had it)
            // over every input once, against the first repeat's run of the method as before -- that this comparison's own making of that
            // method's attempt is the replay's.
            int replayAgrees = 0, replayDiffers = 0;
            using (var checks = new StreamWriter(Path.Combine(outputDirectory, "checks.jsonl"), false, new UTF8Encoding(false)))
            {
                foreach (Input i in inputs)
                {
                    var options = new SpatialSplitReplayOptions { spanInput = true, graph = i.bones.graphSearch, graphFirst = i.bones.graphSearch && i.bones.graphFirst, graphLimits = i.bones.graphLimits };
                    SpatialSplitReplayResult rr = SpatialSplitReplay.Run(i.sample, options);
                    Run mine = runs.Find(x => x.input == i && x.repeat == 0 && x.run.method == Legacy).run;
                    bool agrees = mine != null && mine.ok && rr.error == null
                        && rr.graphLed == mine.graphLed && rr.cut == mine.cut && rr.bent == mine.bent && rr.meshLed == mine.meshLed
                        && string.Equals(rr.graph.verdict.ToString(), mine.graphVerdict, StringComparison.Ordinal)
                        && string.Equals(rr.search.outcome.ToString(), mine.searchOutcome, StringComparison.Ordinal)
                        && rr.positiveTriangles == mine.positiveTriangles && rr.negativeTriangles == mine.negativeTriangles
                        && (!rr.graphLed || string.Equals(rr.graphSidesHash, mine.sidesHashEngine, StringComparison.Ordinal));
                    if (agrees) replayAgrees++; else replayDiffers++;
                    checks.Write("{\"index\":" + i.index + ",\"world\":" + i.sample.world + ",\"attempt\":" + i.sample.attempt + ",\"replayAgrees\":" + B(agrees)
                        + ",\"replayError\":" + (rr.error != null ? SpatialSplitCaptureSink.Json(rr.error) : "null") + ",\"replayOutcome\":" + SpatialSplitCaptureSink.Json(rr.outcome ?? "")
                        + ",\"replayGraph\":\"" + rr.graph.verdict + "\",\"replayGraphLed\":" + B(rr.graphLed) + ",\"replayCut\":" + B(rr.cut) + ",\"replayInputCheck\":" + SpatialSplitCaptureSink.Json(rr.inputCheck ?? "")
                        + ",\"mine\":" + SpatialSplitCaptureSink.Json(mine != null ? mine.detail ?? mine.error ?? "" : "no run") + "}\n");
                }
            }

            text.Append("the existing replay over every input once (outside the measurement) against the method as before, repeat 0: agrees ").Append(replayAgrees).Append(", differs ").Append(replayDiffers).Append('\n');

            foreach (Input i in inputs) FreeNative(i);
            File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), text.ToString(), new UTF8Encoding(false));
            exitCode = failed == 0 && liveDiffers == 0 && liveUnknown == 0 && replayDiffers == 0 ? 0 : 3;
            return text.ToString();
        }

        private static string B(bool v) => v ? "true" : "false";
        private static string Ms(double seconds, CultureInfo inv) => double.IsNaN(seconds) ? "null" : (seconds * 1000.0).ToString("F6", inv);
        private static double Since(long begin) => (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;

        private static double Total(Run r)
        {
            if (!r.ok) return double.NaN;
            double t = r.prepare + r.decision + r.distribution;
            if (!double.IsNaN(r.meshes)) t += r.meshes;
            if (!double.IsNaN(r.bake)) t += r.bake;
            return t;
        }

        private static string ReadSessionId(string sessionDirectory)
        {
            string path = Path.Combine(sessionDirectory, "session.json");
            SpatialSplitSessionIdLine line = JsonUtility.FromJson<SpatialSplitSessionIdLine>(File.ReadAllText(path));
            return line?.sessionId ?? "";
        }

        // The bones' tables as the lineage holds them (native, read only by the decision): shared by the runs of one input, as the
        // product's lineage is shared by its fragments' attempts.
        private static void MakeNative(Input i)
        {
            SpatialSplitBoneSample b = i.bones;
            if (b.baseBones != null)
            {
                i.baseBones = new NativeArray<ulong>(Math.Max(1, b.baseBones.Length), Allocator.Persistent);
                for (int k = 0; k < b.baseBones.Length; k++) i.baseBones[k] = b.baseBones[k];
            }

            int chunks = b.chunkStarts.Length;
            i.origins = new NativeArray<uint2>[chunks];
            i.chunks = new NpcVertexChunk[chunks];
            for (int c = 0; c < chunks; c++)
            {
                uint2[] o = b.chunkOrigins[c];
                i.origins[c] = new NativeArray<uint2>(Math.Max(1, o.Length), Allocator.Persistent);
                for (int k = 0; k < o.Length; k++) i.origins[c][k] = o[k];
                i.chunks[c] = new NpcVertexChunk { origins = (uint2*)i.origins[c].GetUnsafeReadOnlyPtr(), start = b.chunkStarts[c], count = o.Length, localBase = b.chunkLocalBases[c] };
            }
        }

        private static void FreeNative(Input i)
        {
            if (i.baseBones.IsCreated) i.baseBones.Dispose();
            if (i.origins != null) foreach (NativeArray<uint2> o in i.origins) if (o.IsCreated) o.Dispose();
            i.origins = null;
        }

        // One run of one method from its own copy of the input: the vertices, the indices and the shape made here (not timed), the issue's
        // part timed, the numbers on the pool's worker, then -- for a placement that cuts convexes -- the meshes and their bake.
        private static Run RunOne(Input i, string method, SpatialSplitReplayPool pool)
        {
            var r = new Run { method = method };
            SpatialSplitSample sample = i.sample;
            int vertexBytes = UnsafeUtility.SizeOf<VpRenderVertex>();
            if (sample.vertexStructBytes != vertexBytes) { r.error = "the sample's vertex is " + sample.vertexStructBytes + " bytes, this build's " + vertexBytes; return r; }
            int named = sample.vertexIds.Length;
            int n = sample.indices.Length;
            int s = sample.submeshOffsets.Length;
            int c = sample.convexes.Length;
            uint greatest = 0;
            foreach (uint id in sample.vertexIds) greatest = Math.Max(greatest, id);
            foreach (uint id in sample.indices) greatest = Math.Max(greatest, id);
            long viewLength = (long)greatest + 1;
            var vertices = new NativeArray<VpRenderVertex>((int)Math.Max(1, viewLength), Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var indices = new NativeArray<uint>(Math.Max(1, n), Allocator.Persistent);
            PhysicsOwnerShape shape = null;
            Mesh mesh = null;
            CutSpatialSplit.Attempt attempt = null;
            bool hold = false;
            try
            {
                // The span input (the product's shape): the view as long as the greatest id + 1, every vertex at its original id.
                byte* raw = (byte*)vertices.GetUnsafePtr();
                int at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
                for (long k = 0; k < viewLength; k++) *(float3*)(raw + (k * vertexBytes) + at0) = new float3(float.NaN);
                fixed (byte* from = sample.vertexBytes)
                {
                    for (int k = 0; k < named; k++) UnsafeUtility.MemCpy(raw + ((long)sample.vertexIds[k] * vertexBytes), from + ((long)k * vertexBytes), vertexBytes);
                }

                for (int k = 0; k < n; k++) indices[k] = sample.indices[k];
                shape = SpatialSplitReplay.BuildShapeForComparison(sample, out mesh);

                // ---- the issue's own part (timed)
                long managedBefore = ManagedBytes();
                long t0 = Stopwatch.GetTimestamp();
                attempt = new CutSpatialSplit.Attempt
                {
                    serial = sample.attempt,
                    input = new CutSpatialSplit.Input(null, shape, default, sample.anchors != null ? sample.anchors.Length : 0, sample.epoch),
                    parentMass = sample.parentMass,
                    vertexLimit = sample.vertexLimit,
                    supportEpsilon = sample.supportEpsilon,
                    anchorEpsilon = sample.anchorEpsilon,
                    margin = sample.margin,
                    limits = sample.limits,
                    cutConvexes = sample.cutConvexes,
                    minCutSideTriangles = sample.minCutSideTriangles,
                    bentSearch = sample.bentSearch,
                    bentLimits = sample.bentLimits,
                    maxConvexVertices = CutSpatialSplit.Attempt.MaxConvexVertices(shape),
                    geometryToOwner = sample.geometryToOwner,
                    shapeToOwner = shape.LocalToOwner,
                    indexCount = n,
                    submeshCount = s,
                    convexCount = c,
                    materials = new int[s],
                    issuedFrame = sample.issuedFrame,
                    vertices = (VpRenderVertex*)vertices.GetUnsafePtr(),
                    vertexViewLength = (int)viewLength,
                    indices = (uint*)indices.GetUnsafePtr(),
                };
                attempt.TakeBlock();
                for (int k = 0; k < s; k++)
                {
                    attempt.submeshOffsets[k] = sample.submeshOffsets[k];
                    attempt.submeshCounts[k] = sample.submeshCounts[k];
                    attempt.materials[k] = sample.materials[k];
                }

                for (int k = 0; k < c; k++)
                {
                    attempt.banks[k] = shape.BankOf(k);
                    attempt.ranges[k] = shape.Convex(k);
                }

                attempt.classificationBlock = PhysicsCutBlocks.Take<byte>(PhysicsCutClassification.RequiredBlockBytes(shape));
                attempt.arena = new PhysicsCutArena(default, c);
                attempt.anchors = sample.anchors != null && sample.anchors.Length > 0 ? (float3[])sample.anchors.Clone() : null;
                SpatialSplitBoneSample b = i.bones;
                if (method == Bone || method == BoneFallback)
                {
                    // TakeBone's part: the convexes' bones copied, the decision's input from the lineage's references, the sides' bytes.
                    var convexBones = new int[c];
                    for (int k = 0; k < c; k++) convexBones[k] = b.convexBones[k];
                    string insufficient = b.insufficient;
                    attempt.boneLed = true;
                    attempt.boneInput = new NpcBoneSplitSearch.Input
                    {
                        convexCount = c,
                        convexBones = convexBones,
                        lineage = new NpcBoneSplitSearch.Lineage
                        {
                            nodeCount = b.nodeCount, parent = b.parent, present = b.present,
                            baseBones = i.baseBones.IsCreated ? (ulong*)i.baseBones.GetUnsafeReadOnlyPtr() : null,
                            baseStart = b.baseStart, baseCount = b.baseCount, chunks = i.chunks, localCount = b.localCount,
                        },
                        jointKept = b.jointKept,
                        segmentLo = b.segmentLo,
                        segmentHi = b.segmentHi,
                        boneSides = new byte[2 * b.nodeCount],
                        insufficient = insufficient,
                        maxTriangles = attempt.limits.maxTriangles,
                        maxOriginSteps = b.maxOriginSteps,
                    };
                    attempt.graphSides = PhysicsCutBlocks.Take<byte>(c + (n / 3));
                    if (method == BoneFallback)
                    {
                        // NPC-BONE-FALLBACK-1, the issue's part: the graph's switch as the product prepares it for such an owner (the
                        // issue's settings, from the sidecar), the bones' sides its sides.
                        attempt.boneFallback = true;
                        if (b.graphSearch && attempt.anchors == null && c >= 2)
                        {
                            attempt.graphSearch = true;
                            attempt.graphLimits = b.graphLimits;
                            attempt.graphFirst = b.graphFirst;
                        }
                    }
                }
                else if (b.graphSearch && attempt.anchors == null && c >= 2)
                {
                    // The graph stage as the product issued it for such an owner (the issue's settings, from the sidecar).
                    attempt.graphSearch = true;
                    attempt.graphLimits = b.graphLimits;
                    attempt.graphFirst = b.graphFirst;
                    attempt.graphSides = PhysicsCutBlocks.Take<byte>(c + (n / 3));
                }

                r.prepare = Since(t0);
                long managedAfter = ManagedBytes();
                r.prepareManagedBytes = managedBefore >= 0 && managedAfter >= 0 ? managedAfter - managedBefore : -1;
                r.blockBytes = attempt.block.Length;
                r.classificationBytes = attempt.classificationBlock.Length;
                r.sidesBytes = attempt.graphSides.IsCreated ? attempt.graphSides.Length : 0;

                // ---- the numbers on the worker
                var poolResult = new SpatialSplitReplayResult { world = sample.world, attempt = sample.attempt };
                hold = true;
                pool.RunAttempt(attempt, poolResult, method);
                hold = false;
                if (attempt.failure != null)
                {
                    r.error = "the numbers threw: " + attempt.failure.GetType().Name + ": " + attempt.failure.Message;
                    return r;
                }

                if (attempt.beganAt == 0 || attempt.endedAt == 0) { r.error = "the worker's clock was not read (a build that does not observe)"; return r; }
                r.worker = (attempt.endedAt - attempt.beganAt) / (double)Stopwatch.Frequency;
                long decided = attempt.decidedAt != 0 ? attempt.decidedAt : attempt.endedAt;
                r.decision = (decided - attempt.beganAt) / (double)Stopwatch.Frequency;
                r.distribution = (attempt.endedAt - decided) / (double)Stopwatch.Frequency;

                // ---- a placement that cuts convexes: its meshes (Main) and their bake (the worker), as the stages before its publication
                r.end = EndOf(attempt, method);
                if (attempt.cut && r.end == "Published")
                {
                    long m0 = Stopwatch.GetTimestamp();
                    attempt.cooking = PhysicsCutCook.DefaultCooking;
                    attempt.MakeMeshes();
                    r.meshes = Since(m0);
                    if (attempt.meshesBad)
                    {
                        r.end = "NotBuildable";
                    }
                    else
                    {
                        attempt.applied = true;
                        attempt.Reset(CutSpatialSplit.AttemptStage.Baking);
                        hold = true;
                        pool.RunAttempt(attempt, new SpatialSplitReplayResult { world = sample.world, attempt = sample.attempt }, method + "-bake");
                        hold = false;
                        if (attempt.failure != null) { r.error = "the bake threw: " + attempt.failure.GetType().Name + ": " + attempt.failure.Message; return r; }
                        r.bake = attempt.bakeSeconds;
                    }
                }

                // ---- what it came to (not timed)
                r.ok = true;
                r.cut = attempt.cut;
                r.graphLed = attempt.graphLed;
                r.bent = CutSpatialSplit.IsBentResult(in attempt.search);
                r.meshLed = attempt.search.meshLed;
                r.searchOutcome = attempt.search.outcome.ToString();
                r.graphVerdict = attempt.graph.verdict.ToString();
                r.graphBurstRan = attempt.graph.burstRan;
                r.kernelManaged = attempt.classified && attempt.kernel.executedManaged != 0;
                r.fellBack = attempt.boneFellBack;
                bool boneRoute = method == Bone || (method == BoneFallback && !attempt.boneFellBack);
                if (method == BoneFallback && attempt.boneFellBack)
                {
                    // the bones' record of a fallback, beside the method as before's numbers (NPC-BONE-FALLBACK-1)
                    NpcBoneSplitRecord fell = attempt.bone;
                    r.boneVerdict = fell.verdict.ToString();
                    r.boneReason = fell.reason;
                    r.clusters = fell.clusters;
                    r.groups = fell.groups;
                    r.fullGroups = fell.fullGroups;
                    r.meshComponents = fell.meshComponents;
                    r.pieceBones = fell.pieceBones;
                    r.removedWeights = fell.removedWeights;
                    r.unboundGroups = fell.unboundGroups;
                }

                if (boneRoute)
                {
                    NpcBoneSplitRecord rec = attempt.bone;
                    r.boneVerdict = rec.verdict.ToString();
                    r.boneReason = rec.reason;
                    r.workerNativeBytes = rec.nativeBytes;
                    r.clusters = rec.clusters;
                    r.groups = rec.groups;
                    r.fullGroups = rec.fullGroups;
                    r.meshComponents = rec.meshComponents;
                    r.pieceBones = rec.pieceBones;
                    r.removedWeights = rec.removedWeights;
                    r.unboundGroups = rec.unboundGroups;
                    r.positiveTriangles = rec.positiveTriangles;
                    r.negativeTriangles = rec.negativeTriangles;
                    r.positiveConvexes = rec.positiveConvexes;
                    r.negativeConvexes = rec.negativeConvexes;
                    if (rec.verdict == NpcBoneSplitVerdict.Separable && attempt.graphSides.IsCreated)
                    {
                        r.sidesHash = SpatialSplitReplay.SidesHash((byte*)attempt.graphSides.GetUnsafeReadOnlyPtr() + c, n / 3);
                    }

                    if (method == Bone && i.live != null)
                    {
                        r.liveMatch = string.Equals(i.live.verdict, r.boneVerdict, StringComparison.Ordinal)
                            && string.Equals(i.live.sidesHash ?? "", r.sidesHash ?? "", StringComparison.Ordinal)
                            && i.live.clusters == rec.clusters && i.live.groups == rec.groups && i.live.fullGroups == rec.fullGroups && i.live.meshComponents == rec.meshComponents
                            && i.live.positiveConvexes == rec.positiveConvexes && i.live.negativeConvexes == rec.negativeConvexes
                            && i.live.positiveTriangles == rec.positiveTriangles && i.live.negativeTriangles == rec.negativeTriangles;
                    }
                }
                else
                {
                    r.workerNativeBytes = attempt.graph.Entered ? attempt.graph.nativeBytes : -1;
                    r.positiveTriangles = attempt.graphLed ? attempt.graph.positiveTriangles : attempt.search.positiveTriangles;
                    r.negativeTriangles = attempt.graphLed ? attempt.graph.negativeTriangles : attempt.search.negativeTriangles;
                    r.positiveConvexes = attempt.graphLed ? attempt.graph.positiveConvexes : -1;
                    r.negativeConvexes = attempt.graphLed ? attempt.graph.negativeConvexes : -1;
                    if (attempt.graphLed && attempt.graphSides.IsCreated)
                    {
                        byte* sides = (byte*)attempt.graphSides.GetUnsafeReadOnlyPtr();
                        r.sidesHash = SpatialSplitReplay.SidesHash(sides + c, n / 3);
                        // as the replay hashes a graph placement (the check after the measurement): the convexes' bytes back to the engine's form
                        var normal = new byte[Math.Max(1, c + (n / 3))];
                        for (int k = 0; k < c; k++) normal[k] = sides[k] == (byte)ConvexSide.Positive ? (byte)1 : (byte)0;
                        for (int k = c; k < c + (n / 3); k++) normal[k] = sides[k] != 0 ? (byte)1 : (byte)0;
                        fixed (byte* np = normal) r.sidesHashEngine = SpatialSplitReplay.SidesHash(np, c + (n / 3));
                    }
                }

                if (attempt.classified && attempt.classification != null)
                {
                    int pos = 0, neg = 0;
                    IReadOnlyList<ConvexSide> sides = attempt.classification.Sides;
                    for (int k = 0; k < sides.Count; k++) { if (sides[k] == ConvexSide.Positive) pos++; else if (sides[k] == ConvexSide.Negative) neg++; }
                    if (!boneRoute && !attempt.graphLed) { r.positiveConvexes = pos; r.negativeConvexes = neg; }
                }

                r.detail = boneRoute ? r.boneVerdict + ": " + r.boneReason
                    : attempt.graphLed ? "graph " + r.graphVerdict
                    : r.bent ? "bent" : r.meshLed ? (attempt.cut ? "mesh-led, convexes cut" : "mesh-led") : "plane " + r.searchOutcome + (attempt.graph.Entered ? " (graph " + r.graphVerdict + ")" : "");
                if (r.fellBack) r.detail = "fallback after the bones' " + r.boneVerdict + ": " + r.detail;
            }
            catch (SpatialSplitReplayPoolStopException)
            {
                throw;   // the pool did not give the work back: the comparison ends (its input is kept, as the replay keeps it)
            }
            catch (Exception e)
            {
                r.ok = false;
                r.error = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (!hold)
                {
                    attempt?.Free(null);
                    shape?.Dispose();
                    if (mesh != null) UnityEngine.Object.Destroy(mesh);
                    vertices.Dispose();
                    indices.Dispose();
                }
            }

            return r;
        }

        // How the product would end the attempt from what came back (ShouldPublish's reading, without the world's re-check of the input).
        private static string EndOf(CutSpatialSplit.Attempt a, string method)
        {
            if (a.cancelled || a.failure != null || !a.ran) return "NotRun";
            if (method == Bone || (method == BoneFallback && !a.boneFellBack))
            {
                if (a.bone.verdict != NpcBoneSplitVerdict.Separable) return a.bone.verdict == NpcBoneSplitVerdict.OverTheLimits ? "OverTheLimits" : "NotFound";
            }
            else if (a.graph.verdict == SpatialSplitGraphVerdict.Invalid)
            {
                return "NotClassified";
            }
            else if (!a.graphLed)
            {
                switch (a.search.outcome)
                {
                    case SpatialSplitSearchOutcome.Found: break;
                    case SpatialSplitSearchOutcome.NotFound: return a.graph.unfinished ? "Unfinished" : "NotFound";
                    case SpatialSplitSearchOutcome.OverTheLimits: return "OverTheLimits";
                    default: return "NotClassified";
                }
            }

            if (!a.distributed) return "NotFound";
            if (!a.classified) return "NotClassified";
            if (!a.anchorsDistributed || a.kernel.status != ConvexCutOwnerStatus.Ok || a.meshesBad) return "NotBuildable";
            return "Published";
        }

        // NPC-BONE-EVAL-1 review 1: the counter is read only where a probe showed it counts (a runtime may return a constant); else -1.
        private static bool s_managedCounter;

        private static long ManagedBytes() => s_managedCounter ? ReadManagedCounter() : -1;

        private static long ReadManagedCounter()
        {
            try { return GC.GetAllocatedBytesForCurrentThread(); }
            catch (Exception) { return -1; }
        }

        /// <summary>Whether the managed allocation counter counts here: a known allocation read through it (outside every measurement).</summary>
        private static string ProbeManagedCounter()
        {
            const int Probe = 8192;
            long before = ReadManagedCounter();
            var probe = new byte[Probe];
            long after = ReadManagedCounter();
            GC.KeepAlive(probe);
            s_managedCounter = before >= 0 && after >= 0 && after - before >= Probe;
            return "managed allocation counter (GC.GetAllocatedBytesForCurrentThread): a probe of " + Probe + " bytes read as " + (before >= 0 && after >= 0 ? (after - before).ToString(CultureInfo.InvariantCulture) : "no value")
                + (s_managedCounter ? " -> taken" : " -> NOT AVAILABLE here: prepareManagedBytes is -1 (not taken), never 0");
        }

        private static string Line(Input i, int repeat, int position, Run r, CultureInfo inv)
        {
            var t = new StringBuilder(1024);
            t.Append("{\"index\":").Append(i.index).Append(",\"world\":").Append(i.sample.world).Append(",\"attempt\":").Append(i.sample.attempt)
                .Append(",\"category\":").Append(SpatialSplitCaptureSink.Json(i.category)).Append(",\"repeat\":").Append(repeat).Append(",\"position\":").Append(position)
                .Append(",\"method\":\"").Append(r.method).Append("\",\"ok\":").Append(B(r.ok)).Append(",\"error\":").Append(r.error != null ? SpatialSplitCaptureSink.Json(r.error) : "null")
                .Append(",\"end\":").Append(r.end != null ? "\"" + r.end + "\"" : "null").Append(",\"detail\":").Append(r.detail != null ? SpatialSplitCaptureSink.Json(r.detail) : "null")
                .Append(",\"prepareMs\":").Append(Ms(r.prepare, inv)).Append(",\"decisionMs\":").Append(Ms(r.decision, inv)).Append(",\"distributionMs\":").Append(Ms(r.distribution, inv))
                .Append(",\"meshesMs\":").Append(Ms(r.meshes, inv)).Append(",\"bakeMs\":").Append(Ms(r.bake, inv)).Append(",\"workerMs\":").Append(Ms(r.worker, inv)).Append(",\"totalMs\":").Append(Ms(Total(r), inv))
                .Append(",\"cut\":").Append(B(r.cut)).Append(",\"graphLed\":").Append(B(r.graphLed)).Append(",\"bent\":").Append(B(r.bent)).Append(",\"meshLed\":").Append(B(r.meshLed))
                .Append(",\"searchOutcome\":").Append(r.searchOutcome != null ? "\"" + r.searchOutcome + "\"" : "null").Append(",\"graphVerdict\":").Append(r.graphVerdict != null ? "\"" + r.graphVerdict + "\"" : "null")
                .Append(",\"boneVerdict\":").Append(r.boneVerdict != null ? "\"" + r.boneVerdict + "\"" : "null").Append(",\"boneReason\":").Append(r.boneReason != null ? SpatialSplitCaptureSink.Json(r.boneReason) : "null")
                .Append(",\"positiveTriangles\":").Append(r.positiveTriangles).Append(",\"negativeTriangles\":").Append(r.negativeTriangles)
                .Append(",\"positiveConvexes\":").Append(r.positiveConvexes).Append(",\"negativeConvexes\":").Append(r.negativeConvexes)
                .Append(",\"clusters\":").Append(r.clusters).Append(",\"groups\":").Append(r.groups).Append(",\"fullGroups\":").Append(r.fullGroups).Append(",\"meshComponents\":").Append(r.meshComponents)
                .Append(",\"pieceBones\":").Append(r.pieceBones).Append(",\"removedWeights\":").Append(r.removedWeights).Append(",\"unboundGroups\":").Append(r.unboundGroups)
                .Append(",\"sidesHash\":").Append(r.sidesHash != null ? "\"" + r.sidesHash + "\"" : "null").Append(",\"liveMatch\":").Append(r.liveMatch == null ? "null" : B(r.liveMatch.Value)).Append(",\"fellBack\":").Append(B(r.fellBack))
                .Append(",\"blockBytes\":").Append(r.blockBytes).Append(",\"classificationBytes\":").Append(r.classificationBytes).Append(",\"sidesBytes\":").Append(r.sidesBytes)
                .Append(",\"workerNativeBytes\":").Append(r.workerNativeBytes).Append(",\"prepareManagedBytes\":").Append(r.prepareManagedBytes)
                .Append(",\"graphBurstRan\":").Append(B(r.graphBurstRan)).Append(",\"kernelManaged\":").Append(B(r.kernelManaged)).Append("}\n");
            return t.ToString();
        }
    }
}
#endif
