#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Unity.Mathematics;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// What a bone-aware attempt's decision read beside the sample (NPC-BONE-EVAL-1; development and the Editor only): the
    /// convexes' bones, the lineage's rig (parents, presence, joints), the cuts' connections taken off, the base vertices' bones
    /// and every cut's vertex origins -- as the issue copied them, never inferred afterwards. Written beside the sample file
    /// (<see cref="SpatialSplitCaptureSink.SampleFileName"/> with .zsb.gz), on the main thread at the issue, only when the
    /// launch names <see cref="SpatialSplitCaptureSession.BoneArgument"/> (the capture's own files are as before). Also the
    /// settings the geometric searches would have run with for that fragment (the graph's switch, order and limits), so that
    /// the same input can be given to both methods: the issue's own values, not a guess.
    /// </summary>
    /// <summary>
    /// One write of the bones' capture for the writer thread (NPC-BONE-EVAL-1 review 1): a sidecar's content (gzip, made on Main) with
    /// its index line, or a line alone; written after the session's folder was opened, its room reserved in the queue until then.
    /// </summary>
    internal sealed class SpatialSplitBoneItem
    {
        internal string file, index, line;
        internal byte[] content;
        internal long bytes;
    }

    internal sealed class SpatialSplitBoneSample
    {
        internal const string Magic = "ZTKSBON1", End = "END!";
        /// <summary>2 (NPC-BONE-ACTIVE-2): the fragment's part of the rig in place of the cut-edge bits of 1, which this does not read.</summary>
        internal const int FormatVersion = 2;

        internal string sessionId, sha256;
        internal long world, attempt;
        internal int fragment, epoch;
        internal bool graphSearch, graphFirst, graphPrepared;
        internal SpatialSplitGraphLimits graphLimits;
        internal int[] convexBones;
        internal string insufficient;
        internal long maxOriginSteps;
        internal int nodeCount;
        internal int[] parent;
        internal bool[] present;
        internal float3[] joints;
        internal bool[] piecePresent;
        internal ulong[] jointKept;
        internal float[] segmentLo, segmentHi;
        internal uint baseStart;
        internal int baseCount;
        internal ulong[] baseBones;   // null: the lineage had none
        internal int localCount;
        internal uint[] chunkStarts;
        internal int[] chunkLocalBases;
        internal uint2[][] chunkOrigins;

        internal static string FileName(long world, long attempt) =>
            "w" + world.ToString("D3", CultureInfo.InvariantCulture) + "-a" + attempt.ToString("D7", CultureInfo.InvariantCulture) + ".zsb.gz";

        internal static SpatialSplitBoneSample Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var r = new SpatialSplitHashReader(gzip))
            {
                var s = new SpatialSplitBoneSample();
                r.Expect(Magic);
                int version = r.Int();
                if (version != FormatVersion) throw new InvalidDataException("bone format " + version + ", this reads " + FormatVersion);
                s.sessionId = r.String();
                s.world = r.Long();
                s.attempt = r.Long();
                s.fragment = r.Int();
                s.epoch = r.Int();
                s.graphSearch = r.Bool();
                s.graphFirst = r.Bool();
                s.graphPrepared = r.Bool();
                int maxTriangles = r.Int(), maxConvexes = r.Int(), maxVertexSpan = r.Int(), maxNodes = r.Int(), maxCandidates = r.Int();
                long maxGraphBytes = r.Long(), workBudget = r.Long();
                int deferFirst = r.Int(), deferMax = r.Int();
                s.graphLimits = new SpatialSplitGraphLimits(maxTriangles, maxConvexes, maxVertexSpan, maxNodes, maxCandidates, maxGraphBytes, workBudget, deferFirst, deferMax);
                s.convexBones = r.Array<int>(r.Count("convex", 1 << 20));
                s.insufficient = r.Bool() ? r.String() : null;
                s.maxOriginSteps = r.Long();
                s.nodeCount = r.Count("bone", 1 << 16);
                s.parent = r.Array<int>(s.nodeCount);
                byte[] present = r.Array<byte>(s.nodeCount);
                s.present = new bool[s.nodeCount];
                for (int i = 0; i < s.nodeCount; i++) s.present[i] = present[i] != 0;
                s.joints = r.Array<float3>(r.Count("joint", 1 << 16));
                byte[] held = r.Array<byte>(r.Count("held bone", 1 << 16));
                s.piecePresent = new bool[held.Length];
                for (int i = 0; i < held.Length; i++) s.piecePresent[i] = held[i] != 0;
                s.jointKept = r.Array<ulong>(r.Count("kept joint word", 1 << 12));
                s.segmentLo = r.Array<float>(r.Count("segment", 1 << 16));
                s.segmentHi = r.Array<float>(r.Count("segment", 1 << 16));
                s.baseStart = r.UInt();
                s.baseCount = r.Count("base vertex", int.MaxValue);
                s.baseBones = r.Bool() ? r.Array<ulong>(s.baseCount) : null;
                s.localCount = r.Count("lineage vertex", int.MaxValue);
                int chunks = r.Count("chunk", 1 << 20);
                s.chunkStarts = new uint[chunks];
                s.chunkLocalBases = new int[chunks];
                s.chunkOrigins = new uint2[chunks][];
                for (int c = 0; c < chunks; c++)
                {
                    s.chunkStarts[c] = r.UInt();
                    s.chunkLocalBases[c] = r.Int();
                    s.chunkOrigins[c] = r.Array<uint2>(r.Count("origin", int.MaxValue));
                }

                r.Expect(End);
                s.sha256 = r.Finish();
                return s;
            }
        }

        /// <summary>
        /// The sidecar of one bone-aware attempt whose sample was captured (the issue, the main thread): its content made here, in
        /// memory, and handed to the capture's writer with its line for bones.jsonl -- the writer writes both after it opened the
        /// session's folder (nothing here makes or writes in that folder: NPC-BONE-EVAL-1 review 1). False where it was not handed
        /// (the queue's room refused, or the content could not be made): said, and the sample stays as it is (the comparison then
        /// refuses that sample for want of its bones).
        /// </summary>
        internal static unsafe bool Write(SpatialSplitCaptureSink sink, long world, CutSpatialSplit.Attempt a, NpcBonePiece piece, in CutSpatialSplitSettings settings, bool graphPrepared)
        {
            NpcBoneLineage lineage = a.boneLineage;
            string name = FileName(world, a.serial);
            try
            {
                string hash;
                long raw;
                byte[] content;
                int[] bones = a.boneInput.convexBones ?? Array.Empty<int>();
                NpcVertexChunk[] chunks = a.boneInput.lineage.chunks ?? Array.Empty<NpcVertexChunk>();
                int n = lineage.NodeCount;
                using (var memory = new MemoryStream())
                {
                    using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true))
                    using (var w = new SpatialSplitHashWriter(gzip, new byte[1 << 16]))
                    {
                        w.Ascii(Magic);
                        w.Int(FormatVersion);
                        w.String(sink.sessionId);
                        w.Long(world);
                        w.Long(a.serial);
                        w.Int(a.fragment.value);
                        w.Int(a.input.epoch);
                        w.Byte(settings.graphSearch);
                        w.Byte(settings.graphFirst);
                        w.Byte(graphPrepared);
                        SpatialSplitGraphLimits g = settings.graphLimits;
                        w.Int(g.maxTriangles); w.Int(g.maxConvexes); w.Int(g.maxVertexSpan); w.Int(g.maxNodes); w.Int(g.maxCandidates);
                        w.Long(g.maxGraphBytes); w.Long(g.workBudget); w.Int(g.deferFirstFrames); w.Int(g.deferMaxFrames);
                        w.Int(bones.Length);
                        fixed (int* p = bones) w.Bytes(p, (long)bones.Length * sizeof(int));
                        w.Byte(a.boneInput.insufficient != null);
                        if (a.boneInput.insufficient != null) w.String(a.boneInput.insufficient);
                        w.Long(a.boneInput.maxOriginSteps);
                        w.Int(n);
                        fixed (int* p = lineage.Parent) w.Bytes(p, (long)n * sizeof(int));
                        for (int i = 0; i < n; i++) w.Byte(lineage.Present[i]);
                        float3[] joints = lineage.Joints ?? Array.Empty<float3>();
                        w.Int(joints.Length);
                        fixed (float3* p = joints) w.Bytes(p, (long)joints.Length * sizeof(float3));
                        bool[] held = piece != null ? piece.Present : Array.Empty<bool>();
                        w.Int(held.Length);
                        for (int i = 0; i < held.Length; i++) w.Byte(held[i]);
                        ulong[] kept = piece != null ? piece.JointKept : Array.Empty<ulong>();
                        w.Int(kept.Length);
                        fixed (ulong* p = kept) w.Bytes(p, (long)kept.Length * sizeof(ulong));
                        float[] lo = piece != null ? piece.Lo : Array.Empty<float>();
                        float[] hi = piece != null ? piece.Hi : Array.Empty<float>();
                        w.Int(lo.Length);
                        fixed (float* p = lo) w.Bytes(p, (long)lo.Length * sizeof(float));
                        w.Int(hi.Length);
                        fixed (float* p = hi) w.Bytes(p, (long)hi.Length * sizeof(float));
                        w.UInt(lineage.BaseStart);
                        w.Int(lineage.BaseCount);
                        ulong* baseBones = a.boneInput.lineage.baseBones;
                        w.Byte(baseBones != null);
                        if (baseBones != null) w.Bytes(baseBones, (long)lineage.BaseCount * sizeof(ulong));
                        w.Int(a.boneInput.lineage.localCount);
                        w.Int(chunks.Length);
                        for (int c = 0; c < chunks.Length; c++)
                        {
                            w.UInt(chunks[c].start);
                            w.Int(chunks[c].localBase);
                            w.Int(chunks[c].count);
                            w.Bytes(chunks[c].origins, (long)chunks[c].count * sizeof(uint2));
                        }

                        w.Ascii(End);
                        hash = w.Finish();
                        raw = w.Length;
                    }

                    content = memory.ToArray();
                }

                var item = new SpatialSplitBoneItem
                {
                    file = name,
                    content = content,
                    index = "bones.jsonl",
                    line = "{\"world\":" + world + ",\"attempt\":" + a.serial + ",\"file\":\"" + name + "\",\"sha256\":\"" + hash + "\",\"rawBytes\":" + raw + ",\"fileBytes\":" + content.Length
                        + ",\"fragment\":" + a.fragment.value + ",\"convexes\":" + bones.Length + ",\"bones\":" + n + ",\"chunks\":" + chunks.Length
                        + ",\"localCount\":" + a.boneInput.lineage.localCount + ",\"insufficient\":" + (a.boneInput.insufficient != null ? "true" : "false") + "}",
                };
                item.bytes = content.Length + SpatialSplitCaptureResult.Overhead;
                if (!sink.TryReserve(item.bytes, false))
                {
                    UnityEngine.Debug.LogError("SPLIT CAPTURE BONE: the sidecar " + name + " was not handed to the writer: no room (" + sink.LastRefusal + ")");
                    return false;
                }

                sink.HandBone(item);
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("SPLIT CAPTURE BONE: the sidecar " + name + " was not made: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// The live decision of a captured bone-aware attempt (the main thread, where it is counted): its line in bone-results.jsonl,
        /// handed to the capture's writer as the sidecar is.
        /// </summary>
        internal static void WriteResult(SpatialSplitCaptureSink sink, long world, long attempt, in NpcBoneSplitRecord record, string sidesHash, string end)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var item = new SpatialSplitBoneItem
            {
                index = "bone-results.jsonl",
                line = "{\"world\":" + world + ",\"attempt\":" + attempt + ",\"verdict\":\"" + record.verdict + "\",\"reason\":" + SpatialSplitCaptureSink.Json(record.reason)
                    + ",\"bones\":" + record.bones + ",\"clusters\":" + record.clusters + ",\"meshComponents\":" + record.meshComponents + ",\"groups\":" + record.groups
                    + ",\"fullGroups\":" + record.fullGroups + ",\"referencedVertices\":" + record.referencedVertices + ",\"originSteps\":" + record.originSteps
                    + ",\"positiveConvexes\":" + record.positiveConvexes + ",\"negativeConvexes\":" + record.negativeConvexes
                    + ",\"positiveTriangles\":" + record.positiveTriangles + ",\"negativeTriangles\":" + record.negativeTriangles
                    + ",\"sidesHash\":\"" + (sidesHash ?? "") + "\",\"end\":\"" + (end ?? "") + "\",\"seconds\":" + record.seconds.ToString("R", inv) + "}",
                bytes = SpatialSplitCaptureResult.Overhead,
            };
            if (!sink.TryReserve(item.bytes, true))
            {
                UnityEngine.Debug.LogError("SPLIT CAPTURE BONE: the result line of attempt " + attempt + " was not handed to the writer: no room (" + sink.LastRefusal + ")");
                return;
            }

            sink.HandBone(item);
        }
    }
}
#endif
