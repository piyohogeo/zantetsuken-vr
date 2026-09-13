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
    /// without asset I/O and without readback, on public synthetic inputs, with and without attribute seams. Numbers
    /// are recorded, not gated; the only verdicts are that Burst ran and the cuts succeeded. The paired comparison
    /// with the probe's Burst implementation on identical inputs was made once with a temporary test-only copy of the
    /// probe kernel (repository history, "Add the mesh cut verification harness ...") and is recorded in
    /// docs/phase-2.9-mesh-cut-kernel/record.md; the copy is not kept as a second kernel.
    /// Conditions: Editor batch mode, Burst safety checks off, synchronous compilation, warm-up before sampling.
    /// </summary>
    public class MeshCutPerformanceTests
    {
        static readonly float3 k_origin = new float3(0.37f, -0.21f, 0.11f);
        const int k_warmupIterations = 200;
        const int k_samples = 300;

        static double Percentile(List<double> xs, double q) { var c = new List<double>(xs); c.Sort(); return c[Math.Min(c.Count - 1, (int)(c.Count * q))]; }

        [Test]
        public void ProductKernel_BurstPath_OnPublicInputs()
        {
            bool safetyBefore = BurstCompiler.Options.EnableBurstSafetyChecks;
            BurstCompiler.Options.EnableBurstSafetyChecks = false;
            var sb = new StringBuilder();
            sb.AppendLine("Phase 2.9 performance (median us per cut, Burst, safety checks off, warm-up " + k_warmupIterations + ", samples " + k_samples + ", interleaved smooth / seams)");
            sb.AppendLine("case | T | K | product us (p10/p90) | product+seams us | newV | newI | caps | seams renderV / smooth renderV");
            try
            {
                var cases = new List<(string name, LogicalMeshBuilder builder, float4 plane)>
                {
                    ("box-n20", SyntheticGeometry.Box(20, new float3(1, 1.3f, 0.8f), k_origin), SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), k_origin + new float3(0.0071f, -0.0233f, 0.0119f))),
                    ("sphere-64x32", SyntheticGeometry.UvSphere(64, 32, 0.9f, k_origin), SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), k_origin + new float3(0.0071f, -0.0233f, 0.0119f))),
                    ("torus-96x48", SyntheticGeometry.Torus(96, 48, 1f, 0.35f, k_origin), SyntheticGeometry.Plane(new float3(0.1f, 1f, 0.2f), k_origin + new float3(0, 0.0137f, 0))),
                    ("sphere-128x64", SyntheticGeometry.UvSphere(128, 64, 0.9f, k_origin), SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), k_origin + new float3(0.0071f, -0.0233f, 0.0119f))),
                    ("star-prism-32", SyntheticGeometry.StarPrism(32, 1f, 0.4f, 2f, 8, k_origin), SyntheticGeometry.Plane(new float3(0.05f, 0.02f, 1f), k_origin + new float3(0, 0, 0.0137f))),
                    ("lemniscate-128 self-intersecting", SyntheticGeometry.LemniscateTube(128, 8, 1f, 2f, k_origin), SyntheticGeometry.Plane(new float3(0.05f, 0.02f, 1f), k_origin + new float3(0, 0, 0.0137f))),
                    ("sphere-128x64 grazing", SyntheticGeometry.UvSphere(128, 64, 0.9f, k_origin), SyntheticGeometry.Plane(new float3(1, 0, 0), k_origin + new float3(0.871f, 0, 0))),
                };
                foreach (var (name, builder, plane) in cases)
                {
                    var smooth = builder.Finish(new LogicalMeshBuilder.AttributeOptions());
                    var seams = builder.Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
                    using (var product = new KernelBench(smooth))
                    using (var productSeams = new KernelBench(seams))
                    {
                        var productSamples = new List<double>(k_samples); var seamSamples = new List<double>(k_samples);
                        double nsPerTick = 1e9 / System.Diagnostics.Stopwatch.Frequency;
                        for (int i = 0; i < k_warmupIterations; i++) { product.Run(plane); productSeams.Run(plane); }
                        Assert.That(product.LastResult.status, Is.EqualTo(MeshCutStatus.Ok), name + " product status");
                        Assert.That(product.LastResult.executedManaged, Is.EqualTo(0), name + " product ran managed instead of Burst");
                        Assert.That(productSeams.LastResult.status, Is.EqualTo(MeshCutStatus.Ok), name + " product (seams) status");
                        Assert.That(productSeams.LastResult.executedManaged, Is.EqualTo(0), name + " product (seams) ran managed instead of Burst");
                        for (int i = 0; i < k_samples; i++)
                        {
                            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            product.Run(plane);
                            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                            productSeams.Run(plane);
                            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                            productSamples.Add((t1 - t0) * nsPerTick / 1e3); seamSamples.Add((t2 - t1) * nsPerTick / 1e3);
                        }
                        var r = product.LastResult;
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} | {1} | {2} | {3:F1} ({4:F1}/{5:F1}) | {6:F1} | {7} | {8} | {9} | {10} / {11}",
                            name, r.triangleCount, r.crossingTriangles, Percentile(productSamples, 0.5), Percentile(productSamples, 0.1), Percentile(productSamples, 0.9),
                            Percentile(seamSamples, 0.5), r.newVertexCount, r.newIndexCount, r.capTriangles, seams.Vertices.Length, smooth.Vertices.Length));
                    }
                }
            }
            finally { BurstCompiler.Options.EnableBurstSafetyChecks = safetyBefore; }
            TestContext.Out.WriteLine(sb.ToString());
        }

        /// <summary>Product kernel over fixed native views and one reused reservation (no verifier, no readback): the timed region is Execute alone.</summary>
        sealed unsafe class KernelBench : IDisposable
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

            public KernelBench(SyntheticMesh mesh)
            {
                int V = mesh.Vertices.Length, I = mesh.Indices.Length;
                m_topo = new NativeArray<int>(mesh.TopologyOfVertex, Allocator.Persistent);
                m_ranges = new NativeArray<MeshCutIndexRange>(mesh.SubmeshIndexCounts.Count, Allocator.Persistent);
                uint start = 0;
                for (int s = 0; s < mesh.SubmeshIndexCounts.Count; s++) { m_ranges[s] = new MeshCutIndexRange { indexStart = start, indexCount = mesh.SubmeshIndexCounts[s] }; start += (uint)mesh.SubmeshIndexCounts[s]; }
                m_blocks = new NativeArray<RenderTopologyRange>(1, Allocator.Persistent);
                m_blocks[0] = new RenderTopologyRange { vertexBase = 0, count = V, topologyVertex = (int*)m_topo.GetUnsafePtr() };
                // generous reservations for any plane (the bound of the capacity query is 8K + aux vertices, 3(T + 6K + 4 aux) indices)
                int vCap = 8 * (I / 3) + 4096, iCap = 3 * (I / 3 + 6 * (I / 3)) + 4096;
                m_vb = new NativeArray<RenderVertex>(V + vCap, Allocator.Persistent);
                m_ib = new NativeArray<uint>(I + iCap, Allocator.Persistent);
                NativeArray<RenderVertex>.Copy(mesh.Vertices, m_vb, V); NativeArray<uint>.Copy(mesh.Indices, m_ib, I);
                m_outTopo = new NativeArray<int>(vCap, Allocator.Persistent);
                m_outRanges = new NativeArray<MeshCutIndexRange>(2 * m_ranges.Length, Allocator.Persistent);
                m_scratch = new NativeArray<byte>(256 * (2 * (I / 3)) + (1 << 20) + 16 * mesh.TopologyVertexCount, Allocator.Persistent);
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

            public void Dispose()
            {
                m_vb.Dispose(); m_ib.Dispose(); m_topo.Dispose(); m_outTopo.Dispose(); m_scratch.Dispose(); m_ranges.Dispose(); m_outRanges.Dispose(); m_blocks.Dispose();
            }
        }
    }
}
