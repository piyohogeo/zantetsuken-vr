#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// One sample file of the capture, read back (SpatialSplitCaptureSink writes it): every value as its bits were, the
    /// indices as issued (the issuing storage's vertex ids), the vertices they name with those ids ascending, and each
    /// convex's range and arrays in the shape's order. The SHA-256 is of the content before compression.
    /// </summary>
    internal sealed class SpatialSplitSample
    {
        internal sealed class Convex
        {
            internal ConvexBrepRange range;
            internal float3[] vertices;
            internal int[] faceOffsets, faceIndices, faceEdges;
            internal BrepEdge[] edges;
        }

        internal int formatVersion, vertexStructBytes;
        internal string codeVersion, sessionId, objectName, sha256;
        internal long world, attempt, rawBytes;
        internal int issuedFrame, fragment, rootFragment, splitSource, generation, epoch;
        internal float margin, supportEpsilon, anchorEpsilon;
        internal int vertexLimit, minCutSideTriangles, maxConvexVertices, vertexViewLength;
        internal double parentMass;
        internal SpatialSplitSearchLimits limits;
        internal bool cutConvexes, bentSearch;
        internal SpatialSplitBentLimits bentLimits;
        internal float4x4 geometryToOwner, shapeToOwner;
        internal int[] submeshOffsets, submeshCounts, materials;
        internal float3[] anchors;
        internal uint[] indices, vertexIds;
        internal byte[] vertexBytes;
        internal Convex[] convexes;

        internal static SpatialSplitSample Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var r = new SpatialSplitHashReader(gzip))
            {
                var s = new SpatialSplitSample();
                r.Expect(SpatialSplitCaptureSink.SampleMagic);
                s.formatVersion = r.Int();
                if (s.formatVersion != SpatialSplitCaptureSink.FormatVersion)
                {
                    throw new InvalidDataException("format " + s.formatVersion + ", this reads " + SpatialSplitCaptureSink.FormatVersion);
                }

                s.vertexStructBytes = r.Count("vertex size", 256);
                s.codeVersion = r.String();
                s.sessionId = r.String();
                s.objectName = r.String();
                s.world = r.Long();
                s.attempt = r.Long();
                s.issuedFrame = r.Int();
                s.fragment = r.Int();
                s.rootFragment = r.Int();
                s.splitSource = r.Int();
                s.generation = r.Int();
                s.epoch = r.Int();
                s.margin = r.Float();
                s.supportEpsilon = r.Float();
                s.anchorEpsilon = r.Float();
                s.vertexLimit = r.Int();
                s.parentMass = r.Double();
                s.limits = new SpatialSplitSearchLimits(r.Int(), r.Int(), r.Int(), r.Int(), r.Int(), r.Int());
                s.cutConvexes = r.Bool();
                s.minCutSideTriangles = r.Int();
                s.bentSearch = r.Bool();
                s.bentLimits = new SpatialSplitBentLimits(r.Int(), r.Int(), r.Int(), r.Int(), r.Int());
                s.maxConvexVertices = r.Int();
                s.geometryToOwner = r.Matrix();
                s.shapeToOwner = r.Matrix();
                int submeshes = r.Count("submesh", 1 << 16);
                s.submeshOffsets = new int[submeshes];
                s.submeshCounts = new int[submeshes];
                s.materials = new int[submeshes];
                for (int i = 0; i < submeshes; i++)
                {
                    s.submeshOffsets[i] = r.Int();
                    s.submeshCounts[i] = r.Int();
                    s.materials[i] = r.Int();
                }

                int anchors = r.Int();
                if (anchors < -1 || anchors > (1 << 20)) throw new InvalidDataException("the sample's anchor count " + anchors + " is not one");
                s.anchors = anchors >= 0 ? r.Array<float3>(anchors) : null;
                s.indices = r.Array<uint>(r.Count("index", 1 << 28));
                s.vertexViewLength = r.Int();
                int named = r.Count("vertex", 1 << 28);
                s.vertexIds = r.Array<uint>(named);
                s.vertexBytes = r.Array<byte>(checked(named * s.vertexStructBytes));
                int convexes = r.Count("convex", 1 << 16);
                s.convexes = new Convex[convexes];
                for (int c = 0; c < convexes; c++)
                {
                    var range = new ConvexBrepRange
                    {
                        vertexBase = r.Int(), vertexCount = r.Int(), faceBase = r.Int(), faceCount = r.Int(),
                        faceIndexBase = r.Int(), faceIndexCount = r.Int(), edgeBase = r.Int(), edgeCount = r.Int(), maxFaceLoop = r.Int(),
                    };
                    if (range.vertexCount < 0 || range.faceCount < 0 || range.faceIndexCount < 0 || range.edgeCount < 0
                        || range.vertexCount > (1 << 20) || range.faceCount > (1 << 20) || range.faceIndexCount > (1 << 22) || range.edgeCount > (1 << 22))
                    {
                        throw new InvalidDataException("convex " + c + "'s counts are not ones");
                    }

                    s.convexes[c] = new Convex
                    {
                        range = range,
                        vertices = r.Array<float3>(range.vertexCount),
                        faceOffsets = r.Array<int>(range.faceCount + 1),
                        faceIndices = r.Array<int>(range.faceIndexCount),
                        faceEdges = r.Array<int>(range.faceIndexCount),
                        edges = r.Array<BrepEdge>(range.edgeCount),
                    };
                }

                r.Expect(SpatialSplitCaptureSink.SampleEnd);
                s.sha256 = r.Finish();
                s.rawBytes = r.Length;
                return s;
            }
        }
    }

    /// <summary>One result line of results.jsonl, as JsonUtility reads it (the fields the replay compares and reports).</summary>
    [Serializable]
    public sealed class SpatialSplitResultLine
    {
        public long world, attempt;
        public string file, end, detail, failure, outcome, kernelStatus;
        public int issuedFrame, endFrame, bentPieces, positiveTriangles, negativeTriangles;
        public bool searchObserved, ran, cancelled, meshLed, bent, distributed, classified, cut, applied, meshesBad;
        public int meshes, hullsChecked, bentKnotCount;
        public double workerMs, copyMs, resultCopyMs, bakeMs;
        public long copyBytes;
        public string outputsNotKept;
        public string[] kernelNumbers;
        public string[] sections;
        public string[] planeGeometry, planeShape, bentHeight, bentAlong, bentKnotU, bentKnotH;
    }

    /// <summary>One line of samples.jsonl.</summary>
    [Serializable]
    public sealed class SpatialSplitSampleLine
    {
        public long world, attempt, rawBytes, fileBytes, copyBytes;
        public string file, sha256, @object;
        public int issuedFrame, fragment, rootFragment;
    }

    /// <summary>What one replay of one sample came to.</summary>
    internal sealed class SpatialSplitReplayResult
    {
        internal long world, attempt;
        internal string error, inputCheck, outcome;
        internal bool meshLed, bent, found, distributed, classified, anchorsDistributed, cut;
        // The stages' history marks (GRAPH-CONNECT-8): the bent search was entered (search.bent, kept when the mesh-led plane follows;
        // not the bent RESULT, which is 'bent' above), the mesh-led plane was entered (search.meshLed); the bent setting as saved in the
        // sample and as the attempt ran it; the bent search's own clock (the FindBentSurface call; 0 in a build that does not observe);
        // the convexes the mesh-led plane crossed; the ids for the series (sessionId/world/rootFragment).
        internal bool bentRan, meshLedRan, bentSaved, bentEffective;
        internal bool graphFirstEffective;   // GRAPH-CONNECT-10: the graph-first order applied to this attempt
        // GRAPH-CONNECT-14: what this line ran with (the options, or the pool replay's condition D / C), and the pool record (null on this thread)
        internal bool graphOn;
        internal string orderName = "current", bentName = "saved", condition = "";
        internal SpatialSplitPoolRecord pool;
        internal double bentSeconds;
        internal int crossingConvexes, fragment, rootFragment;
        internal string kernelStatus;
        internal int positiveTriangles, negativeTriangles;
        internal double seconds;
        internal string[] sections;
        internal double[] kernelNumbers;
        internal SpatialSplitSearchResult search;
        internal bool ran, failed;
        internal byte kernelExecutedManaged;

        // The checks of what the replay shared out (none where it found nothing to share out).
        internal bool checkedSharing;
        internal int trianglesMissing, trianglesExtra, triangleMarginShort, triangleWrongSide, convexWrongSide, convexMarginShort;

        // A mesh-led plane's convexes on a side that reach across within the support epsilon (allowed, counted), and the
        // farthest any convex on a side reaches across (0: none), in the shape's frame.
        internal int convexAcrossWithinEpsilon;
        internal float convexMostAcross;
        internal string checkNote;

        // The graph stage (GRAPH-CONNECT-6): whether the attempt entered it, led by it, its record, the sides it gave in the
        // engine's form hashed (Separable only), and the checks of its sharing out (combinatorial: no plane stands for it).
        internal bool graphEntered, graphLed;
        internal SpatialSplitGraphRecord graph;
        internal string graphSidesHash = "";
        internal int graphTrianglesMissing, graphTrianglesExtra, graphTriangleWrongSide, graphTriangleOrderBroken, graphConvexWrongSide, graphConvexNotSided;
        internal bool GraphChecksPass => graphTrianglesMissing == 0 && graphTrianglesExtra == 0 && graphTriangleWrongSide == 0 && graphTriangleOrderBroken == 0 && graphConvexWrongSide == 0 && graphConvexNotSided == 0;

        /// <summary>A Separable graph result is complete when the sharing out, the anchors, the classification and the kernel all succeeded and the sharing was checked; a Separable short of that is incomplete (counted apart, never a success).</summary>
        internal bool GraphComplete => graphLed && distributed && anchorsDistributed && classified && kernelStatus == "Ok" && checkedSharing;
        internal bool GraphIncomplete => graphEntered && graph.verdict == SpatialSplitGraphVerdict.Separable && !GraphComplete;

        internal bool ChecksPass => trianglesMissing == 0 && trianglesExtra == 0 && triangleMarginShort == 0 && triangleWrongSide == 0
            && convexWrongSide == 0 && convexMarginShort == 0;
    }

    /// <summary>
    /// The offline replay of the capture (DESIGN 7.9.8): a sample rebuilt as the product's own attempt -- the vertices
    /// the indices name (renumbered from 0 in their ids' order, which keeps every comparison of two ids), the indices in
    /// their order, the shape made by <see cref="PhysicsOwnerShape.Authored"/> from the convexes in theirs -- and the
    /// attempt's own numbers run on it (<see cref="CutSpatialSplit.Attempt.Begin"/>: the plane, the bent and the
    /// mesh-led searches, the sharing out, the anchors, the classification, the kernel). Nothing of them is written
    /// again here. What it came to is put in the capture's canonical bytes with the indices as the issuing storage's ids,
    /// and compared section by section with what the live attempt came to; then checked for triangles lost or doubled,
    /// a vertex short of the margin and a convex on the wrong side. Numbers only: nothing is cooked.
    /// </summary>
    /// <summary>
    /// How a session is replayed (GRAPH-CONNECT-6 §2): the input's shape -- compact (the saved referenced vertices renumbered 0..V-1:
    /// the live reproduction's contract) or span (the vertices at their original ids in a view as long as the greatest id + 1, the
    /// unreferenced slots NaN: the product's shape, with its compaction cost) -- whether the graph stage runs (span only), its
    /// limits, and a list of the samples to replay (the fixed tuning list: the reference decider's lines; every listed sample must
    /// be present once with its SHA-256, and nothing else is replayed).
    /// </summary>
    public sealed class SpatialSplitReplayOptions
    {
        public bool spanInput;
        public bool graph;
        public SpatialSplitGraphLimits graphLimits = SpatialSplitGraphLimits.Default;
        public string listPath;
        /// <summary>The bent partition left out of every attempt (GRAPH-CONNECT-8, the comparison's B condition): the sample's saved
        /// bentSearch is not followed. Only with the graph on the span input. Default false: the saved setting, as before.</summary>
        public bool bentOff;
        /// <summary>The configuration C order (GRAPH-CONNECT-10): the graph stage before the bent partition where it applies, the bent
        /// partition on an unfinished graph. Only with the graph on the span input, and not together with bentOff. Default false: the order as before.</summary>
        public bool graphFirst;

        /// <summary>
        /// GRAPH-CONNECT-14 §3-1: the pool replay -- every sample run in BOTH conditions, D (graph off, the saved bent setting, the order as
        /// before) and C (graph on, graph-first), through the product's dispatcher and a real Background pool, the first condition
        /// alternating by the sample's position (even: D first). Needs the span input; the graph, order and bent arguments are
        /// refused with it (the conditions are the mode's own). The competitor level is L0, L1 or L2 (§3-1-1).
        /// </summary>
        public bool pool;
        public int competitorLevel;
        public long competitorWork = DefaultCompetitorWork;

        /// <summary>The competitor's fixed amount of steps (about 2 ms on the development machine of 2026-10; the amount is what is fixed, not the time).</summary>
        public const long DefaultCompetitorWork = 2000000;

        public static SpatialSplitReplayOptions Default => new SpatialSplitReplayOptions();
        public string PoolName => pool ? "L" + competitorLevel.ToString(System.Globalization.CultureInfo.InvariantCulture) : "off";

        public string InputName => spanInput ? "span" : "compact";
        public string BentName => bentOff ? "off" : "saved";
        public string OrderName => graphFirst ? "graph-first" : "current";

        /// <summary>
        /// Reads the options from launch arguments named by the caller (the Player's and the Editor batch's names differ): the input
        /// argument's value compact or span, the graph flag's presence, the list argument's absolute file. False with
        /// <paramref name="refusal"/> when a value is missing or unknown, the list is not absolute, or the graph is asked for without the span input.
        /// </summary>
        public static bool TryRead(string[] arguments, string inputArgument, string graphArgument, string listArgument, out SpatialSplitReplayOptions options, out string refusal) =>
            TryRead(arguments, inputArgument, graphArgument, listArgument, null, out options, out refusal);

        /// <summary>As above, with the bent argument (saved | off): off is refused without the graph on the span input; a missing or unknown value is refused.</summary>
        public static bool TryRead(string[] arguments, string inputArgument, string graphArgument, string listArgument, string bentArgument, out SpatialSplitReplayOptions options, out string refusal) =>
            TryRead(arguments, inputArgument, graphArgument, listArgument, bentArgument, null, out options, out refusal);

        /// <summary>As above, with the order argument (current | graph-first): graph-first is refused without the graph on the span input, and together with bent off.</summary>
        public static bool TryRead(string[] arguments, string inputArgument, string graphArgument, string listArgument, string bentArgument, string orderArgument, out SpatialSplitReplayOptions options, out string refusal) =>
            TryRead(arguments, inputArgument, graphArgument, listArgument, bentArgument, orderArgument, null, null, out options, out refusal);

        /// <summary>
        /// As above, with the pool replay's arguments (GRAPH-CONNECT-14): the pool argument takes L0, L1 or L2 and needs the span input,
        /// and is refused together with the graph, order or bent arguments; the competitor work argument takes a positive count.
        /// </summary>
        public static bool TryRead(string[] arguments, string inputArgument, string graphArgument, string listArgument, string bentArgument, string orderArgument,
            string poolArgument, string competitorWorkArgument, out SpatialSplitReplayOptions options, out string refusal)
        {
            options = new SpatialSplitReplayOptions();
            refusal = null;
            arguments = arguments ?? Array.Empty<string>();
            string ValueAt(int at) => at >= 0 && at + 1 < arguments.Length && !arguments[at + 1].StartsWith("-", StringComparison.Ordinal) ? arguments[at + 1] : null;
            int inputAt = Array.FindIndex(arguments, a => string.Equals(a, inputArgument, StringComparison.OrdinalIgnoreCase));
            if (inputAt >= 0)
            {
                string value = ValueAt(inputAt);
                if (value == null) { refusal = inputArgument + " has no value"; return false; }
                if (string.Equals(value, "span", StringComparison.OrdinalIgnoreCase)) options.spanInput = true;
                else if (!string.Equals(value, "compact", StringComparison.OrdinalIgnoreCase)) { refusal = inputArgument + " takes compact or span, not '" + value + "'"; return false; }
            }

            options.graph = Array.FindIndex(arguments, a => string.Equals(a, graphArgument, StringComparison.OrdinalIgnoreCase)) >= 0;
            if (options.graph && !options.spanInput) { refusal = graphArgument + " needs " + inputArgument + " span (the product's compaction cost is the span's)"; return false; }
            int listAt = Array.FindIndex(arguments, a => string.Equals(a, listArgument, StringComparison.OrdinalIgnoreCase));
            if (listAt >= 0)
            {
                string value = ValueAt(listAt);
                if (value == null) { refusal = listArgument + " has no value"; return false; }
                if (!Path.IsPathRooted(value)) { refusal = listArgument + " takes an absolute file"; return false; }
                options.listPath = value;
            }

            int bentAt = bentArgument != null ? Array.FindIndex(arguments, a => string.Equals(a, bentArgument, StringComparison.OrdinalIgnoreCase)) : -1;
            if (bentAt >= 0)
            {
                string value = ValueAt(bentAt);
                if (value == null) { refusal = bentArgument + " has no value"; return false; }
                if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)) options.bentOff = true;
                else if (!string.Equals(value, "saved", StringComparison.OrdinalIgnoreCase)) { refusal = bentArgument + " takes saved or off, not '" + value + "'"; return false; }
                if (options.bentOff && !(options.graph && options.spanInput)) { refusal = bentArgument + " off needs " + graphArgument + " and " + inputArgument + " span (the comparison's B condition: plane, graph, mesh-led)"; return false; }
            }

            int orderAt = orderArgument != null ? Array.FindIndex(arguments, a => string.Equals(a, orderArgument, StringComparison.OrdinalIgnoreCase)) : -1;
            if (orderAt >= 0)
            {
                string value = ValueAt(orderAt);
                if (value == null) { refusal = orderArgument + " has no value"; return false; }
                if (string.Equals(value, "graph-first", StringComparison.OrdinalIgnoreCase)) options.graphFirst = true;
                else if (!string.Equals(value, "current", StringComparison.OrdinalIgnoreCase)) { refusal = orderArgument + " takes current or graph-first, not '" + value + "'"; return false; }
                if (options.graphFirst && !(options.graph && options.spanInput)) { refusal = orderArgument + " graph-first needs " + graphArgument + " and " + inputArgument + " span (the comparison's C condition)"; return false; }
                if (options.graphFirst && options.bentOff) { refusal = orderArgument + " graph-first and " + bentArgument + " off are different conditions (C and B): not together"; return false; }
            }
            int poolAt = poolArgument != null ? Array.FindIndex(arguments, a => string.Equals(a, poolArgument, StringComparison.OrdinalIgnoreCase)) : -1;
            if (poolAt >= 0)
            {
                string value = ValueAt(poolAt);
                if (value == null) { refusal = poolArgument + " has no value (L0, L1 or L2)"; return false; }
                if (value.Length != 2 || (value[0] != 'L' && value[0] != 'l') || value[1] < '0' || value[1] > '2') { refusal = poolArgument + " takes L0, L1 or L2, not '" + value + "'"; return false; }
                options.pool = true;
                options.competitorLevel = value[1] - '0';
                if (!options.spanInput) { refusal = poolArgument + " needs " + inputArgument + " span (the product's shape)"; return false; }
                if (options.graph || orderAt >= 0 || bentAt >= 0) { refusal = poolArgument + " runs its own conditions D and C: not together with " + graphArgument + ", " + (orderArgument ?? "the order argument") + " or " + (bentArgument ?? "the bent argument"); return false; }
            }
            int workAt = competitorWorkArgument != null ? Array.FindIndex(arguments, a => string.Equals(a, competitorWorkArgument, StringComparison.OrdinalIgnoreCase)) : -1;
            if (workAt >= 0)
            {
                string value = ValueAt(workAt);
                if (value == null) { refusal = competitorWorkArgument + " has no value"; return false; }
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long steps) || steps <= 0) { refusal = competitorWorkArgument + " takes a positive count of steps, not '" + value + "'"; return false; }
                if (!options.pool) { refusal = competitorWorkArgument + " needs " + poolArgument; return false; }
                options.competitorWork = steps;
            }

            return true;
        }
    }

    [Serializable]
    internal sealed class SpatialSplitReplayListLine
    {
        public string session;
        public long world, attempt;
        public string sha256;
    }

    [Serializable]
    internal sealed class SpatialSplitSessionIdLine
    {
        public string sessionId;
    }

    public static unsafe class SpatialSplitReplay
    {
        /// <summary>The kernel's numbers count as the same within this relative difference, where their bits differ.</summary>
        public const double KernelTolerance = 1e-9;

        /// <summary>
        /// Replays every sample samples.jsonl lists in <paramref name="sessionDirectory"/>, into a new
        /// <paramref name="outputDirectory"/> (replay.jsonl, summary.txt); the summary. Main thread (meshes are made).
        /// </summary>
        public static string RunSession(string sessionDirectory, string outputDirectory, out bool allSame) =>
            RunSession(sessionDirectory, outputDirectory, out allSame, out _);

        /// <summary>As above; <paramref name="noSample"/>: the folder holds no sample at all (its own reason, never "the same").</summary>
        public static string RunSession(string sessionDirectory, string outputDirectory, out bool allSame, out bool noSample) =>
            RunSession(sessionDirectory, outputDirectory, null, out allSame, out noSample);

        /// <summary>As above, with the replay's options (null: compact input, no graph, every sample).</summary>
        public static string RunSession(string sessionDirectory, string outputDirectory, SpatialSplitReplayOptions options, out bool allSame, out bool noSample)
        {
            options = options ?? SpatialSplitReplayOptions.Default;
            if (options.graph && !options.spanInput)
            {
                throw new ArgumentException("the graph stage replays on the span input only (the compact input has not the product's compaction cost)");
            }

            if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            {
                throw new IOException("the replay's output exists already: " + outputDirectory);
            }

            // The index and the results, line by line: a line cut short or not read (a process that ended while writing it)
            // is counted and passed over, and every line before and after it is still read.
            List<SpatialSplitSampleLine> samples = Lines<SpatialSplitSampleLine>(Path.Combine(sessionDirectory, "samples.jsonl"), out int brokenIndexLines);
            var results = new Dictionary<(long, long), SpatialSplitResultLine>();
            List<SpatialSplitResultLine> resultList = Lines<SpatialSplitResultLine>(Path.Combine(sessionDirectory, "results.jsonl"), out int brokenResultLines);
            foreach (SpatialSplitResultLine r in resultList) results[(r.world, r.attempt)] = r;
            int resultLines = resultList.Count;

            // A committed sample the index does not name (its line was not written, or was cut short): found by the folder,
            // read whole and replayed; its hash cannot be checked against the index.
            // The tuning list (GRAPH-CONNECT-6 §2): the samples to replay are those the list names for this session, each present
            // once with the list's SHA-256; a listed sample missing, listed twice, or with another hash is the list check's failure,
            // and the files the list does not name are left out (counted). Applied before any sample is read.
            string sessionId = ReadSessionId(sessionDirectory);
            int listEntries = 0, listMissing = 0, listDuplicates = 0, listHashDiffers = 0, listLeftOut = 0;
            bool listOk = true;
            var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // the index's files, listed or not: never "found by the folder"
            foreach (SpatialSplitSampleLine entry in samples) indexed.Add(entry.file);
            if (options.listPath != null)
            {
                var wanted = new Dictionary<(long, long), string>();
                foreach (string line in File.ReadAllLines(options.listPath))
                {
                    string text = line.Trim();
                    if (text.Length == 0 || !text.StartsWith("{", StringComparison.Ordinal)) continue;
                    SpatialSplitReplayListLine l;
                    try { l = JsonUtility.FromJson<SpatialSplitReplayListLine>(text); }
                    catch (ArgumentException) { continue; }
                    if (l == null || !string.Equals(l.session, sessionId, StringComparison.Ordinal)) continue;
                    listEntries++;
                    if (wanted.ContainsKey((l.world, l.attempt))) { listDuplicates++; continue; }
                    wanted[(l.world, l.attempt)] = l.sha256 ?? "";
                }

                var kept = new List<SpatialSplitSampleLine>();
                var seen = new HashSet<(long, long)>();
                foreach (SpatialSplitSampleLine entry in samples)
                {
                    if (!wanted.TryGetValue((entry.world, entry.attempt), out string sha)) { listLeftOut++; continue; }
                    if (!seen.Add((entry.world, entry.attempt))) { listDuplicates++; continue; }
                    if (sha.Length > 0 && !string.Equals(sha, entry.sha256, StringComparison.Ordinal)) { listHashDiffers++; continue; }
                    kept.Add(entry);
                }

                listMissing = wanted.Count - seen.Count;
                samples = kept;
                listOk = listMissing == 0 && listDuplicates == 0 && listHashDiffers == 0 && listEntries > 0;
            }

            int listed = samples.Count;
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SpatialSplitSampleLine entry in samples) named.Add(entry.file);
            int unindexed = 0;
            foreach (string path in Directory.GetFiles(sessionDirectory, "*.zss.gz"))
            {
                string file = Path.GetFileName(path);
                if (named.Contains(file) || indexed.Contains(file) || !TryParseSampleName(file, out long w, out long a)) continue;
                if (options.listPath != null) { listLeftOut++; continue; }   // a file the list does not name is not replayed
                samples.Add(new SpatialSplitSampleLine { file = file, world = w, attempt = a, sha256 = null });
                unindexed++;
            }

            // The graph stage's main-thread preparation (the product's contract), and a warm-up outside every timed sample.
            bool graphPrepared = false;
            if (options.graph || options.pool) graphPrepared = SpatialSplitGraph.IsPrepared || SpatialSplitGraph.Prepare();
            // GRAPH-CONNECT-14 §3-1: the pool replay's dispatcher and real Background pool, for the whole session (disposed at its end)
            using (SpatialSplitReplayPool pool = options.pool ? new SpatialSplitReplayPool(options.competitorLevel, options.competitorWork, TestPoolAttemptTicks, TestPoolStopMilliseconds) : null)
            {
            if (pool != null) pool.TestBeginGate = TestPoolBeginGate;   // tests: a worker holds the attempt's input at the gate (TL P1)
            if (pool != null)
            {
                // the warm-up: the first samples (up to 5) in both conditions, counted apart in the pool's record and written to no line
                pool.Warmup = true;
                for (int i = 0; i < Math.Min(5, samples.Count); i++)
                {
                    try
                    {
                        SpatialSplitSample warm = SpatialSplitSample.Read(Path.Combine(sessionDirectory, samples[i].file));
                        Run(warm, options, pool, "D");
                        Run(warm, options, pool, "C");
                    }
                    catch (SpatialSplitReplayPoolStopException) { throw; }   // a work that did not come back ends the session, warm-up or not (TL P1)
                    catch (Exception) { /* the warm-up's failure is the sample's own, met again below */ }
                }
                pool.Warmup = false;
            }
            else if (options.spanInput && samples.Count > 0)
            {
                try { Run(SpatialSplitSample.Read(Path.Combine(sessionDirectory, samples[0].file)), options); }
                catch (Exception) { /* the warm-up's failure is the sample's own, met again below */ }
            }

            int graphEntered = 0, graphLedCount = 0, graphDistributed = 0, graphClassified = 0, graphKernelOk = 0, graphBurstRan = 0, graphManaged = 0, graphBodiesNotRun = 0, graphCheckFailures = 0, graphIncomplete = 0;
            int bentRanCount = 0, bentFoundCount = 0, bentEffectiveCount = 0, graphFirstCount = 0, fallbackRan = 0, fallbackFound = 0;
            var bentTimes = new List<double>();
            var graphVerdicts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var graphTimes = new List<double>();

            string[] partials = Directory.GetFiles(sessionDirectory, "*.partial");
            Directory.CreateDirectory(outputDirectory);
            var outcomes = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var mismatchSections = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var notRecordedBy = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int unreadable = 0, hashDiffers = 0, replayed = 0, same = 0, differ = 0, notRecorded = 0, checkFailures = 0, kernelWithin = 0, errors = 0, inputDiffers = 0;
            var times = new List<double>();
            using (var replayLines = new StreamWriter(Path.Combine(outputDirectory, "replay.jsonl"), false, new UTF8Encoding(false)))
            {
                int samplePosition = 0;   // GRAPH-CONNECT-14: the sample's position decides the first condition of the pool replay
                foreach (SpatialSplitSampleLine entry in samples)
                {
                    var line = new StringBuilder();
                    line.Append("{\"world\":").Append(entry.world).Append(",\"attempt\":").Append(entry.attempt).Append(",\"file\":").Append(SpatialSplitCaptureSink.Json(entry.file));
                    SpatialSplitSample sample;
                    try
                    {
                        sample = SpatialSplitSample.Read(Path.Combine(sessionDirectory, entry.file));
                    }
                    catch (Exception e)
                    {
                        unreadable++;
                        line.Append(",\"verdict\":\"unreadable\",\"error\":").Append(SpatialSplitCaptureSink.Json(e.GetType().Name + ": " + e.Message)).Append('}');
                        replayLines.WriteLine(line.ToString());
                        continue;
                    }

                    if (entry.sha256 != null && !string.Equals(sample.sha256, entry.sha256, StringComparison.Ordinal))
                    {
                        hashDiffers++;
                        line.Append(",\"verdict\":\"hash differs from the index\"}");
                        replayLines.WriteLine(line.ToString());
                        continue;
                    }

                    // GRAPH-CONNECT-14: the pool replay runs the sample in both conditions, D first on even positions, C first on odd; one line each
                    string prefix = line.ToString();
                    int position = samplePosition++;
                    string[] conditions = pool == null ? new string[] { null } : (position % 2 == 0 ? new[] { "D", "C" } : new[] { "C", "D" });
                    foreach (string condition in conditions)
                    {
                    line = new StringBuilder(prefix);
                    SpatialSplitReplayResult replay = Run(sample, options, pool, condition);
                    replayed++;
                    // the bent stage's counts on every replayed line (not only where the graph stage was entered)
                    if (replay.bentEffective) bentEffectiveCount++;
                    if (replay.bentRan) { bentRanCount++; bentTimes.Add(replay.bentSeconds); }
                    if (replay.bent) bentFoundCount++;
                    if (replay.graphFirstEffective)
                    {
                        graphFirstCount++;
                        // the bent fallback of the C order: the graph stage ended Unfinished and the bent partition ran after it
                        if (replay.graphEntered && replay.graph.verdict == SpatialSplitGraphVerdict.Unfinished && replay.bentRan) { fallbackRan++; if (replay.bent) fallbackFound++; }
                    }
                    if (replay.graphEntered)
                    {
                        graphEntered++;
                        Count(graphVerdicts, replay.graph.verdict + (replay.graph.verdict == SpatialSplitGraphVerdict.Unfinished ? " (" + replay.graph.stop + ")" : ""));
                        if (replay.graphLed) graphLedCount++;
                        if (replay.graphLed && replay.distributed) graphDistributed++;
                        if (replay.graphLed && replay.classified) graphClassified++;
                        if (replay.graphLed && replay.kernelStatus == "Ok") graphKernelOk++;
                        if (replay.graphLed && !replay.GraphChecksPass) graphCheckFailures++;
                        if (replay.GraphIncomplete) graphIncomplete++;
                        if (replay.graph.bodiesRan) { if (replay.graph.burstRan) graphBurstRan++; else graphManaged++; } else graphBodiesNotRun++;
                        graphTimes.Add(replay.graph.seconds);
                    }
                    if (replay.error != null)
                    {
                        errors++;
                    }
                    else
                    {
                        times.Add(replay.seconds);
                        string kind = replay.outcome + (replay.found ? replay.bent ? " (bent)" : replay.meshLed ? " (mesh-led)" : " (plane)" : "");
                        outcomes[kind] = outcomes.TryGetValue(kind, out int n) ? n + 1 : 1;
                    }

                    if (replay.inputCheck != "same") inputDiffers++;
                    if (replay.checkedSharing && !replay.ChecksPass) checkFailures++;

                    string verdict;
                    results.TryGetValue((entry.world, entry.attempt), out SpatialSplitResultLine live);
                    var differing = new List<string>();
                    bool kernelOnlyWithin = false;
                    if (live == null)
                    {
                        verdict = "結果未記録";   // no line: never NotFound
                        notRecorded++;
                        Count(notRecordedBy, "no result line");
                    }
                    else if (!live.searchObserved || live.sections == null || live.sections.Length != SpatialSplitCanonical.SectionCount)
                    {
                        // Why there is nothing to compare: kept apart, so that an attempt still out at the quitting is not
                        // read as a failure of the copy, nor either as a difference of the answer.
                        string why = !string.IsNullOrEmpty(live.outputsNotKept) ? "the outputs' copy failed or had no room"
                            : live.end == "AppQuit" ? "AppQuit before the numbers were read"
                            : "the numbers did not run or were not read (" + live.end + ")";
                        verdict = "結果未記録 (" + why + ")";
                        notRecorded++;
                        Count(notRecordedBy, why);
                    }
                    else if (replay.error != null)
                    {
                        verdict = "replay failed";
                        differ++;
                    }
                    else
                    {
                        for (int i = 0; i < SpatialSplitCanonical.SectionCount; i++)
                        {
                            if (string.Equals(live.sections[i], replay.sections[i], StringComparison.Ordinal)) continue;
                            if (i == SpatialSplitCanonical.SectionCount - 1 && KernelWithin(live.kernelNumbers, replay.kernelNumbers))
                            {
                                kernelOnlyWithin = true;
                                continue;
                            }

                            differing.Add(SpatialSplitCanonical.SectionNames[i]);
                        }

                        if (differing.Count == 0)
                        {
                            verdict = kernelOnlyWithin ? "same (kernel numbers within " + KernelTolerance.ToString("R", CultureInfo.InvariantCulture) + ")" : "same";
                            same++;
                            if (kernelOnlyWithin) kernelWithin++;
                        }
                        else
                        {
                            verdict = "differs";
                            differ++;
                            foreach (string section in differing) mismatchSections[section] = mismatchSections.TryGetValue(section, out int m) ? m + 1 : 1;
                        }
                    }

                    CultureInfo inv = CultureInfo.InvariantCulture;
                    line.Append(",\"verdict\":").Append(SpatialSplitCaptureSink.Json(verdict))
                        .Append(",\"indexed\":").Append(entry.sha256 != null ? "true" : "false")
                        .Append(",\"liveEnd\":").Append(SpatialSplitCaptureSink.Json(live != null ? live.end : ""))
                        .Append(",\"differing\":[");
                    for (int i = 0; i < differing.Count; i++) line.Append(i > 0 ? "," : "").Append(SpatialSplitCaptureSink.Json(differing[i]));
                    line.Append("],\"error\":").Append(SpatialSplitCaptureSink.Json(replay.error ?? ""))
                        .Append(",\"input\":").Append(SpatialSplitCaptureSink.Json(replay.inputCheck ?? ""))
                        .Append(",\"outcome\":").Append(SpatialSplitCaptureSink.Json(replay.outcome ?? ""))
                        .Append(",\"meshLed\":").Append(replay.meshLed ? "true" : "false").Append(",\"bent\":").Append(replay.bent ? "true" : "false")
                        // GRAPH-CONNECT-8: which stages ran (the history marks, not the result), the anchors of every path, the cut, the bent clock
                        .Append(",\"stagesRan\":{\"plane\":").Append(replay.ran ? "true" : "false").Append(",\"bent\":").Append(replay.bentRan ? "true" : "false")
                        .Append(",\"graph\":").Append(replay.graphEntered ? "true" : "false").Append(",\"meshLed\":").Append(replay.meshLedRan ? "true" : "false").Append('}')
                        .Append(",\"anchorsDistributed\":").Append(replay.anchorsDistributed ? "true" : "false")
                        .Append(",\"cut\":").Append(replay.cut ? "true" : "false").Append(",\"crossingConvexes\":").Append(replay.crossingConvexes)
                        .Append(",\"bentMs\":").Append((replay.bentSeconds * 1000.0).ToString("R", inv))
                        .Append(",\"distributed\":").Append(replay.distributed ? "true" : "false").Append(",\"classified\":").Append(replay.classified ? "true" : "false")
                        .Append(",\"kernelStatus\":").Append(SpatialSplitCaptureSink.Json(replay.kernelStatus ?? ""))
                        .Append(",\"positiveTriangles\":").Append(replay.positiveTriangles).Append(",\"negativeTriangles\":").Append(replay.negativeTriangles)
                        // The search's record as the live line has it, and the kernel's numbers: a difference told by its size.
                        .Append(",\"gap\":").Append(SpatialSplitCaptureSink.Json(replay.search.gap.ToString("R", inv)))
                        .Append(",\"directionsTried\":").Append(replay.search.directionsTried).Append(",\"planesTested\":").Append(replay.search.planesTested)
                        .Append(",\"triangleVisits\":").Append(replay.search.triangleVisits)
                        .Append(",\"meshDirectionsTried\":").Append(replay.search.meshDirectionsTried).Append(",\"meshPlanesTested\":").Append(replay.search.meshPlanesTested)
                        .Append(",\"bentFramesTried\":").Append(replay.search.bentFramesTried).Append(",\"bentSeedsTried\":").Append(replay.search.bentSeedsTried)
                        .Append(",\"bentCandidates\":").Append(replay.search.bentCandidates).Append(",\"bentIterations\":").Append(replay.search.bentIterations)
                        .Append(",\"bentElementsVerified\":").Append(replay.search.bentElementsVerified).Append(",\"bentPieces\":").Append(replay.search.bentPieces)
                        .Append(",\"kernelNumbers\":[").Append(string.Join(",", Array.ConvertAll(replay.kernelNumbers ?? new double[0], v => SpatialSplitCaptureSink.Json(v.ToString("R", inv))))).Append(']')
                        .Append(",\"replayMs\":").Append((replay.seconds * 1000.0).ToString("R", inv))
                        .Append(",\"liveWorkerMs\":").Append(live != null ? live.workerMs.ToString("R", inv) : "null")
                        .Append(",\"checked\":").Append(replay.checkedSharing ? "true" : "false")
                        .Append(",\"trianglesMissing\":").Append(replay.trianglesMissing).Append(",\"trianglesExtra\":").Append(replay.trianglesExtra)
                        .Append(",\"triangleMarginShort\":").Append(replay.triangleMarginShort).Append(",\"triangleWrongSide\":").Append(replay.triangleWrongSide)
                        .Append(",\"convexWrongSide\":").Append(replay.convexWrongSide).Append(",\"convexMarginShort\":").Append(replay.convexMarginShort)
                        .Append(",\"convexAcrossWithinEpsilon\":").Append(replay.convexAcrossWithinEpsilon)
                        .Append(",\"convexMostAcross\":").Append(SpatialSplitCaptureSink.Json(replay.convexMostAcross.ToString("R", inv)))
                        .Append(",\"supportEpsilon\":").Append(SpatialSplitCaptureSink.Json(sample.supportEpsilon.ToString("R", inv)))
                        .Append(",\"checkNote\":").Append(SpatialSplitCaptureSink.Json(replay.checkNote ?? ""))
                        .Append(",\"inputMode\":").Append(SpatialSplitCaptureSink.Json(options.InputName)).Append(",\"graphOn\":").Append(replay.graphOn ? "true" : "false")
                        .Append(",\"sessionId\":").Append(SpatialSplitCaptureSink.Json(sample.sessionId ?? "")).Append(",\"sha256\":").Append(SpatialSplitCaptureSink.Json(sample.sha256 ?? ""))
                        .Append(",\"fragment\":").Append(replay.fragment).Append(",\"rootFragment\":").Append(replay.rootFragment)
                        .Append(",\"bentRequested\":").Append(SpatialSplitCaptureSink.Json(replay.bentName)).Append(",\"bentSaved\":").Append(replay.bentSaved ? "true" : "false")
                        .Append(",\"bentEffective\":").Append(replay.bentEffective ? "true" : "false")
                        .Append(",\"orderRequested\":").Append(SpatialSplitCaptureSink.Json(replay.orderName)).Append(",\"graphFirstEffective\":").Append(replay.graphFirstEffective ? "true" : "false")
                        .Append(",\"anchorCount\":").Append(sample.anchors != null ? sample.anchors.Length : 0).Append(",\"convexCount\":").Append(sample.convexes != null ? sample.convexes.Length : 0)
                        .Append(",\"graphStages\":{\"entered\":").Append(replay.graphEntered ? "true" : "false").Append(",\"led\":").Append(replay.graphLed ? "true" : "false")
                        .Append(",\"distributed\":").Append(replay.graphLed && replay.distributed ? "true" : "false").Append(",\"anchorsDistributed\":").Append(replay.graphLed && replay.anchorsDistributed ? "true" : "false")
                        .Append(",\"classified\":").Append(replay.graphLed && replay.classified ? "true" : "false")
                        .Append(",\"kernelOk\":").Append(replay.graphLed && replay.kernelStatus == "Ok" ? "true" : "false").Append(",\"checked\":").Append(replay.graphLed && replay.checkedSharing ? "true" : "false")
                        .Append(",\"complete\":").Append(replay.GraphComplete ? "true" : "false").Append(",\"incomplete\":").Append(replay.GraphIncomplete ? "true" : "false").Append('}')
                        .Append(",\"graph\":").Append(replay.graphEntered ? GraphJson(in replay.graph, replay.graphSidesHash, inv) : "null")
                        .Append(",\"graphTrianglesMissing\":").Append(replay.graphTrianglesMissing).Append(",\"graphTrianglesExtra\":").Append(replay.graphTrianglesExtra)
                        .Append(",\"graphTriangleWrongSide\":").Append(replay.graphTriangleWrongSide).Append(",\"graphTriangleOrderBroken\":").Append(replay.graphTriangleOrderBroken)
                        .Append(",\"graphConvexWrongSide\":").Append(replay.graphConvexWrongSide).Append(",\"graphConvexNotSided\":").Append(replay.graphConvexNotSided)
                        .Append(",\"object\":").Append(SpatialSplitCaptureSink.Json(sample.objectName))
                        .Append(",\"code\":").Append(SpatialSplitCaptureSink.Json(sample.codeVersion));
                    if (pool != null)
                    {
                        // GRAPH-CONNECT-14: the condition and the pool record (the stamps as ms from the run's origin; null where absent)
                        line.Append(",\"condition\":").Append(SpatialSplitCaptureSink.Json(replay.condition))
                            .Append(",\"pool\":").Append(replay.pool != null ? replay.pool.Json(pool.Origin, inv) : "null");
                    }
                    if (replay.error == null)
                    {
                        // What the replay adopted, as the live result line has its own: two answers that differ, told apart;
                        // every field of the search section, and the input's margin beside the search's own.
                        SpatialSplitCaptureSink.AppendAdopted(line, in replay.search);
                        SpatialSplitCaptureSink.AppendSearchRecord(line, in replay.search, replay.ran, replay.failed, replay.kernelExecutedManaged);
                        line.Append(",\"inputMargin\":").Append(SpatialSplitCaptureSink.Json(sample.margin.ToString("R", inv)));
                    }

                    line.Append('}');
                    replayLines.WriteLine(line.ToString());
                    }   // the conditions
                }
            }

            if (pool != null)
            {
                // GRAPH-CONNECT-14 (TL P2-4): one line a competitor, with its condition, sample, warm-up flag and stamps
                File.WriteAllLines(Path.Combine(outputDirectory, "competitors.jsonl"), pool.CompetitorLines(), new UTF8Encoding(false));
            }
            times.Sort();
            var summary = new StringBuilder();
            CultureInfo c = CultureInfo.InvariantCulture;
#if ENABLE_IL2CPP
            const string backend = "IL2CPP";
#else
            const string backend = "Mono";
#endif
            summary.AppendLine("spatial split replay of the session " + Path.GetFileName(Path.GetFullPath(sessionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .AppendLine("replayed by: Unity " + Application.unityVersion + ", " + backend + ", " + (Application.isEditor ? "Editor" : "Player") + ", " + Application.platform)
                .AppendLine("samples listed " + listed + ", committed and not in the index (found by the folder) " + unindexed + ", result lines " + resultLines
                    + ", lines not read (cut short or broken): index " + brokenIndexLines + ", results " + brokenResultLines + ", partial files (not samples) " + partials.Length)
                .AppendLine("unreadable " + unreadable + ", hash differs from the index " + hashDiffers + ", replayed " + replayed + " (replay errors " + errors + ")")
                .AppendLine("input rebuilt differently " + inputDiffers)
                .AppendLine("compared with the live result " + (same + differ) + ": same " + same + " (of them kernel numbers only within " + KernelTolerance.ToString("R", c) + ": "
                    + kernelWithin + "), differs " + differ + "; not compared, 結果未記録 " + notRecorded)
                .AppendLine("結果未記録 by reason: " + Join(notRecordedBy))
                .AppendLine("saving's failures: unreadable " + unreadable + ", hash differs " + hashDiffers + ", committed without an index line " + unindexed
                    + ", lines not read " + (brokenIndexLines + brokenResultLines))
                .AppendLine("differing sections: " + Join(mismatchSections))
                .AppendLine("checks failed " + checkFailures + " (triangles lost or doubled, a vertex short of the margin or on the wrong side, a convex on the wrong side)")
                .AppendLine("replay outcomes: " + Join(outcomes))
                .AppendLine("options: inputMode " + options.InputName + ", graph " + (options.graph ? "on (prepared " + (graphPrepared ? "yes" : "NO") + "; limits: triangles " + options.graphLimits.maxTriangles + ", convexes " + options.graphLimits.maxConvexes
                        + ", span " + options.graphLimits.maxVertexSpan + ", nodes " + options.graphLimits.maxNodes + ", candidates " + options.graphLimits.maxCandidates + ", bytes " + options.graphLimits.maxGraphBytes + ", work " + options.graphLimits.workBudget + ")" : "off")
                    + ", bent " + options.BentName + ", order " + options.OrderName + ", list " + (options.listPath ?? "none") + ", pool " + options.PoolName)
                .AppendLine(options.listPath == null ? "tuning list: none (every sample of the folder)"
                    : "tuning list for session " + sessionId + ": entries " + listEntries + ", replayed " + listed + ", missing " + listMissing + ", duplicate " + listDuplicates + ", hash differs from the list " + listHashDiffers
                        + ", files left out (not listed) " + listLeftOut + "; list check " + (listOk ? "passed" : "FAILED"))
                .AppendLine(!options.graph ? "graph stage: off"
                    : "graph stage: entered " + graphEntered + " (verdicts: " + Join(graphVerdicts) + "); led " + graphLedCount + ", distributed " + graphDistributed + ", classified " + graphClassified + ", kernel Ok " + graphKernelOk
                        + ", Separable but incomplete " + graphIncomplete + "; bodies through Burst " + graphBurstRan + ", managed " + graphManaged + ", not run " + graphBodiesNotRun + "; graph sharing checks failed " + graphCheckFailures
                        + (graphTimes.Count == 0 ? "" : "; graph stage time (this machine): median " + Ms(graphTimes, 0.5, c) + " ms, p95 " + Ms(graphTimes, 0.95, c) + " ms, p99 " + Ms(graphTimes, 0.99, c) + " ms, max " + Ms(graphTimes, 1.0, c) + " ms"))
                .AppendLine("bent stage (" + (options.bentOff ? "off, asked for: the saved setting not followed" : "the saved setting") + "): effective in " + bentEffectiveCount + " samples, ran " + bentRanCount + ", found " + bentFoundCount
                    + (bentTimes.Count == 0 ? "" : "; bent stage time (this machine, the FindBentSurface call): median " + Ms(bentTimes, 0.5, c) + " ms, p95 " + Ms(bentTimes, 0.95, c) + " ms, p99 " + Ms(bentTimes, 0.99, c) + " ms, max " + Ms(bentTimes, 1.0, c) + " ms"))
                .AppendLine(!options.graphFirst ? "order: current (the bent partition before the graph stage)"
                    : "order: graph-first applied to " + graphFirstCount + " samples; bent fallback after an unfinished graph: ran " + fallbackRan + ", found " + fallbackFound)
                .AppendLine(times.Count == 0 ? "replay search time: none"
                    : "replay search time (this machine, not the live one's" + (pool != null ? "; the worker's run" : "") + "): median " + (times[times.Count / 2] * 1000.0).ToString("F3", c) + " ms, p95 "
                        + (times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))] * 1000.0).ToString("F3", c) + " ms, max " + (times[times.Count - 1] * 1000.0).ToString("F3", c) + " ms");
            summary.AppendLine(pool != null ? pool.Describe() + "; timeline " + (CutSpatialSplit.TimelineObserve ? "on" : "off (the new stamps are not taken: t0, t1, t4 and the waits are null; t2, t3 and X are the existing clock's)")
                : "pool replay: off (every attempt ran on this thread)");
            // The same as it ran: every sample there is was read, replayed, compared with its live result and found the
            // same, and passed the checks -- one that replayed without a live result to compare is not "the same".
            // A session of no sample at all (nothing captured, an empty or wrong folder) is no evidence: never "the same".
            bool none = samples.Count == 0;
            noSample = none;
            if (!options.graph && !options.pool)
            {
                allSame = !none && same >= 1 && unreadable == 0 && hashDiffers == 0 && errors == 0 && differ == 0 && checkFailures == 0 && inputDiffers == 0
                    && notRecorded == 0 && same == samples.Count && unindexed == 0 && brokenIndexLines == 0 && brokenResultLines == 0 && listOk;
                if (none) summary.AppendLine("NO SAMPLE: nothing to replay in this folder (no index line and no sample file)");
                summary.AppendLine("every sample compared with its live result and the same, every check passed" + (options.listPath != null ? ", the list check passed" : "") + ": " + (allSame ? "yes" : "NO"));
            }
            else
            {
                // With the graph stage on, the live result is not what the replay must equal (the live search had no graph stage): the
                // outcome is what the list, the replay and the graph's own checks say; the comparison with other replays is offline.
                // GRAPH-CONNECT-14: the pool replay writes two lines a sample (the conditions D and C): every sample must have replayed twice
                int expectedReplays = samples.Count * (options.pool ? 2 : 1);
                allSame = !none && replayed == expectedReplays && unreadable == 0 && hashDiffers == 0 && errors == 0 && inputDiffers == 0 && graphCheckFailures == 0 && graphIncomplete == 0
                    && checkFailures == 0 && unindexed == 0 && brokenIndexLines == 0 && brokenResultLines == 0 && listOk && graphPrepared
                    && (!options.bentOff || (bentEffectiveCount == 0 && bentRanCount == 0));   // B: the bent partition neither effective nor entered anywhere
                if (none) summary.AppendLine("NO SAMPLE: nothing to replay in this folder (no index line and no sample file)");
                summary.AppendLine((options.pool ? "pool on: every listed sample replayed in both conditions (D and C) without error" : "graph on: every listed sample replayed without error")
                    + ", the list check passed, every Separable complete (shared out, anchors, classified, kernel Ok, checked), every graph and existing sharing check passed (the live comparison is not the criterion here): " + (allSame ? "yes" : "NO"));
            }
            File.WriteAllText(Path.Combine(outputDirectory, "summary.txt"), summary.ToString(), new UTF8Encoding(false));
            return summary.ToString();
            }   // the pool
        }

        private static string ReadSessionId(string sessionDirectory)
        {
            string path = Path.Combine(sessionDirectory, "session.json");
            if (!File.Exists(path)) return "";
            try { return JsonUtility.FromJson<SpatialSplitSessionIdLine>(File.ReadAllText(path))?.sessionId ?? ""; }
            catch (ArgumentException) { return ""; }
        }

        private static string Ms(List<double> values, double quantile, CultureInfo c)
        {
            if (values.Count == 0) return "-";
            var copy = new List<double>(values);
            copy.Sort();
            int at = quantile >= 1.0 ? copy.Count - 1 : Math.Min(copy.Count - 1, (int)(copy.Count * quantile));
            return (copy[at] * 1000.0).ToString("F3", c);
        }

        /// <summary>The graph record as JSON (the time is for the record; the comparison leaves it out).</summary>
        internal static string GraphJson(in SpatialSplitGraphRecord g, string sidesHash, CultureInfo inv)
        {
            return "{\"verdict\":" + SpatialSplitCaptureSink.Json(g.verdict.ToString()) + ",\"stop\":" + SpatialSplitCaptureSink.Json(g.stop.ToString()) + ",\"unfinished\":" + (g.unfinished ? "true" : "false")
                + ",\"nodes\":" + g.nodes + ",\"meshComponents\":" + g.meshComponents + ",\"candidates\":" + g.candidates + ",\"cutAttempts\":" + g.cutAttempts + ",\"gjkLimitHit\":" + g.gjkLimitHit
                + ",\"work\":" + g.work + ",\"workPrepare\":" + g.workPrepare + ",\"workSearch\":" + g.workSearch + ",\"nativeBytes\":" + g.nativeBytes + ",\"listGrowths\":" + g.listGrowths
                + ",\"vertexSpan\":" + g.vertexSpan + ",\"referencedVertices\":" + g.referencedVertices
                + ",\"positiveConvexes\":" + g.positiveConvexes + ",\"negativeConvexes\":" + g.negativeConvexes + ",\"positiveTriangles\":" + g.positiveTriangles + ",\"negativeTriangles\":" + g.negativeTriangles
                + ",\"sidesHash\":" + SpatialSplitCaptureSink.Json(sidesHash ?? "") + ",\"bodiesRan\":" + (g.bodiesRan ? "true" : "false") + ",\"burstRan\":" + (g.burstRan ? "true" : "false")
                + ",\"seconds\":" + g.seconds.ToString("R", inv) + ",\"reason\":" + SpatialSplitCaptureSink.Json(g.reason ?? "") + "}";
        }

        /// <summary>FNV-1a 64 of the sides in the engine's form (1 positive, 0 negative; the convexes then the triangles), as the batch hashes its own.</summary>
        internal static string SidesHash(byte* sides, int count)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < count; i++) { h ^= sides[i]; h *= 1099511628211UL; }
            return h.ToString("x16");
        }

        // The lines of a .jsonl file that read whole; those that do not are counted (a line cut short by an ending process).
        private static List<T> Lines<T>(string path, out int broken)
        {
            broken = 0;
            var lines = new List<T>();
            if (!File.Exists(path)) return lines;
            foreach (string line in File.ReadAllLines(path))
            {
                string text = line.Trim();
                if (text.Length == 0) continue;
                T value = default;
                bool read = false;
                if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
                {
                    try
                    {
                        value = JsonUtility.FromJson<T>(text);
                        read = value != null;
                    }
                    catch (ArgumentException)
                    {
                    }
                }

                if (read) lines.Add(value);
                else broken++;
            }

            return lines;
        }

        // w<world>-a<attempt>.zss.gz (SpatialSplitCaptureSink.SampleFileName).
        private static bool TryParseSampleName(string file, out long world, out long attempt)
        {
            world = attempt = 0;
            const string suffix = ".zss.gz";
            if (!file.StartsWith("w", StringComparison.Ordinal) || !file.EndsWith(suffix, StringComparison.Ordinal)) return false;
            string[] parts = file.Substring(1, file.Length - 1 - suffix.Length).Split(new[] { "-a" }, StringSplitOptions.None);
            return parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out world)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out attempt);
        }

        private static void Count(SortedDictionary<string, int> counts, string key) => counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;

        private static string Join(SortedDictionary<string, int> counts)
        {
            if (counts.Count == 0) return "none";
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in counts) parts.Add(kv.Key + " " + kv.Value);
            return string.Join(", ", parts);
        }

        private static bool KernelWithin(string[] live, double[] replay)
        {
            if (live == null || replay == null || live.Length != replay.Length) return false;
            for (int i = 0; i < live.Length; i++)
            {
                if (!double.TryParse(live[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)) return false;
                double b = replay[i];
                if (a.Equals(b)) continue;   // NaN equals NaN here, as bits of the same kind
                if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b)) return false;
                if (Math.Abs(a - b) > KernelTolerance * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)))) return false;
            }

            return true;
        }

        /// <summary>Replays one sample (Main thread: the shape's mesh is made here), the compact input and no graph stage.</summary>
        internal static SpatialSplitReplayResult Run(SpatialSplitSample sample) => Run(sample, SpatialSplitReplayOptions.Default);

        private static int s_resourcesHeld;

        /// <summary>Tests (TL P1): the pool replay's gate before every attempt's Begin, and its timeouts; null and the defaults in a run.</summary>
        internal static System.Threading.ManualResetEventSlim TestPoolBeginGate;
        internal static int TestPoolAttemptTicks = SpatialSplitReplayPool.DefaultAttemptTicks;
        internal static int TestPoolStopMilliseconds = SpatialSplitReplayPool.DefaultStopMilliseconds;

        /// <summary>How many samples' inputs are held unfreed because their work did not come back from the pool (GRAPH-CONNECT-14, TL P1).</summary>
        internal static int ResourcesHeld => Volatile.Read(ref s_resourcesHeld);

        /// <summary>Replays one sample with the options (Main thread: the shape's mesh is made here).</summary>
        internal static SpatialSplitReplayResult Run(SpatialSplitSample sample, SpatialSplitReplayOptions options) => Run(sample, options, null, null);

        /// <summary>
        /// As above; with a pool (GRAPH-CONNECT-14 §3-1) the attempt's numbers run on the pool's worker under the condition -- "D" (graph off,
        /// the saved bent setting, the order as before) or "C" (graph on, graph-first) -- instead of the options' settings on this thread.
        /// </summary>
        internal static SpatialSplitReplayResult Run(SpatialSplitSample sample, SpatialSplitReplayOptions options, SpatialSplitReplayPool pool, string condition)
        {
            options = options ?? SpatialSplitReplayOptions.Default;
            bool graphOn = condition != null ? condition == "C" : options.graph;
            bool graphFirst = condition != null ? condition == "C" : options.graphFirst;
            bool bentOff = condition == null && options.bentOff;
            var result = new SpatialSplitReplayResult
            {
                world = sample.world, attempt = sample.attempt, graphOn = graphOn, orderName = graphFirst ? "graph-first" : "current", bentName = bentOff ? "off" : "saved", condition = condition ?? "",
            };
            int vertexBytes = UnsafeUtility.SizeOf<VpRenderVertex>();
            if (sample.vertexStructBytes != vertexBytes)
            {
                result.error = "the sample's vertex is " + sample.vertexStructBytes + " bytes, this build's " + vertexBytes;
                return result;
            }

            int named = sample.vertexIds.Length;
            int n = sample.indices.Length;
            int s = sample.submeshOffsets.Length;
            int c = sample.convexes.Length;
            // The span input (GRAPH-CONNECT-6 §2, the product's shape): a view as long as the greatest id + 1, the referenced vertices
            // at their original ids, every other slot's position NaN, the indices as saved. The compact input (the live reproduction):
            // the referenced vertices renumbered 0..V-1 and the indices mapped.
            long viewLength = named;
            if (options.spanInput)
            {
                uint greatest = 0;
                foreach (uint id in sample.vertexIds) greatest = Math.Max(greatest, id);
                foreach (uint id in sample.indices) greatest = Math.Max(greatest, id);
                viewLength = (long)greatest + 1;
                if (viewLength > int.MaxValue / Math.Max(1, vertexBytes)) { result.error = "the span view would be too long: " + viewLength; return result; }
            }

            var vertices = new NativeArray<VpRenderVertex>((int)Math.Max(1, viewLength), Allocator.Persistent, options.spanInput ? NativeArrayOptions.ClearMemory : NativeArrayOptions.UninitializedMemory);
            var indices = new NativeArray<uint>(Math.Max(1, n), Allocator.Persistent);
            PhysicsOwnerShape shape = null;
            Mesh mesh = null;
            CutSpatialSplit.Attempt attempt = null;
            byte* canonical = null;
            // GRAPH-CONNECT-14 (TL P1): a work out on a pool that did not come back keeps everything it may still read -- the attempt, the
            // shape, the vertices and the indices are freed below only when nothing is out, or when the stop says the work was collected
            bool holdResources = false;
            try
            {
                if (options.spanInput)
                {
                    byte* raw = (byte*)vertices.GetUnsafePtr();
                    int at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
                    for (long k = 0; k < viewLength; k++) *(float3*)(raw + (k * vertexBytes) + at0) = new float3(float.NaN);
                    fixed (byte* from = sample.vertexBytes)
                    {
                        for (int i = 0; i < named; i++) UnsafeUtility.MemCpy(raw + ((long)sample.vertexIds[i] * vertexBytes), from + ((long)i * vertexBytes), vertexBytes);
                    }

                    for (int i = 0; i < n; i++) indices[i] = sample.indices[i];
                }
                else
                {
                    fixed (byte* from = sample.vertexBytes)
                    {
                        UnsafeUtility.MemCpy(vertices.GetUnsafePtr(), from, (long)named * vertexBytes);
                    }

                    // The storage's ids as the replay's own, from 0 in the same order; an index the storage's view did not
                    // reach stays past the vertices, so that it is refused as it was.
                    uint view = (uint)Math.Max(0, sample.vertexViewLength);
                    for (int i = 0; i < n; i++)
                    {
                        uint v = sample.indices[i];
                        if (v >= view)
                        {
                            indices[i] = (uint)named;
                            continue;
                        }

                        int at = Array.BinarySearch(sample.vertexIds, v);
                        if (at < 0) throw new InvalidDataException("index " + i + " names vertex " + v + ", which the sample did not keep");
                        indices[i] = (uint)at;
                    }
                }

                try
                {
                    shape = BuildShape(sample, out mesh);
                }
                catch (Exception e)
                {
                    result.error = "the shape could not be made again: " + e.GetType().Name + ": " + e.Message;
                    return result;
                }

                result.inputCheck = CompareShape(shape, sample);
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
                    bentSearch = bentOff ? false : sample.bentSearch,   // GRAPH-CONNECT-8: the B condition leaves the bent partition out
                    graphFirst = graphFirst,                            // GRAPH-CONNECT-10: the C condition's order
                    bentLimits = sample.bentLimits,
                    maxConvexVertices = CutSpatialSplit.Attempt.MaxConvexVertices(shape),
                    geometryToOwner = sample.geometryToOwner,
                    shapeToOwner = shape.LocalToOwner,
                    indexCount = n,
                    submeshCount = s,
                    convexCount = c,
                    materials = (int[])sample.materials.Clone(),
                    issuedFrame = sample.issuedFrame,
                    anchors = sample.anchors != null ? (float3[])sample.anchors.Clone() : null,
                    vertices = (VpRenderVertex*)vertices.GetUnsafePtr(),
                    vertexViewLength = (int)viewLength,
                    indices = (uint*)indices.GetUnsafePtr(),
                };
                if (attempt.maxConvexVertices != sample.maxConvexVertices && result.inputCheck == "same")
                {
                    result.inputCheck = "the most vertices of a convex is " + attempt.maxConvexVertices + ", the sample's " + sample.maxConvexVertices;
                }

                attempt.TakeBlock();
                for (int i = 0; i < s; i++)
                {
                    attempt.submeshOffsets[i] = sample.submeshOffsets[i];
                    attempt.submeshCounts[i] = sample.submeshCounts[i];
                }

                for (int k = 0; k < c; k++)
                {
                    attempt.banks[k] = shape.BankOf(k);
                    attempt.ranges[k] = shape.Convex(k);
                }

                attempt.classificationBlock = PhysicsCutBlocks.Take<byte>(PhysicsCutClassification.RequiredBlockBytes(shape));
                attempt.arena = new PhysicsCutArena(default, c);
                if (graphOn && (sample.anchors == null || sample.anchors.Length == 0) && c >= 2)
                {
                    // The graph stage as the product issues it (GRAPH-CONNECT-4 §3-2, §3-4): an owner without anchors and with two convexes
                    // or more; the sides' bytes allocated here as the main thread does.
                    attempt.graphSearch = true;
                    attempt.graphLimits = options.graphLimits;
                    attempt.testGraphRefuseDistribution = CutSpatialSplit.TestGraphRefuseDistribution;
                    attempt.testGraphGjkMaxIterations = CutSpatialSplit.TestGraphGjkMaxIterations;
                    attempt.testGraphShortSides = CutSpatialSplit.TestGraphShortSides;
                    if (CutSpatialSplit.TestFailGraphSidesAllocation)
                    {
                        // tests: the product's issue records Unfinished(Allocation) in advance when the sides' allocation on the main thread fails
                        attempt.graphSides = default;
                        attempt.graph = new SpatialSplitGraphRecord { verdict = SpatialSplitGraphVerdict.Unfinished, stop = SpatialSplitGraphStop.Allocation, unfinished = true, reason = "the test refused the sides' allocation (recorded before the attempt)" };
                    }
                    else
                    {
                        attempt.graphSides = PhysicsCutBlocks.Take<byte>(c + (n / 3));
                    }
                }

                if (pool != null)
                {
                    // GRAPH-CONNECT-14 §3-1: through the dispatcher and the real Background pool; the time is the worker's run (the existing clock)
                    holdResources = true;   // out on the pool from here
                    try
                    {
                        pool.RunAttempt(attempt, result, condition);
                        holdResources = false;   // collected: the input is this thread's again
                    }
                    catch (SpatialSplitReplayPoolStopException stop)
                    {
                        holdResources = !stop.WorkCollected;
                        throw;   // never an error line: the session ends (RunSession lets it through)
                    }
                    result.seconds = attempt.workerSeconds;
                }
                else
                {
                    long begin = Stopwatch.GetTimestamp();
                    attempt.Begin();
                    result.seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                }
                result.graphEntered = attempt.graph.Entered;
                result.graphLed = attempt.graphLed;
                result.graph = attempt.graph;
                if (attempt.graphLed && attempt.graphSides.IsCreated)
                {
                    // The sides in the engine's form: BeginGraph recoded the convexes' bytes to ConvexSide (Positive 0, Negative 1);
                    // the triangles' bytes are the engine's (1 positive). Normalised so that the batch's placement hashes the same.
                    byte* sides = (byte*)attempt.graphSides.GetUnsafePtr();
                    int count = c + (n / 3);
                    var normal = new NativeArray<byte>(Math.Max(1, count), Allocator.Temp);
                    byte* np = (byte*)normal.GetUnsafePtr();
                    for (int i = 0; i < c; i++) np[i] = sides[i] == (byte)ConvexSide.Positive ? (byte)1 : (byte)0;
                    for (int i = c; i < count; i++) np[i] = sides[i] != 0 ? (byte)1 : (byte)0;
                    result.graphSidesHash = SidesHash(np, count);
                    normal.Dispose();
                }

                long capacity = SpatialSplitCanonical.MaxBytes(attempt);
                canonical = (byte*)UnsafeUtility.Malloc(capacity, 16, Allocator.Persistent);
                var w = new SpatialSplitRawWriter(canonical, capacity);
                var ends = new long[SpatialSplitCanonical.SectionCount];
                if (options.spanInput)
                {
                    SpatialSplitCanonical.Write(attempt, null, 0, ref w, ends);   // the indices are the original ids already
                }
                else
                {
                    fixed (uint* ids = sample.vertexIds)
                    {
                        SpatialSplitCanonical.Write(attempt, ids, named, ref w, ends);
                    }
                }

                result.sections = SpatialSplitCanonical.Hashes(canonical, ends, new byte[1 << 16]);
                result.kernelNumbers = SpatialSplitCanonical.KernelNumbers(in attempt.kernel);
                SpatialSplitSearchResult search = attempt.search;
                result.search = search;
                result.ran = attempt.ran;
                result.failed = attempt.failure != null;
                result.kernelExecutedManaged = attempt.kernel.executedManaged;
                result.outcome = attempt.failure != null ? "Failed: " + attempt.failure.GetType().Name : search.outcome.ToString();
                result.found = search.outcome == SpatialSplitSearchOutcome.Found;
                result.meshLed = search.meshLed;
                result.bent = CutSpatialSplit.IsBentResult(in search);
                result.bentRan = search.bent;
                result.meshLedRan = search.meshLed;
                result.bentSaved = sample.bentSearch;
                result.bentEffective = attempt.bentSearch;
                result.graphFirstEffective = attempt.graphFirstEffective;
                result.bentSeconds = attempt.bentSeconds;
                result.crossingConvexes = search.crossingConvexes;
                result.fragment = sample.fragment;
                result.rootFragment = sample.rootFragment;
                result.distributed = attempt.distributed;
                result.classified = attempt.classified;
                result.anchorsDistributed = attempt.anchorsDistributed;
                result.cut = attempt.cut;
                result.kernelStatus = attempt.classified ? attempt.kernel.status.ToString() : "";
                result.positiveTriangles = attempt.graphLed ? attempt.graph.positiveTriangles : search.positiveTriangles;
                result.negativeTriangles = attempt.graphLed ? attempt.graph.negativeTriangles : search.negativeTriangles;
                if (attempt.graphLed) CheckGraph(attempt, result); else Check(attempt, result);
            }
            catch (SpatialSplitReplayPoolStopException)
            {
                throw;   // the pool replay's stop: not a sample's error
            }
            catch (Exception e)
            {
                result.error = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (canonical != null) UnsafeUtility.Free(canonical, Allocator.Persistent);
                if (holdResources)
                {
                    // The work may still be on a worker: nothing it can read is freed (the attempt, the shape, the vertices, the indices
                    // stay allocated for the process's life; the session ends with the exception). Counted for the record.
                    Interlocked.Increment(ref s_resourcesHeld);
                }
                else
                {
                    attempt?.Free(null);
                    shape?.Dispose();
                    if (mesh != null)
                    {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(mesh);
                        else UnityEngine.Object.DestroyImmediate(mesh);
                    }

                    vertices.Dispose();
                    indices.Dispose();
                }
            }

            return result;
        }

        // The sample's convexes in their order, in one bank of the replay's own (each convex's arrays as they were, its
        // bases from where it now begins), made a shape the product's way: Authored copies it, so the bank goes back here.
        // One mesh for every convex, whose box holds them all: the shape asks a mesh of each, the numbers read none.
        private static PhysicsOwnerShape BuildShape(SpatialSplitSample s, out Mesh mesh)
        {
            int vertices = 0, offsets = 0, faceIndices = 0, edges = 0;
            foreach (SpatialSplitSample.Convex convex in s.convexes)
            {
                vertices += convex.range.vertexCount;
                offsets += convex.range.faceCount + 1;
                faceIndices += convex.range.faceIndexCount;
                edges += convex.range.edgeCount;
            }

            long vAt = 0;
            long oAt = vAt + Align((long)vertices * sizeof(float3));
            long iAt = oAt + Align((long)offsets * sizeof(int));
            long fAt = iAt + Align((long)faceIndices * sizeof(int));
            long eAt = fAt + Align((long)faceIndices * sizeof(int));
            long bytes = eAt + Align((long)edges * sizeof(BrepEdge));
            var block = new NativeArray<byte>(checked((int)Math.Max(16, bytes)), Allocator.Persistent);
            try
            {
                byte* at = (byte*)block.GetUnsafePtr();
                var bank = new ConvexBrepBank
                {
                    vertices = (float3*)(at + vAt), faceOffsets = (int*)(at + oAt), faceIndices = (int*)(at + iAt),
                    faceEdges = (int*)(at + fAt), edges = (BrepEdge*)(at + eAt),
                };
                var ranges = new ConvexBrepRange[s.convexes.Length];
                int v = 0, o = 0, i = 0, e = 0;
                var lo = new float3(float.PositiveInfinity);
                var hi = new float3(float.NegativeInfinity);
                for (int c = 0; c < s.convexes.Length; c++)
                {
                    SpatialSplitSample.Convex convex = s.convexes[c];
                    ConvexBrepRange r = convex.range;
                    ranges[c] = new ConvexBrepRange
                    {
                        vertexBase = v, vertexCount = r.vertexCount, faceBase = o, faceCount = r.faceCount,
                        faceIndexBase = i, faceIndexCount = r.faceIndexCount, edgeBase = e, edgeCount = r.edgeCount, maxFaceLoop = r.maxFaceLoop,
                    };
                    fixed (float3* p = convex.vertices) UnsafeUtility.MemCpy(bank.vertices + v, p, (long)r.vertexCount * sizeof(float3));
                    fixed (int* p = convex.faceOffsets) UnsafeUtility.MemCpy(bank.faceOffsets + o, p, (long)(r.faceCount + 1) * sizeof(int));
                    fixed (int* p = convex.faceIndices) UnsafeUtility.MemCpy(bank.faceIndices + i, p, (long)r.faceIndexCount * sizeof(int));
                    fixed (int* p = convex.faceEdges) UnsafeUtility.MemCpy(bank.faceEdges + i, p, (long)r.faceIndexCount * sizeof(int));
                    fixed (BrepEdge* p = convex.edges) UnsafeUtility.MemCpy(bank.edges + e, p, (long)r.edgeCount * sizeof(BrepEdge));
                    foreach (float3 point in convex.vertices)
                    {
                        if (!math.all(math.isfinite(point))) continue;
                        lo = math.min(lo, point);
                        hi = math.max(hi, point);
                    }

                    v += r.vertexCount;
                    o += r.faceCount + 1;
                    i += r.faceIndexCount;
                    e += r.edgeCount;
                }

                mesh = new Mesh { name = "Zantetsu Spatial Split Replay", hideFlags = HideFlags.HideAndDontSave };
                if (math.all(lo <= hi))
                {
                    mesh.vertices = new Vector3[] { lo, hi };
                    mesh.RecalculateBounds();
                }

                var meshes = new Mesh[s.convexes.Length];
                for (int c = 0; c < meshes.Length; c++) meshes[c] = mesh;
                return PhysicsOwnerShape.Authored(bank, ranges, meshes, PhysicsShapeSource.External(), s.shapeToOwner);
            }
            finally
            {
                block.Dispose();
            }
        }

        private static long Align(long bytes) => (bytes + 15) & ~15L;

        /// <summary>Whether the shape the replay made reads the sample's convexes bit for bit (the input before and after the save, for the numbers).</summary>
        internal static string CompareShape(PhysicsOwnerShape shape, SpatialSplitSample s)
        {
            if (shape.ConvexCount != s.convexes.Length) return "convexes " + shape.ConvexCount + ", the sample's " + s.convexes.Length;
            if (!shape.LocalToOwner.Equals(s.shapeToOwner)) return "the shape's frame differs";
            for (int c = 0; c < s.convexes.Length; c++)
            {
                ConvexBrepRange r = shape.Convex(c);
                ConvexBrepBank b = shape.BankOf(c);
                SpatialSplitSample.Convex x = s.convexes[c];
                if (r.vertexCount != x.range.vertexCount || r.faceCount != x.range.faceCount || r.faceIndexCount != x.range.faceIndexCount
                    || r.edgeCount != x.range.edgeCount || r.maxFaceLoop != x.range.maxFaceLoop)
                {
                    return "convex " + c + "'s counts differ";
                }

                fixed (float3* p = x.vertices)
                fixed (int* fo = x.faceOffsets)
                fixed (int* fi = x.faceIndices)
                fixed (int* fe = x.faceEdges)
                fixed (BrepEdge* ed = x.edges)
                {
                    if (UnsafeUtility.MemCmp(b.vertices + r.vertexBase, p, (long)r.vertexCount * sizeof(float3)) != 0
                        || UnsafeUtility.MemCmp(b.faceOffsets + r.faceBase, fo, (long)(r.faceCount + 1) * sizeof(int)) != 0
                        || UnsafeUtility.MemCmp(b.faceIndices + r.faceIndexBase, fi, (long)r.faceIndexCount * sizeof(int)) != 0
                        || UnsafeUtility.MemCmp(b.faceEdges + r.faceIndexBase, fe, (long)r.faceIndexCount * sizeof(int)) != 0
                        || UnsafeUtility.MemCmp(b.edges + r.edgeBase, ed, (long)r.edgeCount * sizeof(BrepEdge)) != 0)
                    {
                        return "convex " + c + "'s arrays differ";
                    }
                }
            }

            return "same";
        }

        /// <summary>
        /// A bent surface read from its definition (DESIGN 7.9.2) -- knots (u, h), joined by straight pieces, flat beyond
        /// the first and the last, carried along the third axis -- and an element (a triangle, a convex) tested against
        /// it over its whole extent, written here apart from the search: the element is the convex polygon of its
        /// vertices in the (u, h) plane. It is wholly on its side when every vertex is, and at every knot inside its u
        /// range the polygon's nearest point on that vertical (its lower one for the side above) is too -- the difference
        /// of two piecewise-linear functions keeps its sign on an interval where it does at every breakpoint of either --
        /// and it keeps the margin when the least distance between the polygon and the polyline is the margin or more:
        /// for two that do not cross, the least of every vertex's distance to the polyline and every knot's distance to
        /// the polygon. Each segment between two of its vertices stands for the polygon's edges (an edge is one of them,
        /// and every such segment lies inside the polygon).
        /// </summary>
        internal sealed class BentCheck
        {
            private readonly float3 _height, _along;
            private readonly float2[] _knots;

            internal BentCheck(in SpatialSplitBentSurface surface)
            {
                _height = surface.height;
                _along = surface.along;
                int n = math.clamp(surface.knotCount, 0, SpatialSplitBentSurface.MaxKnots);
                _knots = new float2[n];
                for (int k = 0; k < n; k++) _knots[k] = new float2(surface.KnotU(k), surface.KnotH(k));
            }

            internal float2 Project(float3 owner) => new float2(math.dot(_along, owner), math.dot(_height, owner));

            internal float HeightAt(float u)
            {
                int last = _knots.Length - 1;
                if (last < 0) return float.NaN;
                if (u <= _knots[0].x) return _knots[0].y;
                if (u >= _knots[last].x) return _knots[last].y;
                for (int k = 0; k < last; k++)
                {
                    float2 a = _knots[k], b = _knots[k + 1];
                    if (u > b.x) continue;
                    float t = b.x > a.x ? (u - a.x) / (b.x - a.x) : 0f;
                    return a.y + (t * (b.y - a.y));
                }

                return _knots[last].y;
            }

            // A point's distance to the polyline with its two flat ends (each a ray going outward from its end knot).
            internal float Distance(float2 q)
            {
                int last = _knots.Length - 1;
                if (last < 0) return float.NaN;
                float best = q.x <= _knots[0].x ? math.abs(q.y - _knots[0].y) : math.distance(q, _knots[0]);
                best = math.min(best, q.x >= _knots[last].x ? math.abs(q.y - _knots[last].y) : math.distance(q, _knots[last]));
                for (int k = 0; k < last; k++) best = math.min(best, ToSegment(q, _knots[k], _knots[k + 1]));
                return best;
            }

            private static float ToSegment(float2 q, float2 a, float2 b)
            {
                float2 e = b - a;
                float l2 = math.lengthsq(e);
                float t = l2 > 0f ? math.clamp(math.dot(q - a, e) / l2, 0f, 1f) : 0f;
                return math.distance(q, a + (t * e));
            }

            /// <summary>0: wholly on its side, the margin kept; 1: not wholly on its side; 2: on its side, nearer than the margin.</summary>
            internal int Verdict(List<float2> points, bool positive, float margin)
            {
                float sign = positive ? 1f : -1f;
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                foreach (float2 p in points)
                {
                    if (!(sign * (p.y - HeightAt(p.x)) > 0f)) return 1;
                    lo = math.min(lo, p.x);
                    hi = math.max(hi, p.x);
                }

                int n = points.Count;
                foreach (float2 knot in _knots)
                {
                    if (knot.x < lo || knot.x > hi) continue;
                    float extreme = positive ? float.PositiveInfinity : float.NegativeInfinity;
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = i; j < n; j++)
                        {
                            float2 a = points[i], b = points[j];
                            if ((a.x - knot.x) * (b.x - knot.x) > 0f) continue;
                            if (a.x == b.x)
                            {
                                extreme = positive ? math.min(extreme, math.min(a.y, b.y)) : math.max(extreme, math.max(a.y, b.y));
                                continue;
                            }

                            float h = a.y + ((knot.x - a.x) / (b.x - a.x) * (b.y - a.y));
                            extreme = positive ? math.min(extreme, h) : math.max(extreme, h);
                        }
                    }

                    if (!(sign * (extreme - knot.y) > 0f)) return 1;
                }

                float least = float.PositiveInfinity;
                foreach (float2 p in points) least = math.min(least, Distance(p));
                foreach (float2 knot in _knots)
                {
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = i; j < n; j++) least = math.min(least, ToSegment(knot, points[i], points[j]));
                    }
                }

                return least < margin ? 2 : 0;
            }
        }

        // The checks of what was shared out, independent of how it was found: every input triangle on exactly one side, in
        // its submesh and order; each triangle and each convex the classification put on a side wholly on it with the
        // margin -- against the plane vertex by vertex (a plane is linear), against a bent surface over the element's whole
        // extent (BentCheck). Counted by element.
        internal static void Check(CutSpatialSplit.Attempt a, SpatialSplitReplayResult result)
        {
            if (a.search.outcome != SpatialSplitSearchOutcome.Found || !a.distributed)
            {
                result.checkNote = "nothing shared out";
                return;
            }

            result.checkedSharing = true;
            bool bent = CutSpatialSplit.IsBentResult(in a.search);
            int s = a.submeshCount;
            int positiveTotal = a.distribution.positiveIndexCount;
            float4 plane = a.search.planeGeometry;
            BentCheck surface = bent ? new BentCheck(in a.search.bentSurface) : null;
            var points = new List<float2>(128);
            for (int sub = 0; sub < s; sub++)
            {
                int p = a.outSubmeshOffsets[sub];
                int pEnd = p + a.outSubmeshCounts[sub];
                int q = positiveTotal + a.outSubmeshOffsets[s + sub];
                int qEnd = q + a.outSubmeshCounts[s + sub];
                int end = a.submeshOffsets[sub] + a.submeshCounts[sub];
                for (int i = a.submeshOffsets[sub]; i + 2 < end; i += 3)
                {
                    bool positive;
                    if (p < pEnd && Same(a.output + p, a.indices + i))
                    {
                        positive = true;
                        p += 3;
                    }
                    else if (q < qEnd && Same(a.output + q, a.indices + i))
                    {
                        positive = false;
                        q += 3;
                    }
                    else
                    {
                        result.trianglesMissing++;
                        continue;
                    }

                    int verdict = 0;
                    if (bent)
                    {
                        points.Clear();
                        for (int k = 0; k < 3; k++) points.Add(surface.Project(math.transform(a.geometryToOwner, a.vertices[a.indices[i + k]].position)));
                        verdict = surface.Verdict(points, positive, a.margin);
                    }
                    else
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            float d = math.dot(plane.xyz, a.vertices[a.indices[i + k]].position) + plane.w;
                            if (positive ? d <= 0f : d >= 0f) verdict = 1;
                            else if (math.abs(d) <= a.margin && verdict == 0) verdict = 2;
                        }
                    }

                    if (verdict == 1) result.triangleWrongSide++;
                    else if (verdict == 2) result.triangleMarginShort++;
                }

                result.trianglesExtra += ((pEnd - p) + (qEnd - q)) / 3;
            }

            PhysicsCutClassification classification = a.classification;
            if (classification == null || classification.Sides == null)
            {
                result.checkNote = "no classification to check";
                return;
            }

            float4 shapePlane = a.search.planeShape / math.length(a.search.planeShape.xyz);
            float4 ownerPlane = a.search.planeOwner;
            bool wholeConvexes = !bent && !a.search.meshLed;
            for (int c = 0; c < a.convexCount; c++)
            {
                ConvexSide side = classification.Sides[c];
                if (side != ConvexSide.Positive && side != ConvexSide.Negative) continue;
                ConvexBrepRange r = a.ranges[c];
                ConvexBrepBank b = a.banks[c];
                bool positive = side == ConvexSide.Positive;
                int verdict = 0;
                if (bent)
                {
                    points.Clear();
                    for (int k = 0; k < r.vertexCount; k++) points.Add(surface.Project(math.transform(a.shapeToOwner, b.vertices[r.vertexBase + k])));
                    verdict = surface.Verdict(points, positive, a.margin);
                }
                else
                {
                    // How far the convex reaches onto the other side (0 where it does not), in the shape's frame as the
                    // cut's scan measures it. A plane that leaves the convexes whole keeps every vertex beyond the margin:
                    // any reach across is wrong. A mesh-led plane's sides are the cut's scan's (DESIGN 7.6): a vertex
                    // within the support epsilon is no support of either side, so only a reach beyond it is wrong.
                    float across = 0f;
                    for (int k = 0; k < r.vertexCount; k++)
                    {
                        float3 point = b.vertices[r.vertexBase + k];
                        float d = math.dot(shapePlane.xyz, point) + shapePlane.w;
                        across = math.max(across, positive ? -d : d);
                        if ((positive ? d <= 0f : d >= 0f) && wholeConvexes) verdict = 1;
                        float owner = math.dot(ownerPlane.xyz, math.transform(a.shapeToOwner, point)) + ownerPlane.w;
                        if (wholeConvexes && math.abs(owner) <= a.margin && verdict == 0) verdict = 2;
                    }

                    if (!wholeConvexes && across > 0f)
                    {
                        result.convexAcrossWithinEpsilon += across <= a.supportEpsilon ? 1 : 0;
                        if (across > a.supportEpsilon) verdict = 1;
                    }

                    result.convexMostAcross = math.max(result.convexMostAcross, across);
                }

                if (verdict == 1) result.convexWrongSide++;
                else if (verdict == 2) result.convexMarginShort++;
            }

            result.checkNote = bent ? "bent: every triangle and convex over its whole extent, with the margin"
                : a.search.meshLed ? "mesh-led: a convex on a side may reach across within the cut's support epsilon (DESIGN 7.6); cut ones and those within it of the plane are not checked"
                : "plane";
        }

        private static bool Same(uint* a, uint* b) => a[0] == b[0] && a[1] == b[1] && a[2] == b[2];

        // The graph placement's sharing out (GRAPH-CONNECT-6 §2 P1-3): no plane or surface stands for it, so the checks are the
        // combinatorial ones -- every input triangle once, on the side the engine gave its component, in its submesh and order;
        // every convex classified on the side the engine gave it -- and nothing geometric; the placement's geometry is judged
        // offline against the exact-checked placements (or by the reference decider's VerifyPlacement).
        internal static void CheckGraph(CutSpatialSplit.Attempt a, SpatialSplitReplayResult result)
        {
            if (!a.graphLed || !a.graphSides.IsCreated)
            {
                result.checkNote = "graph: not led by the graph stage";
                return;
            }

            if (!a.distributed)
            {
                result.checkNote = "graph: INCOMPLETE -- nothing shared out (the distribution by the given sides refused)";
                return;
            }

            result.checkedSharing = true;
            byte* sides = (byte*)a.graphSides.GetUnsafePtr();
            int s = a.submeshCount;
            int positiveTotal = a.distribution.positiveIndexCount;
            for (int sub = 0; sub < s; sub++)
            {
                int p = a.outSubmeshOffsets[sub];
                int pEnd = p + a.outSubmeshCounts[sub];
                int q = positiveTotal + a.outSubmeshOffsets[s + sub];
                int qEnd = q + a.outSubmeshCounts[s + sub];
                int end = a.submeshOffsets[sub] + a.submeshCounts[sub];
                for (int i = a.submeshOffsets[sub]; i + 2 < end; i += 3)
                {
                    bool positive;
                    if (p < pEnd && Same(a.output + p, a.indices + i)) { positive = true; p += 3; }
                    else if (q < qEnd && Same(a.output + q, a.indices + i)) { positive = false; q += 3; }
                    else
                    {
                        // not next on either side: lost, or out of its order (told apart by a search of the whole side)
                        bool anywhere = false;
                        for (int k = a.outSubmeshOffsets[sub]; k < pEnd && !anywhere; k += 3) anywhere = Same(a.output + k, a.indices + i);
                        for (int k = positiveTotal + a.outSubmeshOffsets[s + sub]; k < qEnd && !anywhere; k += 3) anywhere = Same(a.output + k, a.indices + i);
                        if (anywhere) result.graphTriangleOrderBroken++; else result.graphTrianglesMissing++;
                        continue;
                    }

                    bool given = sides[a.convexCount + (i / 3)] != 0;
                    if (given != positive) result.graphTriangleWrongSide++;
                }

                result.graphTrianglesExtra += ((pEnd - p) + (qEnd - q)) / 3;
            }

            PhysicsCutClassification classification = a.classification;
            if (classification == null || classification.Sides == null)
            {
                result.checkNote = "graph: INCOMPLETE -- triangles checked; no classification (the anchors or the classification refused)";
                return;
            }

            for (int cv = 0; cv < a.convexCount; cv++)
            {
                ConvexSide side = classification.Sides[cv];
                if (side != ConvexSide.Positive && side != ConvexSide.Negative) { result.graphConvexNotSided++; continue; }
                // the sides' bytes hold ConvexSide for the convexes after BeginGraph
                if ((byte)side != sides[cv]) result.graphConvexWrongSide++;
            }

            result.checkNote = "graph: every triangle once on the side the engine gave its component, in its submesh and order; every convex on the side the engine gave it; no plane or surface stands for the placement (its geometry is judged offline)";
        }
    }
}
#endif
