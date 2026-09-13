using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.Verification
{
    /// <summary>Minimal Burst job wrapper (test / harness only) proving the kernel runs on the job path as well as by direct call.</summary>
    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct MeshCutJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public MeshCutInput input;
        [NativeDisableUnsafePtrRestriction] public MeshCutOutput output;
        public NativeArray<MeshCutResult> result;

        public void Execute()
        {
            var r = result[0];
            MeshCutKernel.Execute(in input, in output, ref r);
            result[0] = r;
        }
    }

    public sealed class RunOptions
    {
        public int VertexDeficit, IndexDeficit, ScratchDeficit, ExtraScratch;
        public bool ViaJob;
        public byte Fill = 0xA5;
        public uint ScratchScrambleSeed;
        public bool RequestNodes = true;
        public bool CheckUnrelatedUnchanged = true;
        /// <summary>Slots left unused between the existing pool and the new reservation (sparse layout); default 7 / 5.</summary>
        public uint VertexGap = 7, IndexGap = 5;
        public int ScratchOverride = -1;
    }

    /// <summary>
    /// Test-side driver of one cut over a managed pool mirror: lays the global vertex / index views, the one new-vertex
    /// and one new-index reservation, the topology output and the scratch inside a guarded arena at arbitrary
    /// non-zero bases, queries the kernel's capacity, runs it directly or through <see cref="MeshCutJob"/>, checks that
    /// nothing outside the reservations changed, reads the new block back into the pool and builds the two side
    /// geometries (with the appended topology block) so the outputs can be verified and cut again.
    /// </summary>
    public sealed unsafe class MeshCutHarness : IDisposable
    {
        public readonly PoolArrays Pool = new PoolArrays();
        readonly GuardedArena m_arena;
        NativeArray<MeshCutResult> m_jobResult;
        public MeshCutCapacity LastCapacity;
        public MeshCutResult LastResult;
        public List<string> LastBadGuards = new List<string>();
        public List<string> LastUnrelatedChanges = new List<string>();
        public double LastMicroseconds;
        public int LastScratchBytes, LastVertexCapacity, LastIndexCapacity;
        public ulong LastInputHashBefore, LastInputHashAfter;

        public MeshCutHarness(int arenaBytes = 96 << 20) { m_arena = new GuardedArena(arenaBytes); }

        static readonly RenderVertex k_filler = new RenderVertex { position = new float3(float.NaN), normal = new float3(9, 9, 9), uv0 = new float2(-7, -7), tangent = new float4(0, 0, 0, 0) };

        /// <summary>Places a synthetic mesh at explicit vertex / index offsets of the pool (unused slots hold a filler pattern).</summary>
        public CutGeometry Place(SyntheticMesh mesh, string name, uint vertexOffset, uint indexOffset)
        {
            GrowVertices((int)vertexOffset + mesh.Vertices.Length);
            GrowIndices((int)indexOffset + mesh.Indices.Length);
            for (int i = 0; i < mesh.Vertices.Length; i++) Pool.Vertices[vertexOffset + i] = mesh.Vertices[i];
            for (int i = 0; i < mesh.Indices.Length; i++) Pool.Indices[indexOffset + i] = mesh.Indices[i] + vertexOffset;
            var g = new CutGeometry { Name = name, Pool = Pool, TopologyVertexCount = mesh.TopologyVertexCount };
            uint start = indexOffset;
            foreach (int count in mesh.SubmeshIndexCounts)
            {
                g.Ranges.Add(new MeshCutIndexRange { indexStart = start, indexCount = count });
                start += (uint)count;
            }
            g.Topology.Add(new TopologyBlock { VertexBase = vertexOffset, TopologyVertex = (int[])mesh.TopologyOfVertex.Clone() });
            return g;
        }

        void GrowVertices(int length)
        {
            if (Pool.Vertices.Length >= length) return;
            var a = new RenderVertex[length];
            Array.Copy(Pool.Vertices, a, Pool.Vertices.Length);
            for (int i = Pool.Vertices.Length; i < length; i++) a[i] = k_filler;
            Pool.Vertices = a;
        }

        void GrowIndices(int length)
        {
            if (Pool.Indices.Length >= length) return;
            var a = new uint[length];
            Array.Copy(Pool.Indices, a, Pool.Indices.Length);
            for (int i = Pool.Indices.Length; i < length; i++) a[i] = 0xDEADBEEFu;
            Pool.Indices = a;
        }

        /// <summary>Runs one cut. The result geometry (when Ok) is appended to the pool; the run record carries everything the verifier needs.</summary>
        public CutRun Cut(CutGeometry g, float4 plane, RunOptions opt = null)
        {
            opt = opt ?? new RunOptions();
            m_arena.Reset(opt.Fill);
            LastBadGuards.Clear(); LastUnrelatedChanges.Clear();

            int poolV = Pool.Vertices.Length, poolI = Pool.Indices.Length;
            uint newVertexBase = (uint)poolV + opt.VertexGap;
            uint newIndexBase = (uint)poolI + opt.IndexGap;

            // topology map (input): one block per entry of the geometry's map
            var blocks = m_arena.Alloc<RenderTopologyRange>("topology.blocks", g.Topology.Count);
            for (int b = 0; b < g.Topology.Count; b++)
            {
                int* ids = m_arena.Alloc<int>("topology.ids." + b, g.Topology[b].Count);
                for (int i = 0; i < g.Topology[b].Count; i++) ids[i] = g.Topology[b].TopologyVertex[i];
                blocks[b] = new RenderTopologyRange { vertexBase = g.Topology[b].VertexBase, count = g.Topology[b].Count, topologyVertex = ids };
            }
            var ranges = m_arena.Alloc<MeshCutIndexRange>("input.ranges", g.Ranges.Count);
            for (int i = 0; i < g.Ranges.Count; i++) ranges[i] = g.Ranges[i];

            // capacity query needs the vertex / index views; the views are built with room for the reservation
            // First a provisional input over a minimal view to query capacity (the query reads only the geometry).
            var probeVB = m_arena.Alloc<RenderVertex>("probe.vb", poolV);
            var probeIB = m_arena.Alloc<uint>("probe.ib", poolI);
            for (int i = 0; i < poolV; i++) probeVB[i] = Pool.Vertices[i];
            for (int i = 0; i < poolI; i++) probeIB[i] = Pool.Indices[i];
            var probeInput = new MeshCutInput
            {
                vertices = probeVB, vertexViewLength = poolV, indices = probeIB, indexViewLength = poolI,
                ranges = ranges, rangeCount = g.Ranges.Count,
                topology = new RenderCutTopologyMap { ranges = blocks, rangeCount = g.Topology.Count, topologyVertexCount = g.TopologyVertexCount },
                plane = plane,
            };
            MeshCutCapacity cap = default;
            MeshCutKernel.QueryCapacity(in probeInput, ref cap);
            LastCapacity = cap;

            int vertexCap = Math.Max(0, cap.newVertices - opt.VertexDeficit);
            int indexCap = Math.Max(0, cap.newIndices - opt.IndexDeficit);
            int scratchBytes = opt.ScratchOverride >= 0 ? opt.ScratchOverride : Math.Max(0, cap.scratchBytes - opt.ScratchDeficit + opt.ExtraScratch);
            LastVertexCapacity = vertexCap; LastIndexCapacity = indexCap; LastScratchBytes = scratchBytes;

            // the real views: pool, gap, reservation, tail
            int vbLength = (int)newVertexBase + vertexCap + (int)opt.VertexGap;
            int ibLength = (int)newIndexBase + indexCap + (int)opt.IndexGap;
            var vb = m_arena.Alloc<RenderVertex>("vb", vbLength);
            var ib = m_arena.Alloc<uint>("ib", ibLength);
            for (int i = 0; i < poolV; i++) vb[i] = Pool.Vertices[i];
            for (int i = 0; i < poolI; i++) ib[i] = Pool.Indices[i];
            var topoOut = m_arena.Alloc<int>("out.topology", vertexCap);
            var outRanges = m_arena.Alloc<MeshCutIndexRange>("out.ranges", 2 * g.Ranges.Count);
            int nodeCap = opt.RequestNodes ? Math.Max(1, 2 * cap.crossingTriangles) : 0;
            long* nodeKeys = opt.RequestNodes ? m_arena.Alloc<long>("out.nodeKeys", nodeCap) : null;
            float* nodeParams = opt.RequestNodes ? m_arena.Alloc<float>("out.nodeParams", nodeCap) : null;
            byte* scratch = m_arena.Alloc<byte>("scratch", scratchBytes);
            if (opt.ScratchScrambleSeed != 0) m_arena.Scramble("scratch", opt.ScratchScrambleSeed);

            var input = probeInput;
            input.vertices = vb; input.vertexViewLength = vbLength; input.indices = ib; input.indexViewLength = ibLength;
            var output = new MeshCutOutput
            {
                newVertices = vb + newVertexBase, newVertexBase = newVertexBase, newVertexCapacity = vertexCap,
                newVertexTopology = topoOut,
                newIndices = ib + newIndexBase, newIndexBase = newIndexBase, newIndexCapacity = indexCap,
                outputRanges = outRanges,
                nodeEdgeKeys = nodeKeys, nodeParams = nodeParams, nodeCapacity = nodeCap,
                scratch = scratch, scratchBytes = scratchBytes,
            };

            LastInputHashBefore = HashInput(vb, poolV, ib, poolI, blocks, g);
            var result = new MeshCutResult();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (opt.ViaJob)
            {
                if (!m_jobResult.IsCreated) m_jobResult = new NativeArray<MeshCutResult>(1, Allocator.Persistent);
                m_jobResult[0] = default;
                new MeshCutJob { input = input, output = output, result = m_jobResult }.Schedule().Complete();
                result = m_jobResult[0];
            }
            else MeshCutKernel.Execute(in input, in output, ref result);
            LastMicroseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            LastResult = result;
            LastInputHashAfter = HashInput(vb, poolV, ib, poolI, blocks, g);
            LastBadGuards = m_arena.CheckGuards();

            if (opt.CheckUnrelatedUnchanged)
            {
                for (int i = 0; i < poolV; i++) if (!Same(vb[i], Pool.Vertices[i])) { LastUnrelatedChanges.Add("vertex " + i + " (existing pool) changed"); break; }
                for (int i = 0; i < poolI; i++) if (ib[i] != Pool.Indices[i]) { LastUnrelatedChanges.Add("index " + i + " (existing pool) changed"); break; }
                CheckFilled((byte*)(vb + poolV), (int)opt.VertexGap * sizeof(RenderVertex), opt.Fill, "vertex gap before the reservation");
                CheckFilled((byte*)(ib + poolI), (int)opt.IndexGap * sizeof(uint), opt.Fill, "index gap before the reservation");
                CheckFilled((byte*)(vb + newVertexBase + vertexCap), (int)opt.VertexGap * sizeof(RenderVertex), opt.Fill, "vertex tail after the reservation");
                CheckFilled((byte*)(ib + newIndexBase + indexCap), (int)opt.IndexGap * sizeof(uint), opt.Fill, "index tail after the reservation");
                if (result.status == MeshCutStatus.Ok)
                {
                    CheckFilled((byte*)(vb + newVertexBase + result.newVertexCount), (vertexCap - result.newVertexCount) * sizeof(RenderVertex), opt.Fill, "unused vertex reservation tail");
                    CheckFilled((byte*)(ib + newIndexBase + result.newIndexCount), (indexCap - result.newIndexCount) * sizeof(uint), opt.Fill, "unused index reservation tail");
                }
            }

            var run = new CutRun
            {
                Input = g, Plane = plane, Result = result, NewVertexBase = newVertexBase, TopologyBase = g.TopologyVertexCount,
                OutputRanges = new MeshCutIndexRange[2 * g.Ranges.Count],
            };
            for (int i = 0; i < 2 * g.Ranges.Count; i++) run.OutputRanges[i] = outRanges[i];
            if (result.status != MeshCutStatus.Ok) return run;

            // read the new block back into the pool
            run.NewVertexCount = result.newVertexCount;
            run.NewVertexTopology = new int[result.newVertexCount];
            for (int i = 0; i < result.newVertexCount; i++) run.NewVertexTopology[i] = topoOut[i];
            if (result.newVertexCount > 0)
            {
                GrowVertices((int)newVertexBase + result.newVertexCount);
                for (int i = 0; i < result.newVertexCount; i++) Pool.Vertices[newVertexBase + i] = vb[newVertexBase + i];
            }
            if (result.newIndexCount > 0)
            {
                GrowIndices((int)newIndexBase + result.newIndexCount);
                for (int i = 0; i < result.newIndexCount; i++) Pool.Indices[newIndexBase + i] = ib[newIndexBase + i];
            }
            if (opt.RequestNodes && (result.nodeCorrespondenceWritten != 0 || result.nodeCount == 0))
            {
                run.NodeKeys = new long[result.nodeCount];
                run.NodeParams = new float[result.nodeCount];
                for (int i = 0; i < result.nodeCount; i++) { run.NodeKeys[i] = nodeKeys[i]; run.NodeParams[i] = nodeParams[i]; }
            }
            var appended = result.newVertexCount > 0 ? new TopologyBlock { VertexBase = newVertexBase, TopologyVertex = run.NewVertexTopology } : null;
            run.Positive = SideGeometry(g, run, result.positive, 0, appended, result.newTopologyVertexCount, "+");
            run.Negative = SideGeometry(g, run, result.negative, 1, appended, result.newTopologyVertexCount, "-");
            return run;
        }

        /// <summary>
        /// Several cuts of the same immutable input scheduled together as Burst jobs, each with its own reservation and
        /// scratch inside the same global views (DESIGN 4.5.3: shared Published input, disjoint Reserved outputs).
        /// Results are read back into the pool one after another; verification of each run is the caller's.
        /// </summary>
        public CutRun[] CutConcurrent(CutGeometry g, float4[] planes, byte fill = 0xA5)
        {
            m_arena.Reset(fill);
            int poolV = Pool.Vertices.Length, poolI = Pool.Indices.Length;
            var blocks = m_arena.Alloc<RenderTopologyRange>("topology.blocks", g.Topology.Count);
            for (int b = 0; b < g.Topology.Count; b++)
            {
                int* ids = m_arena.Alloc<int>("topology.ids." + b, g.Topology[b].Count);
                for (int i = 0; i < g.Topology[b].Count; i++) ids[i] = g.Topology[b].TopologyVertex[i];
                blocks[b] = new RenderTopologyRange { vertexBase = g.Topology[b].VertexBase, count = g.Topology[b].Count, topologyVertex = ids };
            }
            var ranges = m_arena.Alloc<MeshCutIndexRange>("input.ranges", g.Ranges.Count);
            for (int i = 0; i < g.Ranges.Count; i++) ranges[i] = g.Ranges[i];
            var probeVB = m_arena.Alloc<RenderVertex>("probe.vb", poolV);
            var probeIB = m_arena.Alloc<uint>("probe.ib", poolI);
            for (int i = 0; i < poolV; i++) probeVB[i] = Pool.Vertices[i];
            for (int i = 0; i < poolI; i++) probeIB[i] = Pool.Indices[i];
            int n = planes.Length;
            var caps = new MeshCutCapacity[n];
            var inputs = new MeshCutInput[n];
            int totalV = 0, totalI = 0;
            for (int k = 0; k < n; k++)
            {
                inputs[k] = new MeshCutInput
                {
                    vertices = probeVB, vertexViewLength = poolV, indices = probeIB, indexViewLength = poolI,
                    ranges = ranges, rangeCount = g.Ranges.Count,
                    topology = new RenderCutTopologyMap { ranges = blocks, rangeCount = g.Topology.Count, topologyVertexCount = g.TopologyVertexCount },
                    plane = planes[k],
                };
                MeshCutKernel.QueryCapacity(in inputs[k], ref caps[k]);
                totalV += caps[k].newVertices + 8; totalI += caps[k].newIndices + 8;
            }
            int vbLength = poolV + 8 + totalV, ibLength = poolI + 8 + totalI;
            var vb = m_arena.Alloc<RenderVertex>("vb", vbLength);
            var ib = m_arena.Alloc<uint>("ib", ibLength);
            for (int i = 0; i < poolV; i++) vb[i] = Pool.Vertices[i];
            for (int i = 0; i < poolI; i++) ib[i] = Pool.Indices[i];
            var outputs = new MeshCutOutput[n];
            var results = new NativeArray<MeshCutResult>[n];
            for (int k = 0; k < n; k++) results[k] = new NativeArray<MeshCutResult>(1, Allocator.TempJob);
            var handles = new JobHandle[n];
            uint vCursor = (uint)poolV + 8, iCursor = (uint)poolI + 8;
            for (int k = 0; k < n; k++)
            {
                int nodeCap = Math.Max(1, 2 * caps[k].crossingTriangles);
                outputs[k] = new MeshCutOutput
                {
                    newVertices = vb + vCursor, newVertexBase = vCursor, newVertexCapacity = caps[k].newVertices,
                    newVertexTopology = m_arena.Alloc<int>("out.topology." + k, caps[k].newVertices),
                    newIndices = ib + iCursor, newIndexBase = iCursor, newIndexCapacity = caps[k].newIndices,
                    outputRanges = m_arena.Alloc<MeshCutIndexRange>("out.ranges." + k, 2 * g.Ranges.Count),
                    nodeEdgeKeys = m_arena.Alloc<long>("out.nodeKeys." + k, nodeCap), nodeParams = m_arena.Alloc<float>("out.nodeParams." + k, nodeCap), nodeCapacity = nodeCap,
                    scratch = m_arena.Alloc<byte>("scratch." + k, caps[k].scratchBytes), scratchBytes = caps[k].scratchBytes,
                };
                vCursor += (uint)caps[k].newVertices + 8; iCursor += (uint)caps[k].newIndices + 8;
                inputs[k].vertices = vb; inputs[k].vertexViewLength = vbLength; inputs[k].indices = ib; inputs[k].indexViewLength = ibLength;
            }
            for (int k = 0; k < n; k++)
                handles[k] = new MeshCutJob { input = inputs[k], output = outputs[k], result = results[k] }.Schedule();
            JobHandle.ScheduleBatchedJobs();
            for (int k = 0; k < n; k++) handles[k].Complete();
            LastBadGuards = m_arena.CheckGuards();
            LastUnrelatedChanges.Clear();
            for (int i = 0; i < poolV; i++) if (!Same(vb[i], Pool.Vertices[i])) { LastUnrelatedChanges.Add("vertex " + i + " changed"); break; }
            for (int i = 0; i < poolI; i++) if (ib[i] != Pool.Indices[i]) { LastUnrelatedChanges.Add("index " + i + " changed"); break; }

            var runs = new CutRun[n];
            for (int k = 0; k < n; k++)
            {
                var result = results[k][0];
                var run = new CutRun { Input = g, Plane = planes[k], Result = result, NewVertexBase = outputs[k].newVertexBase, TopologyBase = g.TopologyVertexCount, OutputRanges = new MeshCutIndexRange[2 * g.Ranges.Count] };
                for (int i = 0; i < 2 * g.Ranges.Count; i++) run.OutputRanges[i] = outputs[k].outputRanges[i];
                runs[k] = run;
                if (result.status != MeshCutStatus.Ok) continue;
                run.NewVertexCount = result.newVertexCount;
                run.NewVertexTopology = new int[result.newVertexCount];
                for (int i = 0; i < result.newVertexCount; i++) run.NewVertexTopology[i] = outputs[k].newVertexTopology[i];
                GrowVertices((int)outputs[k].newVertexBase + result.newVertexCount);
                for (int i = 0; i < result.newVertexCount; i++) Pool.Vertices[outputs[k].newVertexBase + i] = vb[outputs[k].newVertexBase + i];
                GrowIndices((int)outputs[k].newIndexBase + result.newIndexCount);
                for (int i = 0; i < result.newIndexCount; i++) Pool.Indices[outputs[k].newIndexBase + i] = ib[outputs[k].newIndexBase + i];
                if (result.nodeCorrespondenceWritten != 0)
                {
                    run.NodeKeys = new long[result.nodeCount]; run.NodeParams = new float[result.nodeCount];
                    for (int i = 0; i < result.nodeCount; i++) { run.NodeKeys[i] = outputs[k].nodeEdgeKeys[i]; run.NodeParams[i] = outputs[k].nodeParams[i]; }
                }
                var appended = result.newVertexCount > 0 ? new TopologyBlock { VertexBase = outputs[k].newVertexBase, TopologyVertex = run.NewVertexTopology } : null;
                run.Positive = SideGeometry(g, run, result.positive, 0, appended, result.newTopologyVertexCount, "+");
                run.Negative = SideGeometry(g, run, result.negative, 1, appended, result.newTopologyVertexCount, "-");
            }
            for (int k = 0; k < n; k++) results[k].Dispose();
            return runs;
        }

        static CutGeometry SideGeometry(CutGeometry parent, CutRun run, MeshCutSideResult side, int index, TopologyBlock appended, int topologyCount, string suffix)
        {
            var ranges = new List<MeshCutIndexRange>();
            if (side.reusesInput != 0) ranges.AddRange(parent.Ranges);
            else
                for (int i = 0; i < parent.Ranges.Count; i++) ranges.Add(run.OutputRanges[index * parent.Ranges.Count + i]);
            return parent.Child(parent.Name + suffix, ranges, appended, topologyCount);
        }

        void CheckFilled(byte* p, int bytes, byte fill, string what)
        {
            for (int i = 0; i < bytes; i++) if (p[i] != fill) { LastUnrelatedChanges.Add(what + " was written"); return; }
        }

        static bool Same(RenderVertex a, RenderVertex b) =>
            UnsafeUtility.MemCmp(&a, &b, sizeof(RenderVertex)) == 0;

        static ulong HashInput(RenderVertex* vb, int vbCount, uint* ib, int ibCount, RenderTopologyRange* blocks, CutGeometry g)
        {
            ulong h = 14695981039346656037UL;
            void Mix(byte* p, long n) { for (long i = 0; i < n; i++) { h ^= p[i]; h *= 1099511628211UL; } }
            Mix((byte*)vb, (long)vbCount * sizeof(RenderVertex));
            Mix((byte*)ib, (long)ibCount * sizeof(uint));
            for (int b = 0; b < g.Topology.Count; b++) Mix((byte*)blocks[b].topologyVertex, (long)blocks[b].count * 4);
            return h;
        }

        /// <summary>FNV-1a over the new block written by the last successful run (bitwise comparison across scratch fills).</summary>
        public ulong OutputHash(CutRun run)
        {
            ulong h = 14695981039346656037UL;
            void Mix(byte* p, long n) { for (long i = 0; i < n; i++) { h ^= p[i]; h *= 1099511628211UL; } }
            fixed (RenderVertex* v = Pool.Vertices) Mix((byte*)(v + run.NewVertexBase), (long)run.NewVertexCount * sizeof(RenderVertex));
            fixed (uint* idx = Pool.Indices) Mix((byte*)(idx + run.Result.positive.indexStart), (long)run.Result.newIndexCount * sizeof(uint));
            foreach (int t in run.NewVertexTopology ?? Array.Empty<int>()) { h ^= (uint)t; h *= 1099511628211UL; }
            foreach (var r in run.OutputRanges) { h ^= r.indexStart; h *= 1099511628211UL; h ^= (uint)r.indexCount; h *= 1099511628211UL; }
            return h;
        }

        public void Dispose()
        {
            if (m_jobResult.IsCreated) m_jobResult.Dispose();
            m_arena.Dispose();
        }
    }
}
