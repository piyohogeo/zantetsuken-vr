using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.MeshCut.Verification;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Phase 2.9 performance record (DESIGN 14): the real Burst path of the product kernel timed without the verifier,
    /// without asset I/O, without allocation and without readback, on public synthetic inputs. Three timed regions per
    /// case, interleaved sample by sample:
    ///  - Execute alone over a reservation sized in advance (the figure the first record reported);
    ///  - QueryCapacity alone (its own classification pass over every corner, without the memo);
    ///  - the call total from the caller's point of view: QueryCapacity, then Execute over exactly the queried
    ///    reservation. The pool reservation itself is Phase 3 work and is not timed (fixed arrays are reused).
    /// A reservation-deficit region times a vertex reservation one short (Execute does all its work up to the capacity
    /// verdict, then fails) plus the re-run with the exact figure. Numbers are recorded, not gated; the only verdicts
    /// are that Burst ran and the cuts succeeded. The paired comparison with the probe's Burst implementation was made
    /// with a temporary test-only copy of the probe kernel (repository history, commit "Add the mesh cut verification
    /// harness ...") and is recorded in docs/phase-2.9-mesh-cut-kernel/record.md; the copy is not kept.
    /// Conditions: Editor batch mode, Burst safety checks off, synchronous compilation, warm-up before sampling.
    /// </summary>
    public class MeshCutPerformanceTests
    {
        static readonly float3 k_origin = new float3(0.37f, -0.21f, 0.11f);
        const int k_warmupIterations = 200;
        const int k_samples = 300;
        const int k_deficitSamples = 50;

        static double Percentile(List<double> xs, double q) { var c = new List<double>(xs); c.Sort(); return c[Math.Min(c.Count - 1, (int)(c.Count * q))]; }
        static string F1(double x) => x.ToString("F1", CultureInfo.InvariantCulture);

        public static List<(string name, LogicalMeshBuilder builder, float4 plane)> Cases() => new List<(string, LogicalMeshBuilder, float4)>
        {
            ("box-n20", SyntheticGeometry.Box(20, new float3(1, 1.3f, 0.8f), k_origin), SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), k_origin + new float3(0.0071f, -0.0233f, 0.0119f))),
            ("sphere-64x32", SyntheticGeometry.UvSphere(64, 32, 0.9f, k_origin), SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), k_origin + new float3(0.0071f, -0.0233f, 0.0119f))),
            ("torus-96x48", SyntheticGeometry.Torus(96, 48, 1f, 0.35f, k_origin), SyntheticGeometry.Plane(new float3(0.1f, 1f, 0.2f), k_origin + new float3(0, 0.0137f, 0))),
            ("sphere-128x64", SyntheticGeometry.UvSphere(128, 64, 0.9f, k_origin), SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), k_origin + new float3(0.0071f, -0.0233f, 0.0119f))),
            ("star-prism-32 cap-heavy", SyntheticGeometry.StarPrism(32, 1f, 0.4f, 2f, 8, k_origin), SyntheticGeometry.Plane(new float3(0.05f, 0.02f, 1f), k_origin + new float3(0, 0, 0.0137f))),
            ("lemniscate-128 self-intersecting", SyntheticGeometry.LemniscateTube(128, 8, 1f, 2f, k_origin), SyntheticGeometry.Plane(new float3(0.05f, 0.02f, 1f), k_origin + new float3(0, 0, 0.0137f))),
            ("sphere-128x64 grazing large-T small-K", SyntheticGeometry.UvSphere(128, 64, 0.9f, k_origin), SyntheticGeometry.Plane(new float3(1, 0, 0), k_origin + new float3(0.871f, 0, 0))),
            ("torus-96x48 grazing many loops", SyntheticGeometry.Torus(96, 48, 1f, 0.35f, k_origin), SyntheticGeometry.Plane(new float3(0, 1, 0), k_origin + new float3(0, 0.3413f, 0))),
        };

        [Test]
        public void ProductKernel_BurstPath_OnPublicInputs()
        {
            bool safetyBefore = BurstCompiler.Options.EnableBurstSafetyChecks;
            BurstCompiler.Options.EnableBurstSafetyChecks = false;
            var sb = new StringBuilder();
            sb.AppendLine("Phase 2.9 performance (median us per cut, Burst, safety checks off, warm-up " + k_warmupIterations + ", samples " + k_samples + ", interleaved execute / query / query+execute / seams)");
            sb.AppendLine("case | T | K | execute us (p10/p90) | query us | query+execute us (p10/p90) | total/execute | seams execute us | deficit: failed + rerun us | queried newV bound / used | queried newI bound / used | caps | cycles split/comb | seams renderV / smooth renderV");
            try
            {
                foreach (var (name, builder, plane) in Cases())
                {
                    var smooth = builder.Finish(new LogicalMeshBuilder.AttributeOptions());
                    var seams = builder.Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
                    using (var product = new KernelBench(smooth))
                    using (var productSeams = new KernelBench(seams))
                    {
                        var execSamples = new List<double>(k_samples); var querySamples = new List<double>(k_samples);
                        var totalSamples = new List<double>(k_samples); var seamSamples = new List<double>(k_samples);
                        var failedSamples = new List<double>(k_deficitSamples); var rerunSamples = new List<double>(k_deficitSamples);
                        double nsPerTick = 1e9 / System.Diagnostics.Stopwatch.Frequency;
                        for (int i = 0; i < k_warmupIterations; i++) { product.Run(plane); product.Query(plane); product.RunQueried(plane); productSeams.Run(plane); }
                        Assert.That(product.LastResult.status, Is.EqualTo(MeshCutStatus.Ok), name + " product status (queried reservation)");
                        Assert.That(product.LastResult.executedManaged, Is.EqualTo(0), name + " product ran managed instead of Burst");
                        Assert.That(productSeams.LastResult.status, Is.EqualTo(MeshCutStatus.Ok), name + " product (seams) status");
                        Assert.That(productSeams.LastResult.executedManaged, Is.EqualTo(0), name + " product (seams) ran managed instead of Burst");
                        for (int i = 0; i < k_samples; i++)
                        {
                            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            product.Run(plane);
                            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                            product.Query(plane);
                            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                            product.RunQueried(plane);
                            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                            productSeams.Run(plane);
                            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
                            execSamples.Add((t1 - t0) * nsPerTick / 1e3); querySamples.Add((t2 - t1) * nsPerTick / 1e3);
                            totalSamples.Add((t3 - t2) * nsPerTick / 1e3); seamSamples.Add((t4 - t3) * nsPerTick / 1e3);
                        }
                        var r = product.LastResult;
                        var cap = product.LastCapacity;
                        for (int i = 0; i < k_deficitSamples; i++)
                        {
                            var (failed, rerun) = product.RunDeficit(plane, r.newVertexCount);
                            failedSamples.Add(failed); rerunSamples.Add(rerun);
                        }
                        Assert.That(product.LastResult.status, Is.EqualTo(MeshCutStatus.Ok), name + " re-run after the deficit");
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} | {1} | {2} | {3} ({4}/{5}) | {6} | {7} ({8}/{9}) | {10:F2} | {11} | {12} + {13} | {14} / {15} | {16} / {17} | {18} | {19}/{20} | {21} / {22}",
                            name, r.triangleCount, r.crossingTriangles, F1(Percentile(execSamples, 0.5)), F1(Percentile(execSamples, 0.1)), F1(Percentile(execSamples, 0.9)),
                            F1(Percentile(querySamples, 0.5)), F1(Percentile(totalSamples, 0.5)), F1(Percentile(totalSamples, 0.1)), F1(Percentile(totalSamples, 0.9)),
                            Percentile(totalSamples, 0.5) / Percentile(execSamples, 0.5), F1(Percentile(seamSamples, 0.5)),
                            F1(Percentile(failedSamples, 0.5)), F1(Percentile(rerunSamples, 0.5)),
                            cap.newVertices, r.newVertexCount, cap.newIndices, r.newIndexCount, r.capTriangles, r.capSplitCycles, r.capCombinatorialCycles, seams.Vertices.Length, smooth.Vertices.Length));
                    }
                }
            }
            finally { BurstCompiler.Options.EnableBurstSafetyChecks = safetyBefore; }
            TestContext.Out.WriteLine(sb.ToString());
        }
    }

    /// <summary>
    /// Product kernel over fixed native views and one reused reservation (no verifier, no allocation, no readback).
    /// Run: Execute alone over the generous fixed reservation. Query: QueryCapacity alone. RunQueried: QueryCapacity then
    /// Execute over exactly the queried figures. RunDeficit: Execute with the vertex reservation one short of the exact
    /// need (fails at the capacity verdict) then with the reported exact need.
    /// </summary>
    public sealed unsafe class KernelBench : IDisposable
    {
        NativeArray<RenderVertex> m_vb;
        NativeArray<uint> m_ib;
        NativeArray<int> m_topo, m_outTopo;
        NativeArray<byte> m_scratch;
        NativeArray<MeshCutIndexRange> m_ranges, m_outRanges;
        NativeArray<RenderTopologyRange> m_blocks;
        MeshCutInput m_input;
        MeshCutOutput m_output;
        public MeshCutResult LastResult;
        public MeshCutCapacity LastCapacity;

        public KernelBench(SyntheticMesh mesh)
        {
            int V = mesh.Vertices.Length, I = mesh.Indices.Length;
            m_topo = new NativeArray<int>(mesh.TopologyOfVertex, Allocator.Persistent);
            m_ranges = new NativeArray<MeshCutIndexRange>(mesh.SubmeshIndexCounts.Count, Allocator.Persistent);
            uint start = 0;
            for (int s = 0; s < mesh.SubmeshIndexCounts.Count; s++) { m_ranges[s] = new MeshCutIndexRange { indexStart = start, indexCount = mesh.SubmeshIndexCounts[s] }; start += (uint)mesh.SubmeshIndexCounts[s]; }
            m_blocks = new NativeArray<RenderTopologyRange>(1, Allocator.Persistent);
            m_blocks[0] = new RenderTopologyRange { vertexBase = 0, count = V, topologyVertex = (int*)m_topo.GetUnsafePtr() };
            // generous reservations for any plane (the bound of the capacity query is 12K + 2 aux vertices, 3(T + 6K + 4 aux) indices)
            int vCap = 12 * (I / 3) + 4096, iCap = 3 * (I / 3 + 6 * (I / 3)) + 4096;
            m_vb = new NativeArray<RenderVertex>(V + vCap, Allocator.Persistent);
            m_ib = new NativeArray<uint>(I + iCap, Allocator.Persistent);
            NativeArray<RenderVertex>.Copy(mesh.Vertices, m_vb, V); NativeArray<uint>.Copy(mesh.Indices, m_ib, I);
            m_outTopo = new NativeArray<int>(vCap, Allocator.Persistent);
            m_outRanges = new NativeArray<MeshCutIndexRange>(2 * m_ranges.Length, Allocator.Persistent);
            m_scratch = new NativeArray<byte>(512 * (2 * (I / 3)) + (1 << 20) + 16 * mesh.TopologyVertexCount, Allocator.Persistent);
            m_input = new MeshCutInput
            {
                vertices = (RenderVertex*)m_vb.GetUnsafePtr(), vertexViewLength = m_vb.Length, indices = (uint*)m_ib.GetUnsafePtr(), indexViewLength = m_ib.Length,
                ranges = (MeshCutIndexRange*)m_ranges.GetUnsafePtr(), rangeCount = m_ranges.Length,
                topology = new RenderCutTopologyMap { ranges = (RenderTopologyRange*)m_blocks.GetUnsafePtr(), rangeCount = 1, topologyVertexCount = mesh.TopologyVertexCount },
                plane = new float4(0, 1, 0, 0),
            };
            m_output = new MeshCutOutput
            {
                newVertices = (RenderVertex*)m_vb.GetUnsafePtr() + V, newVertexBase = (uint)V, newVertexCapacity = vCap,
                newVertexTopology = (int*)m_outTopo.GetUnsafePtr(),
                newIndices = (uint*)m_ib.GetUnsafePtr() + I, newIndexBase = (uint)I, newIndexCapacity = iCap,
                outputRanges = (MeshCutIndexRange*)m_outRanges.GetUnsafePtr(),
                scratch = (byte*)m_scratch.GetUnsafePtr(), scratchBytes = m_scratch.Length,
            };
        }

        public void Run(float4 plane)
        {
            m_input.plane = plane;
            var r = new MeshCutResult();
            MeshCutKernel.Execute(in m_input, in m_output, ref r);
            LastResult = r;
        }

        public void Query(float4 plane)
        {
            m_input.plane = plane;
            var c = new MeshCutCapacity();
            MeshCutKernel.QueryCapacity(in m_input, ref c);
            LastCapacity = c;
        }

        public void RunQueried(float4 plane)
        {
            Query(plane);
            var o = m_output;
            o.newVertexCapacity = Math.Min(o.newVertexCapacity, LastCapacity.newVertices);
            o.newIndexCapacity = Math.Min(o.newIndexCapacity, LastCapacity.newIndices);
            o.scratchBytes = Math.Min(o.scratchBytes, LastCapacity.scratchBytes);
            var r = new MeshCutResult();
            MeshCutKernel.Execute(in m_input, in o, ref r);
            LastResult = r;
        }

        /// <summary>Microseconds of the failed attempt (vertex reservation one short of <paramref name="exactVertices"/>) and of the re-run with the reported need.</summary>
        public (double failedUs, double rerunUs) RunDeficit(float4 plane, int exactVertices)
        {
            double nsPerTick = 1e9 / System.Diagnostics.Stopwatch.Frequency;
            m_input.plane = plane;
            var o = m_output;
            o.newVertexCapacity = exactVertices - 1;
            var r = new MeshCutResult();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            MeshCutKernel.Execute(in m_input, in o, ref r);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (r.status != MeshCutStatus.CapacityVertex) throw new InvalidOperationException("deficit attempt returned " + r.status);
            o.newVertexCapacity = r.requiredVertexCapacity;
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            MeshCutKernel.Execute(in m_input, in o, ref r);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            LastResult = r;
            return ((t1 - t0) * nsPerTick / 1e3, (t3 - t2) * nsPerTick / 1e3);
        }

        public void Dispose()
        {
            m_vb.Dispose(); m_ib.Dispose(); m_topo.Dispose(); m_outTopo.Dispose(); m_scratch.Dispose(); m_ranges.Dispose(); m_outRanges.Dispose(); m_blocks.Dispose();
        }
    }
}
