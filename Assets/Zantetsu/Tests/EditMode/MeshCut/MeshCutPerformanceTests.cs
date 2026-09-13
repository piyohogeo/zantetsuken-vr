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
    /// Phase 2.9 performance confirmation (DESIGN 14): the real Burst path of the product kernel, timed without the
    /// verifier and without any asset I/O, paired in the same run with the probe's adopted Burst implementation on the
    /// identical logical input (temporary test-only copy under ProbeBaseline). Numbers are recorded, not gated: the
    /// two do different work (the probe materializes both fragments in full with per-side vertex copies; the product
    /// kernel appends only new render vertices into the shared pool but writes the global index columns, handles
    /// attribute seams and the fixed cap UV), so the paired ratio is reported together with what differs.
    /// Conditions: Editor batch mode, Burst safety checks off, synchronous compilation, warm-up before sampling.
    /// </summary>
    public class MeshCutPerformanceTests
    {
        static readonly float3 k_origin = new float3(0.37f, -0.21f, 0.11f);
        const int k_warmupIterations = 200;
        const int k_samples = 300;

        struct Row
        {
            public string name; public int T, K; public double productUs, productSeamUs, probeUs; public int productNewV, probeOutV, productNewI, probeOutI; public string note;
        }

        static double Percentile(List<double> xs, double q) { var c = new List<double>(xs); c.Sort(); return c[Math.Min(c.Count - 1, (int)(c.Count * q))]; }

        [Test]
        public void ProductKernel_VersusProbeBaseline_OnIdenticalInputs()
        {
            bool safetyBefore = BurstCompiler.Options.EnableBurstSafetyChecks;
            BurstCompiler.Options.EnableBurstSafetyChecks = false;
            var rows = new List<Row>();
            try
            {
                var cases = new List<(string name, LogicalMeshBuilder builder, float4 plane)>
                {
                    ("box-n20 T=2400", SyntheticGeometry.Box(20, new float3(1, 1.3f, 0.8f), k_origin), SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), k_origin + new float3(0.0071f, -0.0233f, 0.0119f))),
                    ("sphere-64x32 T=3968", SyntheticGeometry.UvSphere(64, 32, 0.9f, k_origin), SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), k_origin + new float3(0.0071f, -0.0233f, 0.0119f))),
                    ("torus-96x48 T=9216", SyntheticGeometry.Torus(96, 48, 1f, 0.35f, k_origin), SyntheticGeometry.Plane(new float3(0.1f, 1f, 0.2f), k_origin + new float3(0, 0.0137f, 0))),
                    ("sphere-128x64 T=16128", SyntheticGeometry.UvSphere(128, 64, 0.9f, k_origin), SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), k_origin + new float3(0.0071f, -0.0233f, 0.0119f))),
                    ("star-prism-32 T=1536", SyntheticGeometry.StarPrism(32, 1f, 0.4f, 2f, 8, k_origin), SyntheticGeometry.Plane(new float3(0.05f, 0.02f, 1f), k_origin + new float3(0, 0, 0.0137f))),
                    ("lemniscate-128 self-intersecting T=2304", SyntheticGeometry.LemniscateTube(128, 8, 1f, 2f, k_origin), SyntheticGeometry.Plane(new float3(0.05f, 0.02f, 1f), k_origin + new float3(0, 0, 0.0137f))),
                    ("sphere-128x64 grazing", SyntheticGeometry.UvSphere(128, 64, 0.9f, k_origin), SyntheticGeometry.Plane(new float3(1, 0, 0), k_origin + new float3(0.871f, 0, 0))),
                };
                foreach (var (name, builder, plane) in cases)
                {
                    var smooth = builder.Finish(new LogicalMeshBuilder.AttributeOptions());
                    var seams = builder.Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
                    var row = new Row { name = name, T = smooth.TriangleCount };
                    // probe baseline: logical SoA mesh with the game vertex (normal, uv, tangent) per logical vertex
                    int V = builder.Positions.Count;
                    var px = new float[V]; var py = new float[V]; var pz = new float[V]; var attr = new float[9 * V];
                    for (int v = 0; v < V; v++)
                    {
                        RenderVertex rv = smooth.Vertices[v];   // smooth layer: one render vertex per logical vertex, same order
                        px[v] = rv.position.x; py[v] = rv.position.y; pz[v] = rv.position.z;
                        attr[9 * v] = rv.normal.x; attr[9 * v + 1] = rv.normal.y; attr[9 * v + 2] = rv.normal.z;
                        attr[9 * v + 3] = rv.uv0.x; attr[9 * v + 4] = rv.uv0.y;
                        attr[9 * v + 5] = rv.tangent.x; attr[9 * v + 6] = rv.tangent.y; attr[9 * v + 7] = rv.tangent.z; attr[9 * v + 8] = rv.tangent.w;
                    }
                    var indices = new int[smooth.Indices.Length];
                    for (int i = 0; i < indices.Length; i++) indices[i] = (int)smooth.Indices[i];
                    float3 mn = new float3(float.MaxValue), mx = new float3(float.MinValue);
                    foreach (var p in builder.Positions) { mn = math.min(mn, p); mx = math.max(mx, p); }
                    double eps = 1e-6 * math.cmax(mx - mn);

                    using (var probe = new ProbeBaseline.BurstScanEdgeHashCut())
                    using (var product = new KernelBench(smooth))
                    using (var productSeams = new KernelBench(seams))
                    {
                        probe.SetMesh(px, py, pz, indices, attr);
                        var probeSamples = new List<double>(k_samples); var productSamples = new List<double>(k_samples); var seamSamples = new List<double>(k_samples);
                        double nsPerTick = 1e9 / System.Diagnostics.Stopwatch.Frequency;
                        // warm-up (Burst compiles synchronously on first use; the Editor may still be compiling other jobs in the background)
                        for (int i = 0; i < k_warmupIterations; i++)
                        {
                            probe.Cut(plane.x, plane.y, plane.z, plane.w, eps, true, out _);
                            product.Run(plane); productSeams.Run(plane);
                        }
                        Assert.That(product.LastResult.status, Is.EqualTo(MeshCutStatus.Ok), name + " product status");
                        Assert.That(product.LastResult.executedManaged, Is.EqualTo(0), name + " product ran managed");
                        Assert.That(productSeams.LastResult.status, Is.EqualTo(MeshCutStatus.Ok), name + " product (seams) status");
                        Assert.That(probe.LastCounters.Unsupported, Is.False, name + " probe declined the case");
                        for (int i = 0; i < k_samples; i++)
                        {
                            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            probe.Cut(plane.x, plane.y, plane.z, plane.w, eps, true, out _);
                            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                            product.Run(plane);
                            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                            productSeams.Run(plane);
                            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                            probeSamples.Add((t1 - t0) * nsPerTick / 1e3); productSamples.Add((t2 - t1) * nsPerTick / 1e3); seamSamples.Add((t3 - t2) * nsPerTick / 1e3);
                        }
                        row.K = product.LastResult.crossingTriangles;
                        row.probeUs = Percentile(probeSamples, 0.5); row.productUs = Percentile(productSamples, 0.5); row.productSeamUs = Percentile(seamSamples, 0.5);
                        row.productNewV = product.LastResult.newVertexCount; row.productNewI = product.LastResult.newIndexCount;
                        row.probeOutV = probe.PositiveVertexCount + probe.NegativeVertexCount; row.probeOutI = probe.PositiveIndexCount + probe.NegativeIndexCount;
                        row.note = "K probe=" + probe.LastCounters.CrossingTriangles + " caps product/probe=" + product.LastResult.capTriangles + "/" + probe.LastCounters.CapTriangles +
                                   " p10/p90 product=" + Percentile(productSamples, 0.1).ToString("F1", CultureInfo.InvariantCulture) + "/" + Percentile(productSamples, 0.9).ToString("F1", CultureInfo.InvariantCulture) +
                                   " probe=" + Percentile(probeSamples, 0.1).ToString("F1", CultureInfo.InvariantCulture) + "/" + Percentile(probeSamples, 0.9).ToString("F1", CultureInfo.InvariantCulture) +
                                   " seamsRenderV=" + seams.Vertices.Length + "/" + smooth.Vertices.Length;
                        Assert.That(probe.LastCounters.CrossingTriangles, Is.EqualTo(row.K), name + ": same K on the same input");
                    }
                    rows.Add(row);
                }
            }
            finally { BurstCompiler.Options.EnableBurstSafetyChecks = safetyBefore; }

            var sb = new StringBuilder();
            sb.AppendLine("Phase 2.9 performance (median us per cut, Burst, safety checks off, warm-up " + k_warmupIterations + ", samples " + k_samples + ", interleaved probe/product/product+seams)");
            sb.AppendLine("case | T | K | probe us | product us | product+seams us | product/probe | newV product / outV probe | newI product / outI probe | notes");
            foreach (var r in rows)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} | {1} | {2} | {3:F1} | {4:F1} | {5:F1} | {6:F2} | {7} / {8} | {9} / {10} | {11}",
                    r.name, r.T, r.K, r.probeUs, r.productUs, r.productSeamUs, r.productUs / r.probeUs, r.productNewV, r.probeOutV, r.productNewI, r.probeOutI, r.note));
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
                // views sized for the reservation after a first capacity query over a minimal view
                var vb0 = new NativeArray<RenderVertex>(mesh.Vertices, Allocator.Temp);
                var ib0 = new NativeArray<uint>(mesh.Indices, Allocator.Temp);
                m_input = new MeshCutInput
                {
                    vertices = (RenderVertex*)vb0.GetUnsafeReadOnlyPtr(), vertexViewLength = V, indices = (uint*)ib0.GetUnsafeReadOnlyPtr(), indexViewLength = I,
                    ranges = (MeshCutIndexRange*)m_ranges.GetUnsafePtr(), rangeCount = m_ranges.Length,
                    topology = new RenderCutTopologyMap { ranges = (RenderTopologyRange*)m_blocks.GetUnsafePtr(), rangeCount = 1, topologyVertexCount = mesh.TopologyVertexCount },
                    plane = new float4(0, 1, 0, 0),
                };
                // capacity for the worst of a few planes is not needed: reserve generously (2x the crossing bound of any plane)
                int vCap = 8 * (I / 3) + 4096, iCap = 3 * (I / 3 + 6 * (I / 3)) + 4096;
                m_vb = new NativeArray<RenderVertex>(V + vCap, Allocator.Persistent);
                m_ib = new NativeArray<uint>(I + iCap, Allocator.Persistent);
                NativeArray<RenderVertex>.Copy(vb0, m_vb, V); NativeArray<uint>.Copy(ib0, m_ib, I);
                vb0.Dispose(); ib0.Dispose();
                m_outTopo = new NativeArray<int>(vCap, Allocator.Persistent);
                m_outRanges = new NativeArray<MeshCutIndexRange>(2 * m_ranges.Length, Allocator.Persistent);
                m_scratch = new NativeArray<byte>(256 * (2 * (I / 3)) + (1 << 20), Allocator.Persistent);
                m_input.vertices = (RenderVertex*)m_vb.GetUnsafePtr(); m_input.vertexViewLength = m_vb.Length;
                m_input.indices = (uint*)m_ib.GetUnsafePtr(); m_input.indexViewLength = m_ib.Length;
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
