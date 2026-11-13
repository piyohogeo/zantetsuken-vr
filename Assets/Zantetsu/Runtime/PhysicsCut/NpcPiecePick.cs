#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// NPC-PIECE-PICK-1 (development and the Editor only): the collection, during ordinary play, of pieces the person wants apart that the
    /// additional split leaves together -- the material for looking at the bone-aware split's conditions later. One pick is the piece the
    /// left controller's line rests on when the left X button is pressed (<c>Sandbox.PieceCollector</c>), saved as it is at that moment:
    /// <list type="bullet">
    /// <item>its whole mesh, convexes and frames as the issued inputs' capture saves an attempt's input (<see cref="SpatialSplitCaptureSample"/>,
    /// the .zss.gz file and its samples.jsonl line; world = the split's world, attempt = the pick's number);</item>
    /// <item>its bones as the capture's sidecar saves them (<see cref="SpatialSplitBoneSample"/>: the convexes' bones, the rig, the joints, the
    /// piece's part of the rig, the base vertices' bones and every cut's vertex origins; .zsb.gz and bones.jsonl);</item>
    /// <item>what is the pick's own (<see cref="NpcPiecePickSample"/>, .zsp.gz and picks.jsonl): who and when, the generation, where the line
    /// pointed (the world, the owner's and the geometry's frames), the owner's pose, the split's last note on the piece with the generation
    /// that note was of, and the model's weight table the piece's base vertices read;</item>
    /// <item>once a model: its vertices' bones and weights as floats (<see cref="NpcModelWeightTable"/>, .zsw.gz and models.jsonl).</item>
    /// </list>
    /// Everything goes through the capture's own sink (<see cref="SpatialSplitCaptureSink"/>: its queue, its writer thread, its temporary
    /// name and move, its index lines), a sink of the collection's own in a folder of its own; the issued inputs' capture is not turned
    /// on and nothing of the split's selection, searches or publication changes. The copy is a read of the main thread in the frame of the
    /// press, under the same conditions an issue reads a fragment (the current target, no cut under way, the owner and shape live, the
    /// geometry settled, the indices leased), and the geometry's identity is read again after it: a piece that changed or went is a pick
    /// that failed, said as such and counted.
    /// </summary>
    internal static class NpcPiecePick
    {
        public const string Prefix = "PIECE COLLECT: ";
    }

    /// <summary>What a stored geometry is, as far as a pick can tell one from the next one of the same fragment (the Input's own fields).</summary>
    internal struct NpcPieceGeometryIdentity : IEquatable<NpcPieceGeometryIdentity>
    {
        internal int vertexStart, vertexCount, topologyVertexCount, submeshStart, submeshCount, blockStart, blockCount;

        internal static NpcPieceGeometryIdentity Of(in VpStoredGeometry g) => new NpcPieceGeometryIdentity
        {
            vertexStart = g.vertexStart, vertexCount = g.vertexCount, topologyVertexCount = g.topologyVertexCount, submeshStart = g.submeshStart,
            submeshCount = g.submeshCount, blockStart = g.blockStart, blockCount = g.blockCount,
        };

        public bool Equals(NpcPieceGeometryIdentity o) =>
            vertexStart == o.vertexStart && vertexCount == o.vertexCount && topologyVertexCount == o.topologyVertexCount && submeshStart == o.submeshStart
            && submeshCount == o.submeshCount && blockStart == o.blockStart && blockCount == o.blockCount;

        public override string ToString() => "vertices " + vertexStart + "+" + vertexCount + " (topology " + topologyVertexCount + "), submeshes " + submeshStart + "+" + submeshCount
            + ", blocks " + blockStart + "+" + blockCount;

        internal void Write(SpatialSplitHashWriter w)
        {
            w.Int(vertexStart); w.Int(vertexCount); w.Int(topologyVertexCount); w.Int(submeshStart); w.Int(submeshCount); w.Int(blockStart); w.Int(blockCount);
        }

        internal static NpcPieceGeometryIdentity Read(SpatialSplitHashReader r) => new NpcPieceGeometryIdentity
        {
            vertexStart = r.Int(), vertexCount = r.Int(), topologyVertexCount = r.Int(), submeshStart = r.Int(), submeshCount = r.Int(), blockStart = r.Int(), blockCount = r.Int(),
        };
    }

    /// <summary>
    /// One model's vertices' bones and weights (NPC-PIECE-PICK-1), as the model's table holds them in the collection's mode: for every
    /// vertex of the model's mesh, four palette numbers (<see cref="NpcVertexBones.None"/> where there is none) and, in the same places, the
    /// weights as the mesh holds them -- floats, every bit; a weight of exactly zero is "a place the vertex holds without using it" and is not
    /// kept, as the bones' table never kept its bone. **These are the model's own vertices' weights**: a vertex a cut made has none of its
    /// own here -- the decision walks it back to the base vertices it was interpolated from (the sidecar's origins) and reads theirs. What an
    /// interpolated weight would need and this format does not hold: the interpolation parameter along the cut edge (the origins name the
    /// two base vertices only; the cut's plane and both vertices' positions at the time of the cut would give it, and the sample holds the
    /// positions of the vertices its indices name, not of every origin). Written once a model a session, named by the pick that first needed it.
    /// </summary>
    internal sealed class NpcModelWeightTable
    {
        internal const string Magic = "ZTKSWGT1", End = "END!";
        internal const int FormatVersion = 1;

        internal string sessionId, modelName, sha256;
        internal int vertexCount, paletteCount;
        internal bool hasWeights;
        internal ulong[] bones;
        internal float[] weights;   // 4 a vertex; null where the table kept none

        internal static string FileName(int model) => "model-" + model.ToString("D3", CultureInfo.InvariantCulture) + ".zsw.gz";

        /// <summary>The table's bytes (gzip) and the hash of its content before compression, from a table of bones (and weights where kept).</summary>
        internal static unsafe byte[] Make(string sessionId, NpcVertexBones table, out string hash, out long raw)
        {
            using (var memory = new MemoryStream())
            {
                using (var gzip = new GZipStream(memory, System.IO.Compression.CompressionLevel.Fastest, true))
                using (var w = new SpatialSplitHashWriter(gzip, new byte[1 << 16]))
                {
                    w.Ascii(Magic);
                    w.Int(FormatVersion);
                    w.String(sessionId);
                    w.String(table.ModelName);
                    int n = table.VertexCount;
                    w.Int(n);
                    w.Int(table.PaletteCount);
                    w.Byte(table.HasWeights);
                    w.Bytes(table.Pointer, (long)n * sizeof(ulong));
                    if (table.HasWeights) w.Bytes(table.WeightsPointer, 4L * n * sizeof(float));
                    w.Ascii(End);
                    hash = w.Finish();
                    raw = w.Length;
                }

                return memory.ToArray();
            }
        }

        internal static NpcModelWeightTable Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var r = new SpatialSplitHashReader(gzip))
            {
                var t = new NpcModelWeightTable();
                r.Expect(Magic);
                int version = r.Int();
                if (version != FormatVersion) throw new InvalidDataException("weight table format " + version + ", this reads " + FormatVersion);
                t.sessionId = r.String();
                t.modelName = r.String();
                t.vertexCount = r.Count("vertex", 1 << 26);
                t.paletteCount = r.Count("palette bone", 1 << 16);
                t.hasWeights = r.Bool();
                t.bones = r.Array<ulong>(t.vertexCount);
                t.weights = t.hasWeights ? r.Array<float>(4 * t.vertexCount) : null;
                r.Expect(End);
                t.sha256 = r.Finish();
                return t;
            }
        }

        /// <summary>Null where every kept weight has a bone and every bone a positive finite weight (where weights are kept); else what does not hold.</summary>
        internal string Check()
        {
            if (bones == null || bones.Length != vertexCount) return "bones of " + (bones != null ? bones.Length : 0) + " vertices, not " + vertexCount;
            if (!hasWeights) return null;
            if (weights == null || weights.Length != 4 * vertexCount) return "weights of " + (weights != null ? weights.Length / 4 : 0) + " vertices, not " + vertexCount;
            for (int v = 0; v < vertexCount; v++)
            {
                for (int q = 0; q < 4; q++)
                {
                    bool bone = ((bones[v] >> (16 * q)) & 0xFFFF) != NpcVertexBones.None;
                    float w = weights[(4 * v) + q];
                    if (bone && !(w > 0f && float.IsFinite(w))) return "vertex " + v + " influence " + q + " has a bone and a weight of " + w.ToString("R", CultureInfo.InvariantCulture);
                    if (!bone && w != 0f) return "vertex " + v + " influence " + q + " has a weight and no bone";
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The picked geometry's own topology table (NPC-PIECE-TOPOLOGY-1, 2026-11-13; .zst.gz and topologies.jsonl): for every render
    /// vertex the geometry's indices name -- of whichever vertex block, the base's, a cut's, a cap's auxiliary ones -- the topology
    /// vertex the storage holds for it in this geometry, read through the map the cut kernel and the bone-aware split's decision
    /// read (<see cref="NpcBoneTopology.TryOfGeometry"/>, <see cref="RenderCutTopologyMap.TopologyOf"/>) while the pick holds the
    /// geometry's index lease. Nothing is worked out from positions or origins, when it is written or when it is read: a pick read
    /// back is decided with this table as it stands. The vertex ids are the storage's, ascending, each once.
    /// </summary>
    internal sealed unsafe class NpcPieceTopologySample
    {
        internal const string Magic = "ZTKSTPL1", End = "END!", Index = "topologies.jsonl";
        internal const int FormatVersion = 1;

        internal string sessionId, sha256;
        internal long world;
        internal int pick, fragment, epoch;
        internal NpcPieceGeometryIdentity geometry;
        internal int topologyVertexCount;
        internal int[] blockStarts, blockCounts;   // the geometry's vertex blocks, in its order
        internal uint[] vertexIds;                 // every render vertex the indices name: ascending, each once
        internal int[] topologyIds;                // its topology vertex, in the same places

        internal static string FileName(long world, long pick) =>
            "w" + world.ToString("D3", CultureInfo.InvariantCulture) + "-a" + pick.ToString("D7", CultureInfo.InvariantCulture) + ".zst.gz";

        /// <summary>
        /// The table of <paramref name="geometry"/> for the vertices <paramref name="indices"/> name; null with <paramref name="why"/> where
        /// the geometry gives no mapping or a vertex is of none of its blocks. The caller holds the geometry's index lease.
        /// </summary>
        internal static NpcPieceTopologySample Take(VpCpuGeometryStorage storage, VpStoredGeometry geometry, uint* indices, int indexCount, out string why)
        {
            if (!NpcBoneTopology.TryOfGeometry(storage, geometry, out NpcBoneTopology topology, out why)) return null;
            try
            {
                if (!storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out int count)) { why = "the geometry's vertex blocks are not readable"; return null; }
                var made = new NpcPieceTopologySample
                {
                    geometry = NpcPieceGeometryIdentity.Of(in geometry), topologyVertexCount = count, blockStarts = new int[blocks.Length], blockCounts = new int[blocks.Length],
                };
                for (int b = 0; b < blocks.Length; b++)
                {
                    made.blockStarts[b] = blocks[b].vertexStart;
                    made.blockCounts[b] = blocks[b].vertexCount;
                }

                var ids = new uint[Math.Max(0, indexCount)];
                for (int i = 0; i < ids.Length; i++) ids[i] = indices[i];
                Array.Sort(ids);
                int n = 0;
                for (int i = 0; i < ids.Length; i++)
                {
                    if (i == 0 || ids[i] != ids[i - 1]) ids[n++] = ids[i];
                }

                Array.Resize(ref ids, n);
                var of = new int[n];
                RenderCutTopologyMap map = topology.Map;
                int last = 0;
                for (int i = 0; i < n; i++)
                {
                    int t = RenderCutTopologyMap.TopologyOf(in map, ids[i], ref last);
                    if ((uint)t >= (uint)count) { why = "vertex " + ids[i] + " has no topology vertex in the geometry's blocks (" + t + " of " + count + ")"; return null; }
                    of[i] = t;
                }

                made.vertexIds = ids;
                made.topologyIds = of;
                return made;
            }
            finally
            {
                topology.Dispose();
            }
        }

        /// <summary>The topology vertex saved for a storage vertex id; -1 where the table has none.</summary>
        internal int TopologyOf(uint id)
        {
            int at = Array.BinarySearch(vertexIds, id);
            return at >= 0 ? topologyIds[at] : -1;
        }

        /// <summary>What is wrong with the table as read, or null: ids ascending and each once, inside a block, topology ids inside the count.</summary>
        internal string Check()
        {
            if (vertexIds.Length != topologyIds.Length) return "the two columns differ in length";
            if (blockStarts.Length != blockCounts.Length || blockStarts.Length != geometry.blockCount) return "the vertex blocks are not the geometry's " + geometry.blockCount;
            if (topologyVertexCount != geometry.topologyVertexCount) return "the topology vertex count " + topologyVertexCount + " is not the geometry's " + geometry.topologyVertexCount;
            for (int i = 0; i < vertexIds.Length; i++)
            {
                if (i > 0 && vertexIds[i] <= vertexIds[i - 1]) return "vertex ids not ascending at " + i;
                if ((uint)topologyIds[i] >= (uint)topologyVertexCount) return "vertex " + vertexIds[i] + " has topology vertex " + topologyIds[i] + " of " + topologyVertexCount;
                bool inside = false;
                for (int b = 0; b < blockStarts.Length && !inside; b++) inside = vertexIds[i] >= (uint)blockStarts[b] && vertexIds[i] - (uint)blockStarts[b] < (uint)blockCounts[b];
                if (!inside) return "vertex " + vertexIds[i] + " is in none of the geometry's vertex blocks";
            }

            return null;
        }

        internal byte[] Make(out string hash, out long raw)
        {
            using (var memory = new MemoryStream())
            {
                using (var gzip = new GZipStream(memory, System.IO.Compression.CompressionLevel.Fastest, true))
                using (var w = new SpatialSplitHashWriter(gzip, new byte[1 << 16]))
                {
                    w.Ascii(Magic);
                    w.Int(FormatVersion);
                    w.String(sessionId);
                    w.Long(world); w.Int(pick); w.Int(fragment); w.Int(epoch);
                    geometry.Write(w);
                    w.Int(topologyVertexCount);
                    w.Int(blockStarts.Length);
                    for (int b = 0; b < blockStarts.Length; b++) { w.Int(blockStarts[b]); w.Int(blockCounts[b]); }
                    w.Int(vertexIds.Length);
                    if (vertexIds.Length > 0)
                    {
                        fixed (uint* ids = vertexIds) w.Bytes(ids, (long)vertexIds.Length * sizeof(uint));
                        fixed (int* of = topologyIds) w.Bytes(of, (long)topologyIds.Length * sizeof(int));
                    }

                    w.Ascii(End);
                    hash = w.Finish();
                    raw = w.Length;
                }

                return memory.ToArray();
            }
        }

        internal static NpcPieceTopologySample Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var r = new SpatialSplitHashReader(gzip))
            {
                var s = new NpcPieceTopologySample();
                r.Expect(Magic);
                int version = r.Int();
                if (version != FormatVersion) throw new InvalidDataException("topology format " + version + ", this reads " + FormatVersion);
                s.sessionId = r.String();
                s.world = r.Long(); s.pick = r.Int(); s.fragment = r.Int(); s.epoch = r.Int();
                s.geometry = NpcPieceGeometryIdentity.Read(r);
                s.topologyVertexCount = r.Int();
                int blocks = r.Count("vertex block", 1 << 24);
                s.blockStarts = new int[blocks];
                s.blockCounts = new int[blocks];
                for (int b = 0; b < blocks; b++) { s.blockStarts[b] = r.Int(); s.blockCounts[b] = r.Int(); }
                int n = r.Count("topology entry", 1 << 28);
                s.vertexIds = r.Array<uint>(n);
                s.topologyIds = r.Array<int>(n);
                r.Expect(End);
                s.sha256 = r.Finish();
                return s;
            }
        }

        /// <summary>The topologies.jsonl line.</summary>
        internal string Line(string file, string hash, long raw, int fileBytes) =>
            "{\"world\":" + world + ",\"attempt\":" + pick + ",\"pick\":" + pick + ",\"file\":" + SpatialSplitCaptureSink.Json(file) + ",\"sha256\":" + SpatialSplitCaptureSink.Json(hash)
            + ",\"rawBytes\":" + raw + ",\"fileBytes\":" + fileBytes + ",\"format\":" + FormatVersion + ",\"fragment\":" + fragment + ",\"epoch\":" + epoch
            + ",\"topologyVertices\":" + topologyVertexCount + ",\"vertices\":" + vertexIds.Length + ",\"blocks\":" + blockStarts.Length + "}";
    }

    /// <summary>
    /// The pick's own record (NPC-PIECE-PICK-1): who, when, where the line pointed, the owner's pose, the split's note and the model's table.
    /// **Format 2 (NPC-PIECE-TOPOLOGY-1, 2026-11-13)** names the pick's topology table (<see cref="NpcPieceTopologySample"/>: its file, its
    /// content hash, its counts) and keeps four more counts of the split's note; format 1 (the collections up to 2026-11-12) holds none of
    /// these and is still read -- <see cref="formatVersion"/> says which a record is.
    /// </summary>
    internal sealed class NpcPiecePickSample
    {
        internal const string Magic = "ZTKSPIK1", End = "END!";
        internal const int FormatVersion = 2, FormatWithoutTopology = 1;

        /// <summary>The format this record was read as, or is written as (<see cref="FormatVersion"/> unless a test writes the one before).</summary>
        internal int formatVersion = FormatVersion;

        // Format 2: the topology table's file, its content hash, the geometry's topology vertices and the table's entries; and of the note,
        // the decision's topology vertices, seam vertices joined and connections.
        internal string topologyFile = "", topologySha256 = "";
        internal int topologyVertices, topologyEntries;
        internal int noteTopologyVertices, noteSeamVertices, noteConnectionsKept, noteConnectionsDropped;

        internal string sessionId, codeVersion, sha256;
        internal int pick, frame;
        internal double realtimeSeconds;
        internal string utc;
        internal long world, ledgerRevision;
        internal int fragment, rootFragment, splitSource, generation, epoch;
        internal NpcPieceGeometryIdentity geometry;
        internal string objectName, modelName, modelFile, modelSha256;
        internal bool weightsKept;
        internal string sampleFile, bonesFile;
        internal float3 aimOrigin, aimDirection, hitWorld, hitOwner, hitGeometry;
        internal float hitDistance;
        internal string hitCollider;
        internal float4x4 ownerLocalToWorld, geometryToOwner, shapeToOwner;
        internal float3 bodyPosition, bodyVelocity;
        internal float4 bodyRotation;
        internal int convexCount, indexCount;
        internal string examine;

        // The split's note on the piece (CutSpatialSplit.PieceNote), as it stood; noteIsOfThisGeometry: the note's last attempt was issued for
        // this very geometry in this epoch (false where the note is of an earlier geometry of the fragment's id, or there is no note).
        internal bool hasNote, noteAttempted, noteBoneLed, noteFellBack, noteIsOfThisGeometry;
        internal int noteEndFrame, noteMadeFrame, noteMadeFrom, noteInputEpoch;
        internal string noteEnd, noteMadeBy, noteBoneVerdict, noteBoneReason;
        internal NpcPieceGeometryIdentity noteInput;
        internal int noteBones, noteClusters, noteMeshComponents, noteGroups, noteFullGroups, noteReferencedVertices, noteLineageVertices, notePieceBones, noteUnboundGroups;
        internal int notePositiveConvexes, noteNegativeConvexes, notePositiveTriangles, noteNegativeTriangles;
        internal long noteOriginSteps, noteRemovedWeights;

        internal static string FileName(long world, long pick) =>
            "w" + world.ToString("D3", CultureInfo.InvariantCulture) + "-a" + pick.ToString("D7", CultureInfo.InvariantCulture) + ".zsp.gz";

        internal byte[] Make(out string hash, out long raw)
        {
            using (var memory = new MemoryStream())
            {
                using (var gzip = new GZipStream(memory, System.IO.Compression.CompressionLevel.Fastest, true))
                using (var w = new SpatialSplitHashWriter(gzip, new byte[1 << 16]))
                {
                    w.Ascii(Magic);
                    w.Int(formatVersion);
                    w.String(sessionId); w.String(codeVersion);
                    w.Int(pick); w.Int(frame); w.Double(realtimeSeconds); w.String(utc);
                    w.Long(world); w.Long(ledgerRevision);
                    w.Int(fragment); w.Int(rootFragment); w.Int(splitSource); w.Int(generation); w.Int(epoch);
                    geometry.Write(w);
                    w.String(objectName); w.String(modelName); w.String(modelFile); w.String(modelSha256); w.Byte(weightsKept);
                    w.String(sampleFile); w.String(bonesFile);
                    F3(w, aimOrigin); F3(w, aimDirection); F3(w, hitWorld); F3(w, hitOwner); F3(w, hitGeometry); w.Float(hitDistance); w.String(hitCollider);
                    w.Matrix(ownerLocalToWorld); w.Matrix(geometryToOwner); w.Matrix(shapeToOwner);
                    F3(w, bodyPosition); F3(w, bodyVelocity); w.Float(bodyRotation.x); w.Float(bodyRotation.y); w.Float(bodyRotation.z); w.Float(bodyRotation.w);
                    w.Int(convexCount); w.Int(indexCount); w.String(examine);
                    w.Byte(hasNote); w.Byte(noteAttempted); w.Byte(noteBoneLed); w.Byte(noteFellBack); w.Byte(noteIsOfThisGeometry);
                    w.Int(noteEndFrame); w.Int(noteMadeFrame); w.Int(noteMadeFrom); w.Int(noteInputEpoch);
                    w.String(noteEnd); w.String(noteMadeBy); w.String(noteBoneVerdict); w.String(noteBoneReason);
                    noteInput.Write(w);
                    w.Int(noteBones); w.Int(noteClusters); w.Int(noteMeshComponents); w.Int(noteGroups); w.Int(noteFullGroups); w.Int(noteReferencedVertices); w.Int(noteLineageVertices);
                    w.Int(notePieceBones); w.Int(noteUnboundGroups);
                    w.Int(notePositiveConvexes); w.Int(noteNegativeConvexes); w.Int(notePositiveTriangles); w.Int(noteNegativeTriangles);
                    w.Long(noteOriginSteps); w.Long(noteRemovedWeights);
                    if (formatVersion >= 2)
                    {
                        w.String(topologyFile); w.String(topologySha256); w.Int(topologyVertices); w.Int(topologyEntries);
                        w.Int(noteTopologyVertices); w.Int(noteSeamVertices); w.Int(noteConnectionsKept); w.Int(noteConnectionsDropped);
                    }

                    w.Ascii(End);
                    hash = w.Finish();
                    raw = w.Length;
                }

                return memory.ToArray();
            }
        }

        private static void F3(SpatialSplitHashWriter w, float3 v) { w.Float(v.x); w.Float(v.y); w.Float(v.z); }

        private static float3 F3(SpatialSplitHashReader r) => new float3(r.Float(), r.Float(), r.Float());

        internal static NpcPiecePickSample Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var r = new SpatialSplitHashReader(gzip))
            {
                var s = new NpcPiecePickSample();
                r.Expect(Magic);
                int version = r.Int();
                if (version != FormatVersion && version != FormatWithoutTopology) throw new InvalidDataException("pick format " + version + ", this reads " + FormatWithoutTopology + " and " + FormatVersion);
                s.formatVersion = version;
                s.sessionId = r.String(); s.codeVersion = r.String();
                s.pick = r.Int(); s.frame = r.Int(); s.realtimeSeconds = r.Double(); s.utc = r.String();
                s.world = r.Long(); s.ledgerRevision = r.Long();
                s.fragment = r.Int(); s.rootFragment = r.Int(); s.splitSource = r.Int(); s.generation = r.Int(); s.epoch = r.Int();
                s.geometry = NpcPieceGeometryIdentity.Read(r);
                s.objectName = r.String(); s.modelName = r.String(); s.modelFile = r.String(); s.modelSha256 = r.String(); s.weightsKept = r.Bool();
                s.sampleFile = r.String(); s.bonesFile = r.String();
                s.aimOrigin = F3(r); s.aimDirection = F3(r); s.hitWorld = F3(r); s.hitOwner = F3(r); s.hitGeometry = F3(r); s.hitDistance = r.Float(); s.hitCollider = r.String();
                s.ownerLocalToWorld = r.Matrix(); s.geometryToOwner = r.Matrix(); s.shapeToOwner = r.Matrix();
                s.bodyPosition = F3(r); s.bodyVelocity = F3(r); s.bodyRotation = new float4(r.Float(), r.Float(), r.Float(), r.Float());
                s.convexCount = r.Int(); s.indexCount = r.Int(); s.examine = r.String();
                s.hasNote = r.Bool(); s.noteAttempted = r.Bool(); s.noteBoneLed = r.Bool(); s.noteFellBack = r.Bool(); s.noteIsOfThisGeometry = r.Bool();
                s.noteEndFrame = r.Int(); s.noteMadeFrame = r.Int(); s.noteMadeFrom = r.Int(); s.noteInputEpoch = r.Int();
                s.noteEnd = r.String(); s.noteMadeBy = r.String(); s.noteBoneVerdict = r.String(); s.noteBoneReason = r.String();
                s.noteInput = NpcPieceGeometryIdentity.Read(r);
                s.noteBones = r.Int(); s.noteClusters = r.Int(); s.noteMeshComponents = r.Int(); s.noteGroups = r.Int(); s.noteFullGroups = r.Int(); s.noteReferencedVertices = r.Int();
                s.noteLineageVertices = r.Int(); s.notePieceBones = r.Int(); s.noteUnboundGroups = r.Int();
                s.notePositiveConvexes = r.Int(); s.noteNegativeConvexes = r.Int(); s.notePositiveTriangles = r.Int(); s.noteNegativeTriangles = r.Int();
                s.noteOriginSteps = r.Long(); s.noteRemovedWeights = r.Long();
                if (version >= 2)
                {
                    s.topologyFile = r.String(); s.topologySha256 = r.String(); s.topologyVertices = r.Int(); s.topologyEntries = r.Int();
                    s.noteTopologyVertices = r.Int(); s.noteSeamVertices = r.Int(); s.noteConnectionsKept = r.Int(); s.noteConnectionsDropped = r.Int();
                }

                r.Expect(End);
                s.sha256 = r.Finish();
                return s;
            }
        }

        /// <summary>The picks.jsonl line (the files' hashes are in their own index lines, written by the writer after this one).</summary>
        internal string Line(string file, string hash, long raw, int fileBytes, string failure)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            string J(string s) => SpatialSplitCaptureSink.Json(s ?? "");
            string V(float3 v) => "[" + J(v.x.ToString("R", inv)) + "," + J(v.y.ToString("R", inv)) + "," + J(v.z.ToString("R", inv)) + "]";
            return "{\"pick\":" + pick + ",\"world\":" + world + ",\"frame\":" + frame + ",\"seconds\":" + realtimeSeconds.ToString("R", inv) + ",\"utc\":" + J(utc)
                + ",\"failure\":" + J(failure)
                + ",\"file\":" + J(file) + ",\"sha256\":" + J(hash) + ",\"rawBytes\":" + raw + ",\"fileBytes\":" + fileBytes
                + ",\"format\":" + formatVersion + ",\"topology\":" + J(topologyFile) + ",\"topologyVertices\":" + topologyVertices + ",\"topologyEntries\":" + topologyEntries
                + ",\"sample\":" + J(sampleFile) + ",\"bones\":" + J(bonesFile) + ",\"model\":" + J(modelFile) + ",\"modelName\":" + J(modelName) + ",\"weightsKept\":" + (weightsKept ? "true" : "false")
                + ",\"fragment\":" + fragment + ",\"rootFragment\":" + rootFragment + ",\"splitSource\":" + splitSource + ",\"generation\":" + generation + ",\"epoch\":" + epoch
                + ",\"ledgerRevision\":" + ledgerRevision + ",\"vertexStart\":" + geometry.vertexStart + ",\"vertexCount\":" + geometry.vertexCount
                + ",\"object\":" + J(objectName) + ",\"convexes\":" + convexCount + ",\"indices\":" + indexCount
                + ",\"hitWorld\":" + V(hitWorld) + ",\"hitGeometry\":" + V(hitGeometry) + ",\"hitDistance\":" + J(hitDistance.ToString("R", inv))
                + ",\"examine\":" + J(examine)
                + ",\"note\":" + (hasNote ? "true" : "false") + ",\"noteAttempted\":" + (noteAttempted ? "true" : "false") + ",\"noteIsOfThisGeometry\":" + (noteIsOfThisGeometry ? "true" : "false")
                + ",\"noteEnd\":" + J(noteEnd) + ",\"noteBoneVerdict\":" + J(noteBoneVerdict) + ",\"noteBoneReason\":" + J(noteBoneReason) + ",\"noteFellBack\":" + (noteFellBack ? "true" : "false")
                + ",\"noteMadeBy\":" + J(noteMadeBy) + ",\"noteMadeFrom\":" + noteMadeFrom
                + ",\"noteGroups\":" + noteGroups + ",\"noteFullGroups\":" + noteFullGroups + ",\"noteClusters\":" + noteClusters + "}";
        }
    }

    /// <summary>
    /// The main thread's part of a pick (NPC-PIECE-PICK-1): the checks an issue makes, the copies, and the hand-over to the sink. One call a
    /// press; nothing is kept between calls but which models' tables were written.
    /// </summary>
    internal static unsafe class NpcPiecePickCopy
    {
        /// <summary>Where the line pointed, as the collector read it (the world's frame).</summary>
        internal struct Aim
        {
            internal float3 origin, direction, hitWorld;
            internal float hitDistance;
            internal string hitCollider;
        }

        /// <summary>The parts of one pick handed to the writer: the sample, the bones, the topology table and the pick's own record (four items, each told apart).</summary>
        internal const int ItemsPerPick = 4;

        private static readonly List<float3> s_anchors = new List<float3>();

        /// <summary>
        /// Copies <paramref name="fragment"/> as it is now and hands the pick to <paramref name="sink"/>: true with the record made; false with
        /// <paramref name="failure"/> where the piece cannot be read as an issue reads one (not the current target, a cut under way, no owner or
        /// shape, the geometry open or not readable), is not an NPC's piece, changed while it was read, or the copy was refused. <paramref name="done"/>
        /// is told once by the writer thread for each of the <see cref="ItemsPerPick"/> items whether it was written. <paramref name="models"/> keeps
        /// the tables written so far (by reference) with their file names and hashes.
        /// </summary>
        internal static bool TryPick(CutWorldRoot world, CutSpatialSplit split, SpatialSplitCaptureSink sink, int pick, string codeVersion, LogicalFragmentId fragment, in Aim aim,
            Dictionary<NpcVertexBones, (string file, string hash)> models, Action<bool, string> done, out NpcPiecePickSample record, out string failure)
        {
            record = null;
            failure = null;
            LogicalCutLedger ledger = world != null ? world.Ledger : null;
            PhysicsOwnerRegistry owners = world != null ? world.Owners : null;
            CutDag dag = world != null ? world.Geometry : null;
            VpCpuGeometryStorage storage = world != null ? world.Storage : null;
            NpcBoneSplitRegistry bones = world != null ? world.NpcBones : null;
            if (ledger == null || owners == null || dag == null || storage == null || split == null || sink == null) { failure = "no cut world, split or sink"; return false; }
            if (!fragment.IsSet || !ledger.IsCurrentTarget(fragment)) { failure = "fragment " + fragment.value + " is not a current target of the ledger"; return false; }
            if (ledger.TryGetActiveOperation(fragment, out _) || owners.TryGetProvisionalOf(fragment, out _)) { failure = "a cut of fragment " + fragment.value + " is under way"; return false; }
            if (!owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.IsWithdrawn || owner.IsReleased || owner.Root == null || owner.Body == null) { failure = "fragment " + fragment.value + " has no live owner with a body"; return false; }
            PhysicsOwnerShape shape = owner.Shape;
            if (shape == null || shape.IsFreed) { failure = "fragment " + fragment.value + "'s shape is gone"; return false; }
            if (owner.GeometryLocalToOwner == null) { failure = "fragment " + fragment.value + "'s owner has no geometry frame"; return false; }
            if (!dag.TryGetGeometry(fragment, out VpStoredGeometry geometry)) { failure = "fragment " + fragment.value + "'s geometry is open"; return false; }
            if (bones == null || !bones.TryGet(fragment, out NpcBoneFragment boneRecord)) { failure = "fragment " + fragment.value + " is not an NPC's piece (no bone record)"; return false; }
            if (!storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes) || submeshes.Length == 0) { failure = "fragment " + fragment.value + "'s submeshes are not readable"; return false; }
            if (!storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indexView)) { failure = "fragment " + fragment.value + "'s indices could not be leased"; return false; }

            NpcPieceGeometryIdentity identity = NpcPieceGeometryIdentity.Of(in geometry);
            NpcBoneLineage lineage = boneRecord.Lineage;
            NpcBonePiece piece = boneRecord.Piece;
            lineage.Hold();
            bool held = false;
            NativeArray<int> submeshBlock = default;
            NativeArray<ConvexBrepRange> ranges = default;
            NativeArray<ConvexBrepBank> banks = default;
            SpatialSplitCaptureSample sample = null;
            try
            {
                shape.AcquireForWork();
                held = true;

                // 1. The model's table, once (the pick names it): the base vertices' bones and -- in the collection's mode -- their weights.
                NpcVertexBones table = lineage.Vertices;
                string modelFile = "", modelHash = "";
                bool weightsKept = false;
                if (table != null && !table.IsReleased)
                {
                    weightsKept = table.HasWeights;
                    if (!models.TryGetValue(table, out (string file, string hash) known))
                    {
                        if (!TryHandModelTable(sink, table, models.Count + 1, pick, null, out known, out string why)) { failure = why; return false; }
                        models[table] = known;
                    }

                    modelFile = known.file;
                    modelHash = known.hash;
                }

                // 2. The sample: the piece's mesh, convexes and frames, copied as an issue's input is (the same block layout, the same file).
                int s = submeshes.Length;
                submeshBlock = new NativeArray<int>(2 * s, Allocator.Temp);
                var materials = new int[s];
                for (int i = 0; i < s; i++)
                {
                    submeshBlock[i] = submeshes[i].indexOffset;
                    submeshBlock[s + i] = submeshes[i].indexCount;
                    materials[i] = submeshes[i].materialIndex;
                }

                int convexes = shape.ConvexCount;
                ranges = new NativeArray<ConvexBrepRange>(Math.Max(1, convexes), Allocator.Temp);
                banks = new NativeArray<ConvexBrepBank>(Math.Max(1, convexes), Allocator.Temp);
                var convexBones = new int[convexes];
                for (int c = 0; c < convexes; c++)
                {
                    ranges[c] = shape.Convex(c);
                    banks[c] = shape.BankOf(c);
                    convexBones[c] = shape.ConvexBone(c);
                }

                s_anchors.Clear();
                ledger.TryGetAnchors(fragment, s_anchors);
                float3[] anchors = s_anchors.Count == 0 ? null : s_anchors.ToArray();
                CutSpatialSplitSettings settings = split.Settings;
                CutWorldProfile profile = world.Profile;
                var captureIdentity = new SpatialSplitCaptureIdentity
                {
                    world = split.CaptureWorld, attempt = pick, issuedFrame = Time.frameCount, fragment = fragment.value, epoch = split.MarkEpoch, objectName = owner.Root.name,
                };
                if (ledger.TryGetRoot(fragment, out LogicalFragmentId root)) captureIdentity.rootFragment = root.value;
                if (ledger.TryGetSplitSource(fragment, out LogicalFragmentId source)) captureIdentity.splitSource = source.value;
                LogicalFragmentId at = fragment;
                for (int depth = 0; depth < 4096 && ledger.TryGetParent(at, out LogicalFragmentId parent); depth++)
                {
                    captureIdentity.generation++;
                    at = parent;
                }

                NativeArray<VpRenderVertex>.ReadOnly vertices = storage.Vertices;
                var parameters = new SpatialSplitCaptureParameters
                {
                    margin = CutSpatialSplit.MarginOf(profile), supportEpsilon = profile != null ? profile.SupportEpsilon : 0f, anchorEpsilon = profile != null ? profile.AnchorEpsilon : 0f,
                    vertexLimit = profile != null ? profile.VertexLimit : 0, submeshCount = s, parentMass = owner.Mass, limits = settings.limits, cutConvexes = settings.cutConvexes,
                    minCutSideTriangles = settings.minCutSideTriangles, bentSearch = settings.bentSearch, bentLimits = settings.bentLimits,
                    maxConvexVertices = CutSpatialSplit.Attempt.MaxConvexVertices(shape), geometryToOwner = owner.GeometryLocalToOwner.Value, shapeToOwner = shape.LocalToOwner,
                };
                int* offsets = (int*)submeshBlock.GetUnsafeReadOnlyPtr();
                sample = SpatialSplitCaptureCopy.TryTake(sink, in captureIdentity, in parameters, offsets, offsets + s, materials, anchors,
                    (uint*)indexView.GetUnsafeReadOnlyPtr(), indexView.Length, (VpRenderVertex*)vertices.GetUnsafeReadOnlyPtr(), vertices.Length, convexes,
                    (ConvexBrepRange*)ranges.GetUnsafeReadOnlyPtr(), (ConvexBrepBank*)banks.GetUnsafeReadOnlyPtr());
                if (sample == null) { failure = "the mesh's copy was refused (" + sink.LastRefusal + (sink.StopReason != SpatialSplitCaptureRefusal.None ? "; the capture stopped: " + sink.StopDetail : "") + ")"; return false; }
                sample.done = done;

                // 3. The piece changed or went while it was read? (The same frame of the main thread: expected never; said if ever.)
                if (!ledger.IsCurrentTarget(fragment) || !dag.TryGetGeometry(fragment, out VpStoredGeometry again) || !NpcPieceGeometryIdentity.Of(in again).Equals(identity))
                {
                    sink.Return(sample);
                    sample = null;
                    failure = "fragment " + fragment.value + "'s geometry changed while it was read";
                    return false;
                }

                // 3b. The geometry's own topology table (NPC-PIECE-TOPOLOGY-1): every render vertex the indices name, of every vertex block, to
                //     its topology vertex -- read under the same index lease, through the map the decision reads.
                NpcPieceTopologySample topologySample = NpcPieceTopologySample.Take(storage, geometry, (uint*)indexView.GetUnsafeReadOnlyPtr(), indexView.Length, out string noTopology);
                if (topologySample == null)
                {
                    sink.Return(sample);
                    sample = null;
                    failure = "fragment " + fragment.value + "'s topology table could not be copied: " + noTopology;
                    return false;
                }

                topologySample.sessionId = sink.sessionId;
                topologySample.world = captureIdentity.world;
                topologySample.pick = pick;
                topologySample.fragment = fragment.value;
                topologySample.epoch = captureIdentity.epoch;
                SpatialSplitBoneItem topologyItem = MakeTopologyItem(topologySample, done, out string topologyHash);

                // 4. The pick's own record, then the bones; everything handed in the order sample, bones, topology, pick (the sample first: its file is the one the others name).
                record = new NpcPiecePickSample
                {
                    topologyFile = topologyItem.file, topologySha256 = topologyHash, topologyVertices = topologySample.topologyVertexCount, topologyEntries = topologySample.vertexIds.Length,
                    sessionId = sink.sessionId, codeVersion = codeVersion, pick = pick, frame = Time.frameCount, realtimeSeconds = Time.realtimeSinceStartupAsDouble,
                    utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), world = captureIdentity.world, ledgerRevision = ledger.Revision,
                    fragment = fragment.value, rootFragment = captureIdentity.rootFragment, splitSource = captureIdentity.splitSource, generation = captureIdentity.generation, epoch = captureIdentity.epoch,
                    geometry = identity, objectName = owner.Root.name, modelName = table != null ? table.ModelName : "", modelFile = modelFile, modelSha256 = modelHash, weightsKept = weightsKept,
                    sampleFile = SpatialSplitCaptureSink.SampleFileName(captureIdentity.world, pick), bonesFile = SpatialSplitBoneSample.FileName(captureIdentity.world, pick),
                    aimOrigin = aim.origin, aimDirection = aim.direction, hitWorld = aim.hitWorld, hitDistance = aim.hitDistance, hitCollider = aim.hitCollider ?? "",
                    ownerLocalToWorld = owner.Root.transform.localToWorldMatrix, geometryToOwner = parameters.geometryToOwner, shapeToOwner = parameters.shapeToOwner,
                    bodyPosition = owner.Body.position, bodyVelocity = owner.Body.linearVelocity, bodyRotation = new float4(owner.Body.rotation.x, owner.Body.rotation.y, owner.Body.rotation.z, owner.Body.rotation.w),
                    convexCount = convexes, indexCount = indexView.Length, examine = split.Examine(fragment).ToString(),
                };
                record.hitOwner = math.transform(math.inverse(record.ownerLocalToWorld), aim.hitWorld);
                record.hitGeometry = math.transform(math.inverse(parameters.geometryToOwner), record.hitOwner);
                if (split.TryGetPieceNote(fragment, out CutSpatialSplit.PieceNote note))
                {
                    record.hasNote = true;
                    record.noteAttempted = note.attempted;
                    record.noteBoneLed = note.boneLed;
                    record.noteFellBack = note.fellBack;
                    record.noteIsOfThisGeometry = note.IsOf(in geometry, split.MarkEpoch);
                    record.noteEndFrame = note.endFrame;
                    record.noteMadeFrame = note.madeFrame;
                    record.noteMadeFrom = note.madeFrom.value;
                    record.noteInputEpoch = note.inputEpoch;
                    record.noteEnd = note.attempted ? note.end.ToString() : "";
                    record.noteMadeBy = note.madeBy ?? "";
                    record.noteBoneVerdict = note.bone.Entered ? note.bone.verdict.ToString() : "";
                    record.noteBoneReason = note.bone.reason ?? "";
                    record.noteInput = new NpcPieceGeometryIdentity
                    {
                        vertexStart = note.inputVertexStart, vertexCount = note.inputVertexCount, topologyVertexCount = note.inputTopologyVertexCount, submeshStart = note.inputSubmeshStart,
                        submeshCount = note.inputSubmeshCount, blockStart = note.inputBlockStart, blockCount = note.inputBlockCount,
                    };
                    record.noteBones = note.bone.bones; record.noteClusters = note.bone.clusters; record.noteMeshComponents = note.bone.meshComponents; record.noteGroups = note.bone.groups;
                    record.noteFullGroups = note.bone.fullGroups; record.noteReferencedVertices = note.bone.referencedVertices; record.noteLineageVertices = note.bone.lineageVertices;
                    record.notePieceBones = note.bone.pieceBones; record.noteUnboundGroups = note.bone.unboundGroups;
                    record.notePositiveConvexes = note.bone.positiveConvexes; record.noteNegativeConvexes = note.bone.negativeConvexes;
                    record.notePositiveTriangles = note.bone.positiveTriangles; record.noteNegativeTriangles = note.bone.negativeTriangles;
                    record.noteOriginSteps = note.bone.originSteps; record.noteRemovedWeights = note.bone.removedWeights;
                    record.noteTopologyVertices = note.bone.topologyVertices; record.noteSeamVertices = note.bone.seamVertices;
                    record.noteConnectionsKept = note.bone.connectionsKept; record.noteConnectionsDropped = note.bone.connectionsDropped;
                }

                SpatialSplitBoneItem pickItem = MakeRecordItem(record, done);
                if (!sink.TryReserve(pickItem.bytes, false))
                {
                    sink.Return(sample);
                    sample = null;
                    failure = "no room in the queue for the pick's record (" + sink.LastRefusal + ")";
                    return false;
                }

                sink.NoteIssued(sample);   // the sample is the writer's from here
                sample = null;
                string insufficient = lineage.Insufficient;
                if (insufficient == null && anchors != null) insufficient = "the fragment has anchors";
                if (insufficient == null && !shape.HasConvexBones) insufficient = "the shape carries no convex bones";
                bool bonesHanded = SpatialSplitBoneSample.Write(sink, captureIdentity.world, pick, fragment.value, captureIdentity.epoch, lineage, piece, convexBones, insufficient,
                    NpcBoneSplitSearch.DefaultMaxOriginSteps, table != null && !table.IsReleased ? table.Pointer : null, lineage.Chunks, lineage.LocalCount, in settings, SpatialSplitGraph.IsPrepared, done);
                if (!bonesHanded) done?.Invoke(false, "the bones' sidecar was not handed to the writer");
                if (sink.TryReserve(topologyItem.bytes, false)) sink.HandBone(topologyItem);
                else done?.Invoke(false, "no room in the queue for the topology table (" + sink.LastRefusal + ")");
                sink.HandBone(pickItem);
                return true;
            }
            catch (Exception e)
            {
                if (sample != null) { sink.Return(sample); sample = null; }
                failure = "the copy threw: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                if (submeshBlock.IsCreated) submeshBlock.Dispose();
                if (ranges.IsCreated) ranges.Dispose();
                if (banks.IsCreated) banks.Dispose();
                if (held) shape.ReleaseFromWork();
                storage.TryReleaseIndexReadLease(lease);
                lineage.Release();
            }
        }

        /// <summary>
        /// The model's table as one item of the writer's (the file and its models.jsonl line), handed with its room reserved: true with the
        /// file's name and content hash; false with why where the queue had no room. Tests write a table the same way.
        /// </summary>
        internal static bool TryHandModelTable(SpatialSplitCaptureSink sink, NpcVertexBones table, int model, int firstPick, Action<bool, string> done, out (string file, string hash) named, out string why)
        {
            string name = NpcModelWeightTable.FileName(model);
            byte[] content = NpcModelWeightTable.Make(sink.sessionId, table, out string hash, out long raw);
            var item = new SpatialSplitBoneItem
            {
                file = name, content = content, index = "models.jsonl",
                line = "{\"model\":" + model + ",\"file\":" + SpatialSplitCaptureSink.Json(name) + ",\"sha256\":" + SpatialSplitCaptureSink.Json(hash) + ",\"rawBytes\":" + raw
                    + ",\"fileBytes\":" + content.Length + ",\"modelName\":" + SpatialSplitCaptureSink.Json(table.ModelName) + ",\"vertices\":" + table.VertexCount
                    + ",\"palette\":" + table.PaletteCount + ",\"weightsKept\":" + (table.HasWeights ? "true" : "false") + ",\"firstPick\":" + firstPick + "}",
                bytes = content.Length + SpatialSplitCaptureResult.Overhead, done = done,
            };
            named = (name, hash);
            if (!sink.TryReserve(item.bytes, false))
            {
                why = "no room in the queue for the model's table (" + sink.LastRefusal + ")";
                return false;
            }

            sink.HandBone(item);
            why = null;
            return true;
        }

        /// <summary>The topology table as one item of the writer's (its file and its topologies.jsonl line); the caller reserves its room and hands it.</summary>
        internal static SpatialSplitBoneItem MakeTopologyItem(NpcPieceTopologySample table, Action<bool, string> done, out string hash)
        {
            string name = NpcPieceTopologySample.FileName(table.world, table.pick);
            byte[] content = table.Make(out hash, out long raw);
            return new SpatialSplitBoneItem
            {
                file = name, content = content, index = NpcPieceTopologySample.Index, line = table.Line(name, hash, raw, content.Length),
                bytes = content.Length + SpatialSplitCaptureResult.Overhead, done = done,
            };
        }

        /// <summary>The pick's own record as one item of the writer's (its file and its picks.jsonl line); the caller reserves its room and hands it.</summary>
        internal static SpatialSplitBoneItem MakeRecordItem(NpcPiecePickSample record, Action<bool, string> done)
        {
            string name = NpcPiecePickSample.FileName(record.world, record.pick);
            byte[] content = record.Make(out string hash, out long raw);
            return new SpatialSplitBoneItem
            {
                file = name, content = content, index = "picks.jsonl", line = record.Line(name, hash, raw, content.Length, null),
                bytes = content.Length + SpatialSplitCaptureResult.Overhead, done = done,
            };
        }

        /// <summary>A pick that failed, as its picks.jsonl line (no files): handed to the writer so that the folder itself tells of it.</summary>
        internal static void HandFailure(SpatialSplitCaptureSink sink, long world, int pick, string failure, int fragment, Action<bool, string> done)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var item = new SpatialSplitBoneItem
            {
                index = "picks.jsonl",
                line = "{\"pick\":" + pick + ",\"world\":" + world + ",\"frame\":" + Time.frameCount + ",\"seconds\":" + Time.realtimeSinceStartupAsDouble.ToString("R", inv)
                    + ",\"utc\":" + SpatialSplitCaptureSink.Json(DateTime.UtcNow.ToString("o", inv)) + ",\"failure\":" + SpatialSplitCaptureSink.Json(failure ?? "") + ",\"fragment\":" + fragment + "}",
                bytes = SpatialSplitCaptureResult.Overhead, done = done,
            };
            if (!sink.TryReserve(item.bytes, true))
            {
                done?.Invoke(false, "no room in the queue for the failure's line (" + sink.LastRefusal + ")");
                return;
            }

            sink.HandBone(item);
        }
    }

    /// <summary>
    /// A collection's folder read back (NPC-PIECE-PICK-1): each pick's three files and its model's table found, read whole and matched to
    /// their index lines' hashes, the three in agreement on who the pick is and of which geometry, and -- for the analysis -- the decision's
    /// input rebuilt from the saved parts so that <see cref="NpcBoneSplitSearch.Decide"/> can be run over it as the live split ran.
    /// </summary>
    internal static unsafe class NpcPiecePickSet
    {
        /// <summary>One pick as read back: its records, or what was missing or did not agree.</summary>
        internal sealed class Pick
        {
            internal int number;
            internal string failure;   // the pick's own failure at the time (a line without files), or null
            internal NpcPiecePickSample record;
            internal SpatialSplitSample sample;
            internal SpatialSplitBoneSample bones;
            internal NpcModelWeightTable model;
            internal NpcPieceTopologySample topology;   // format 2; null for a pick of the format before (it saved none)
            internal readonly List<string> problems = new List<string>();

            /// <summary>
            /// Whole: every file there, every hash as listed, the records of one pick and one geometry, the table with weights -- and, for a
            /// pick of format 2, its topology table covering every vertex the sample names.
            /// </summary>
            internal bool IsWhole => failure == null && problems.Count == 0 && record != null && sample != null && bones != null
                && (record.formatVersion < 2 || topology != null);

            /// <summary>The pick holds its geometry's own topology table (format 2, read and agreeing).</summary>
            internal bool HasTopology => topology != null;
        }

        [Serializable]
        private sealed class IndexLine
        {
            public int pick;
            public long world, attempt;
            public string file, sha256, failure, sample, bones, model;
        }

        /// <summary>Reads every pick listed in picks.jsonl (one entry a pick number; a failed pick is an entry with its failure and no files).</summary>
        internal static List<Pick> Read(string folder)
        {
            var picks = new List<Pick>();
            string index = Path.Combine(folder, "picks.jsonl");
            if (!File.Exists(index)) return picks;
            Dictionary<string, string> sampleHashes = Hashes(Path.Combine(folder, "samples.jsonl"));
            Dictionary<string, string> boneHashes = Hashes(Path.Combine(folder, "bones.jsonl"));
            Dictionary<string, string> modelHashes = Hashes(Path.Combine(folder, "models.jsonl"));
            Dictionary<string, string> topologyHashes = Hashes(Path.Combine(folder, NpcPieceTopologySample.Index));
            foreach (string text in File.ReadAllLines(index))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                IndexLine line;
                try { line = JsonUtility.FromJson<IndexLine>(text); }
                catch (Exception e) { picks.Add(new Pick { number = -1, problems = { "a picks.jsonl line that does not read: " + e.Message } }); continue; }
                var pick = new Pick { number = line.pick, failure = string.IsNullOrEmpty(line.failure) ? null : line.failure };
                picks.Add(pick);
                if (pick.failure != null) continue;
                pick.record = ReadOne(folder, line.file, line.sha256, "the pick's record", pick.problems, NpcPiecePickSample.Read, r => r.sha256);
                pick.sample = ReadOne(folder, line.sample, sampleHashes.TryGetValue(line.sample ?? "", out string sh) ? sh : null, "the sample", pick.problems, SpatialSplitSample.Read, r => r.sha256);
                pick.bones = ReadOne(folder, line.bones, boneHashes.TryGetValue(line.bones ?? "", out string bh) ? bh : null, "the bones", pick.problems, SpatialSplitBoneSample.Read, r => r.sha256);
                if (!string.IsNullOrEmpty(line.model))
                {
                    pick.model = ReadOne(folder, line.model, modelHashes.TryGetValue(line.model, out string mh) ? mh : null, "the model's table", pick.problems, NpcModelWeightTable.Read, r => r.sha256);
                    if (pick.model != null)
                    {
                        string check = pick.model.Check();
                        if (check != null) pick.problems.Add("the model's table: " + check);
                        if (!pick.model.hasWeights && pick.record != null && pick.record.weightsKept) pick.problems.Add("the pick says the weights were kept, the model's table keeps none");
                    }
                }
                else
                {
                    pick.problems.Add("no model's table is named");
                }

                if (pick.record != null && pick.record.formatVersion >= 2)
                {
                    // The topology table: the file the record names, with the hash its index line lists and the hash the record keeps.
                    string named = pick.record.topologyFile;
                    pick.topology = ReadOne(folder, named, topologyHashes.TryGetValue(named ?? "", out string th) ? th : null, "the topology", pick.problems, NpcPieceTopologySample.Read, r => r.sha256);
                    if (pick.topology != null)
                    {
                        NpcPieceTopologySample t = pick.topology;
                        string check = t.Check();
                        if (check != null) pick.problems.Add("the topology: " + check);
                        if (!string.Equals(t.sha256, pick.record.topologySha256, StringComparison.Ordinal)) pick.problems.Add("the topology is not the one the pick named (hash)");
                        if (t.world != pick.record.world || t.pick != pick.record.pick || t.fragment != pick.record.fragment || t.epoch != pick.record.epoch || t.sessionId != pick.record.sessionId)
                            pick.problems.Add("the topology is of another pick (world " + t.world + ", pick " + t.pick + ", fragment " + t.fragment + ")");
                        if (!t.geometry.Equals(pick.record.geometry)) pick.problems.Add("the topology is of another geometry (" + t.geometry + ") than the pick's (" + pick.record.geometry + ")");
                        if (t.topologyVertexCount != pick.record.topologyVertices || t.vertexIds.Length != pick.record.topologyEntries) pick.problems.Add("the topology's counts are not the pick's");
                        if (pick.sample != null)
                        {
                            if (t.vertexIds.Length != pick.sample.vertexIds.Length) pick.problems.Add("the topology has " + t.vertexIds.Length + " vertices, the sample names " + pick.sample.vertexIds.Length);
                            foreach (uint id in pick.sample.vertexIds)
                            {
                                if (t.TopologyOf(id) < 0) { pick.problems.Add("the topology has no entry for vertex " + id + " the sample names"); break; }
                            }
                        }

                        if (pick.problems.Count > 0) pick.topology = null;   // never decided with a table that does not agree
                    }
                }

                if (pick.record != null && pick.sample != null && pick.bones != null)
                {
                    if (pick.record.world != pick.sample.world || pick.record.pick != pick.sample.attempt) pick.problems.Add("the sample is of another pick (world " + pick.sample.world + ", attempt " + pick.sample.attempt + ")");
                    if (pick.record.world != pick.bones.world || pick.record.pick != pick.bones.attempt) pick.problems.Add("the bones are of another pick (world " + pick.bones.world + ", attempt " + pick.bones.attempt + ")");
                    if (pick.record.fragment != pick.sample.fragment || pick.record.fragment != pick.bones.fragment) pick.problems.Add("the three name different fragments (" + pick.record.fragment + ", " + pick.sample.fragment + ", " + pick.bones.fragment + ")");
                    if (pick.record.epoch != pick.sample.epoch || pick.record.epoch != pick.bones.epoch) pick.problems.Add("the three are of different epochs");
                    if (pick.record.sessionId != pick.sample.sessionId || pick.record.sessionId != pick.bones.sessionId) pick.problems.Add("the three are of different sessions");
                    if (pick.sample.indices.Length != pick.record.indexCount || pick.sample.convexes.Length != pick.record.convexCount) pick.problems.Add("the sample's counts are not the pick's");
                    if (pick.bones.convexBones.Length != pick.sample.convexes.Length) pick.problems.Add("the bones name " + pick.bones.convexBones.Length + " convexes, the sample " + pick.sample.convexes.Length);
                    // The generation: every vertex the sample's indices name must be of the saved lineage (its base, or a cut's new vertices it
                    // recorded); one that is not is a sample of another geometry than the bones describe.
                    foreach (uint id in pick.sample.vertexIds)
                    {
                        if (LocalOf(pick.bones, id) < 0) { pick.problems.Add("the sample names vertex " + id + ", which is not of the saved lineage: the geometry generation differs"); break; }
                    }

                    if (pick.model != null && pick.model.vertexCount != pick.bones.baseCount) pick.problems.Add("the model's table has " + pick.model.vertexCount + " vertices, the lineage's base " + pick.bones.baseCount);
                    if (pick.model != null && pick.record.modelSha256 != pick.model.sha256) pick.problems.Add("the model's table is not the one the pick named (hash)");
                }
            }

            return picks;
        }

        private static T ReadOne<T>(string folder, string file, string listedHash, string what, List<string> problems, Func<string, T> read, Func<T, string> hashOf) where T : class
        {
            if (string.IsNullOrEmpty(file)) { problems.Add(what + " is not named"); return null; }
            string path = Path.Combine(folder, file);
            if (!File.Exists(path)) { problems.Add(what + " is missing: " + file); return null; }
            T value;
            try { value = read(path); }
            catch (Exception e) { problems.Add(what + " does not read: " + file + ": " + e.GetType().Name + ": " + e.Message); return null; }
            if (listedHash == null) problems.Add(what + " has no index line: " + file);
            else if (!string.Equals(listedHash, hashOf(value), StringComparison.Ordinal)) problems.Add(what + "'s hash is not the listed one: " + file);
            return value;
        }

        private static Dictionary<string, string> Hashes(string index)
        {
            var hashes = new Dictionary<string, string>();
            if (!File.Exists(index)) return hashes;
            foreach (string text in File.ReadAllLines(index))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                try
                {
                    IndexLine line = JsonUtility.FromJson<IndexLine>(text);
                    if (!string.IsNullOrEmpty(line.file) && !string.IsNullOrEmpty(line.sha256)) hashes[line.file] = line.sha256;
                }
                catch (Exception)
                {
                    // a cut-short line: the file it would have listed is then "without an index line"
                }
            }

            return hashes;
        }

        /// <summary>NPC-ATTEMPT-CAPTURE-1: the capture's hash of the sides at <paramref name="offset"/> (the triangles', after the convexes'), for a caller without pointers.</summary>
        internal static string SidesHash(byte[] sides, int offset, int count)
        {
            fixed (byte* p = sides) return SpatialSplitReplay.SidesHash(p + offset, count);
        }

        /// <summary>A storage vertex id in the saved lineage's own numbering (the base first, then each cut's new vertices); -1 for none.</summary>
        internal static int LocalOf(SpatialSplitBoneSample bones, uint id)
        {
            uint f = id - bones.baseStart;
            if (f < (uint)bones.baseCount) return (int)f;
            for (int c = 0; c < bones.chunkStarts.Length; c++)
            {
                uint a = id - bones.chunkStarts[c];
                if (a < (uint)bones.chunkOrigins[c].Length) return bones.chunkLocalBases[c] + (int)a;
            }

            return -1;
        }

        /// <summary>
        /// The decision run over a pick as read back, as the live split runs it: the lineage and the piece from the bones' sidecar, the
        /// convexes' bones, the sample's indices (the storage's ids, which the saved lineage numbers). The sides (convexes then triangles) are
        /// written to <paramref name="sides"/>; the bones' sides to <paramref name="boneSides"/> (2n).
        /// </summary>
        /// <remarks>
        /// NPC-PIECE-TOPOLOGY-1: a pick of format 2 is decided with its own saved topology table, as it stands (nothing worked out from
        /// positions or origins); a pick of the format before holds none and is decided with each render vertex its own topology vertex --
        /// which is what the live decision did when those were taken.
        /// </remarks>
        internal static NpcBoneSplitRecord Decide(Pick pick, out byte[] sides, out byte[] boneSides)
        {
            int[] ids = SavedTopologyOfLocal(pick, out int count);
            return Decide(pick, ids, count, out sides, out boneSides);
        }

        /// <summary>
        /// The pick's saved topology table in the saved lineage's numbering (one id a lineage vertex; a vertex no index names keeps 0 and is
        /// never looked up), with the geometry's topology vertex count; null for a pick that saved none.
        /// </summary>
        internal static int[] SavedTopologyOfLocal(Pick pick, out int topologyVertexCount)
        {
            topologyVertexCount = 0;
            if (pick.topology == null || pick.bones == null) return null;
            var ids = new int[Math.Max(0, pick.bones.localCount)];
            NpcPieceTopologySample t = pick.topology;
            for (int i = 0; i < t.vertexIds.Length; i++)
            {
                int local = LocalOf(pick.bones, t.vertexIds[i]);
                if (local >= 0 && local < ids.Length) ids[local] = t.topologyIds[i];
            }

            topologyVertexCount = t.topologyVertexCount;
            return ids;
        }

        /// <summary>
        /// NPC-BONE-TOPOLOGY-1: the same with a topology given from outside, one id a vertex of the saved lineage's numbering (ids below
        /// <paramref name="topologyVertexCount"/>); null decides with each render vertex its own topology vertex.
        /// </summary>
        internal static NpcBoneSplitRecord Decide(Pick pick, int[] topologyOfLocal, int topologyVertexCount, out byte[] sides, out byte[] boneSides)
        {
            SpatialSplitBoneSample b = pick.bones;
            int n = b.nodeCount;
            var chunks = new NpcVertexChunk[b.chunkStarts.Length];
            var owned = new List<NativeArray<uint2>>();
            var baseBones = new NativeArray<ulong>(Math.Max(1, b.baseBones != null ? b.baseBones.Length : 0), Allocator.Temp);
            var indices = new NativeArray<uint>(Math.Max(1, pick.sample.indices.Length), Allocator.Temp);
            int c = b.convexBones.Length, t = pick.sample.indices.Length / 3;
            var bytes = new NativeArray<byte>(Math.Max(1, c + t), Allocator.Temp);
            try
            {
                if (b.baseBones != null) NativeArray<ulong>.Copy(b.baseBones, baseBones, b.baseBones.Length);
                NativeArray<uint>.Copy(pick.sample.indices, indices, pick.sample.indices.Length);
                for (int k = 0; k < chunks.Length; k++)
                {
                    var origins = new NativeArray<uint2>(Math.Max(1, b.chunkOrigins[k].Length), Allocator.Temp);
                    NativeArray<uint2>.Copy(b.chunkOrigins[k], origins, b.chunkOrigins[k].Length);
                    owned.Add(origins);
                    chunks[k] = new NpcVertexChunk { origins = (uint2*)origins.GetUnsafeReadOnlyPtr(), start = b.chunkStarts[k], count = b.chunkOrigins[k].Length, localBase = b.chunkLocalBases[k] };
                }

                boneSides = new byte[2 * n];
                var input = new NpcBoneSplitSearch.Input
                {
                    indices = (uint*)indices.GetUnsafeReadOnlyPtr(), indexCount = pick.sample.indices.Length, convexCount = c, convexBones = b.convexBones,
                    lineage = new NpcBoneSplitSearch.Lineage
                    {
                        nodeCount = n, parent = b.parent, present = b.present, baseBones = b.baseBones != null ? (ulong*)baseBones.GetUnsafeReadOnlyPtr() : null,
                        baseStart = b.baseStart, baseCount = b.baseCount, chunks = chunks, localCount = b.localCount,
                    },
                    jointKept = b.jointKept, segmentLo = b.segmentLo, segmentHi = b.segmentHi, boneSides = boneSides,
                    insufficient = b.insufficient, maxTriangles = pick.sample.limits.maxTriangles, maxOriginSteps = b.maxOriginSteps,   // the search limits the live decision read
                };
                var record = default(NpcBoneSplitRecord);
                using (NpcBoneTopology topology = NpcBoneTopology.OfLocal(in input.lineage, topologyOfLocal, topologyVertexCount))
                {
                    input.topology = topology.Map;
                    NpcBoneSplitSearch.Decide(in input, (byte*)bytes.GetUnsafePtr(), c + t, ref record);
                }

                sides = new byte[c + t];
                NativeArray<byte>.Copy(bytes, sides, c + t);
                return record;
            }
            finally
            {
                foreach (NativeArray<uint2> o in owned) o.Dispose();
                baseBones.Dispose();
                indices.Dispose();
                bytes.Dispose();
            }
        }

        /// <summary>One line a pick: whole or not, and why not.</summary>
        internal static string Describe(List<Pick> picks)
        {
            var text = new StringBuilder();
            int whole = 0, failed = 0, broken = 0, withTopology = 0, formatBefore = 0;
            foreach (Pick p in picks)
            {
                if (p.failure != null) { failed++; text.Append("pick #").Append(p.number).Append(": failed at the time: ").Append(p.failure).Append('\n'); continue; }
                if (p.IsWhole)
                {
                    whole++;
                    if (p.HasTopology) withTopology++; else formatBefore++;
                    continue;
                }

                broken++;
                text.Append("pick #").Append(p.number).Append(": ").Append(string.Join("; ", p.problems)).Append('\n');
            }

            return "picks " + picks.Count + ": whole " + whole + ", failed at the time " + failed + ", missing or not agreeing " + broken
                + "; of the whole: with their own topology table " + withTopology + ", of the format before (no topology saved) " + formatBefore
                + (text.Length > 0 ? "\n" + text.ToString().TrimEnd('\n') : "");
        }
    }
}
#endif
