using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// NPC-BONE-SPLIT-1: the cut kernel's optional record of where each new render vertex came from, through the asynchronous
    /// storage cut -- asked for by the options, made for the reservation, handed to the caller with a successful result, and
    /// nothing at all when not asked for. Every new vertex is checked against its origin: an interpolated one lies on the
    /// segment between its two input vertices, which the plane separates; one at a parameter of 0 or 1 is its one vertex; a
    /// cap's auxiliary vertex names none and lies on the plane.
    /// </summary>
    public unsafe class VpAsyncStorageCutOriginsTests
    {
        private const int DeadlineMilliseconds = 30000;

        private sealed class Fixture : IDisposable
        {
            public VpCpuGeometryStorage storage;
            public UnityJobWorkExecutor job;
            public WorkerPoolExecutor geometry;
            public WorkerPoolExecutor background;
            public SharedWorkDispatcher dispatcher;
            public VpAsyncStorageCut runner;
            private int _frame;

            public void RunUntilOver(VpStorageCutRequest request)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    dispatcher.BeginFrame(++_frame);
                    dispatcher.Dispatch();
                    runner.Pump();
                    if (request.IsOver) return;
                    Thread.Sleep(1);
                }

                Assert.Fail("the cut was still " + request.Stage + " when the deadline passed");
            }

            public void Dispose()
            {
                runner?.Dispose();
                dispatcher?.Shutdown(DeadlineMilliseconds);
                runner?.Pump();
                geometry?.Dispose();
                background?.Dispose();
                storage?.Dispose();
            }
        }

        private static Fixture NewFixture()
        {
            var f = new Fixture
            {
                storage = new VpCpuGeometryStorage(4096, 16384, 64, 256, 256, Allocator.Persistent),
                job = new UnityJobWorkExecutor(4),
                geometry = WorkerPoolExecutor.GeometryPool(2),
                background = WorkerPoolExecutor.BackgroundPool(1),
            };
            f.dispatcher = new SharedWorkDispatcher(8, 2, 32, f.job, f.geometry, f.background);
            f.runner = new VpAsyncStorageCut(f.storage, f.dispatcher);
            return f;
        }

        private static VpStoredGeometry AppendBox(VpCpuGeometryStorage storage, int divisions = 3)
        {
            SyntheticMesh mesh = SyntheticGeometry.Box(divisions, new float3(1f, 1f, 1f), float3.zero, true)
                .Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
            var submeshes = new VpGeometrySubmesh[mesh.SubmeshIndexCounts.Count];
            int offset = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                submeshes[s] = new VpGeometrySubmesh(offset, mesh.SubmeshIndexCounts[s], s);
                offset += mesh.SubmeshIndexCounts[s];
            }

            Assert.That(storage.TryAppendCuttable(mesh.Vertices, mesh.Indices, mesh.TopologyOfVertex, mesh.TopologyVertexCount, submeshes,
                out VpStoredGeometry geometry, out _), Is.True, "the box is appended as a cut input");
            return geometry;
        }

        private static VpStorageCutRequest Cut(Fixture f, VpStoredGeometry box, float4 plane, in VpStorageCutOptions options)
        {
            Assert.That(VpStorageCutInput.TryAcquire(f.storage, box, out VpStorageCutInput input), Is.True);
            VpStorageCutRequest request = f.runner.Submit(input, plane, options);
            f.RunUntilOver(request);
            Assert.That(request.Stage, Is.EqualTo(VpStorageCutStage.Finished));
            Assert.That(request.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut succeeded");
            return request;
        }

        private static float Side(float4 plane, float3 p) => math.dot(plane.xyz, p) + plane.w;

        private static float DistanceToSegment(float3 p, float3 a, float3 b)
        {
            float3 ab = b - a;
            float t = math.lengthsq(ab) > 0f ? math.saturate(math.dot(p - a, ab) / math.lengthsq(ab)) : 0f;
            return math.length(p - (a + (t * ab)));
        }

        /// <summary>Every new vertex's origin agrees with where the vertex is, for a plane with no vertex on it and for one through vertices.</summary>
        [TestCase(0.37f, 0.61f, -0.7f, 0.0071f, -0.0233f, 0.0119f, 0, 3)]
        [TestCase(0f, 1f, 0f, 0f, 0f, 0f, 0, 2)]         // through the middle row of the box's vertices (y = 0): parameters of 0 and 1
        [TestCase(0.37f, 0.61f, -0.7f, 0.0071f, -0.0233f, 0.0119f, 1, 3)]   // a first reservation of one vertex: the retry's larger array
        public void TheOrigins_AgreeWithEveryNewVertex(float nx, float ny, float nz, float px, float py, float pz, int firstVertexRoom, int divisions)
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry box = AppendBox(f.storage, divisions);
                float4 plane = SyntheticGeometry.Plane(new float3(nx, ny, nz), new float3(px, py, pz));
                var options = new VpStorageCutOptions { wantVertexOrigins = true, newVertexCapacity = firstVertexRoom };
                VpStorageCutRequest request = Cut(f, box, plane, options);
                MeshCutResult kernel = request.Result.kernel;
                Assert.That(kernel.vertexOriginsWritten, Is.EqualTo(1), "the kernel wrote them");
                if (firstVertexRoom > 0) Assert.That(request.Result.attempts, Is.GreaterThan(1), "the layout: the first reservation was short");
                Assert.That(request.TryTakeVertexOrigins(out NativeArray<uint2> origins, out uint baseVertex, out int count), Is.True, "handed over");
                try
                {
                    Assert.That(request.TryTakeVertexOrigins(out _, out _, out _), Is.False, "once");
                    Assert.That(count, Is.EqualTo(kernel.newVertexCount));
                    Assert.That(count, Is.GreaterThan(0), "the layout: the plane crosses the box");
                    Assert.That(origins.Length, Is.GreaterThanOrEqualTo(count));
                    NativeArray<VpRenderVertex>.ReadOnly vertices = f.storage.Vertices;
                    int interpolated = 0, onAVertex = 0, auxiliary = 0;
                    for (int i = 0; i < count; i++)
                    {
                        float3 p = vertices[(int)baseVertex + i].position;
                        uint2 o = origins[i];
                        if (o.x == MeshCutOutput.NoVertexOrigin)
                        {
                            Assert.That(o.y, Is.EqualTo(MeshCutOutput.NoVertexOrigin), "an auxiliary vertex names neither");
                            Assert.That(math.abs(Side(plane, p)), Is.LessThan(1e-4f), "an auxiliary cap vertex lies on the plane");
                            auxiliary++;
                            continue;
                        }

                        Assert.That(o.x >= (uint)box.vertexStart && o.x < (uint)(box.vertexStart + box.vertexCount), Is.True, "an input vertex: " + o.x);
                        Assert.That(o.y >= (uint)box.vertexStart && o.y < (uint)(box.vertexStart + box.vertexCount), Is.True, "an input vertex: " + o.y);
                        float3 a = vertices[(int)o.x].position, b = vertices[(int)o.y].position;
                        if (o.x == o.y)
                        {
                            Assert.That(math.length(p - a), Is.LessThan(1e-6f), "a vertex at a parameter of 0 or 1 is its one input vertex");
                            onAVertex++;
                            continue;
                        }

                        Assert.That(DistanceToSegment(p, a, b), Is.LessThan(1e-4f), "on the segment of its two input vertices");
                        Assert.That(Side(plane, a) >= 0f != Side(plane, b) >= 0f, Is.True, "which the plane separates");
                        interpolated++;
                    }

                    TestContext.Out.WriteLine("new vertices " + count + ": between two " + interpolated + ", at one " + onAVertex + ", auxiliary " + auxiliary
                        + "; attempts " + request.Result.attempts);
                    Assert.That(interpolated + onAVertex, Is.GreaterThan(0));
                    if (ny == 1f) Assert.That(onAVertex, Is.GreaterThan(0), "the layout: the plane through a row of vertices puts nodes on them");
                }
                finally
                {
                    origins.Dispose();
                }
            }
        }

        /// <summary>Not asked for: nothing is made, nothing is handed over, and the cut is the same.</summary>
        [Test]
        public void NotAskedFor_NothingIsMadeOrHandedOver()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry box = AppendBox(f.storage);
                VpStorageCutRequest request = Cut(f, box, SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), new float3(0.0071f, -0.0233f, 0.0119f)), default);
                Assert.That(request.Result.kernel.vertexOriginsWritten, Is.EqualTo(0));
                Assert.That(request.TryTakeVertexOrigins(out NativeArray<uint2> origins, out _, out _), Is.False);
                Assert.That(origins.IsCreated, Is.False);
            }
        }

        /// <summary>A plane that misses the geometry: no new vertex, and an empty record handed over or none -- never a stale one.</summary>
        [Test]
        public void APlaneThatMisses_LeavesNoOrigins()
        {
            using (Fixture f = NewFixture())
            {
                VpStoredGeometry box = AppendBox(f.storage);
                var options = new VpStorageCutOptions { wantVertexOrigins = true };
                VpStorageCutRequest request = Cut(f, box, SyntheticGeometry.Plane(new float3(0f, 1f, 0f), new float3(0f, 8f, 0f)), options);
                Assert.That(request.Result.kernel.newVertexCount, Is.Zero);
                if (request.TryTakeVertexOrigins(out NativeArray<uint2> origins, out _, out int count))
                {
                    Assert.That(count, Is.Zero, "nothing to record");
                    origins.Dispose();
                }
            }
        }
    }
}
