using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// CPU VP geometry storage (DESIGN 4.5.3): vertices appended and indices rebased and published, submeshes and base
    /// vertices kept, retired index space reused while vertices keep appending, rejected meshes cancelling their
    /// reservation without committing vertices, capacity and descriptor shortage, index views only through read leases,
    /// stale tokens, and disposal.
    /// </summary>
    public class VpCpuGeometryStorageTests
    {
        // Built-in mesh sizes: Quad 4 vertices / 6 indices, Cube 24 / 36.
        private const int QuadVertices = 4;
        private const int QuadIndices = 6;
        private const int CubeVertices = 24;
        private const int CubeIndices = 36;

        private static readonly Vector3[] ThreePositions = { Vector3.zero, Vector3.right, Vector3.up };

        private static readonly Vector3[] SixPositions =
        {
            new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
            new Vector3(2f, 0f, 1f), new Vector3(3f, 0f, 1f), new Vector3(2f, 1f, 1f),
        };

        private readonly List<Mesh> _meshes = new List<Mesh>();

        [TearDown]
        public void DestroyMeshes()
        {
            foreach (Mesh mesh in _meshes)
            {
                Object.DestroyImmediate(mesh);
            }

            _meshes.Clear();
        }

        private static Mesh BuiltIn(string name)
        {
            Mesh mesh = Resources.GetBuiltinResource<Mesh>(name);
            Assert.That(mesh, Is.Not.Null, name);
            return mesh;
        }

        private Mesh NewMesh(Vector3[] positions, bool normals)
        {
            var mesh = new Mesh();
            _meshes.Add(mesh);
            mesh.SetVertices(positions);
            if (normals)
            {
                mesh.SetNormals(Enumerable.Repeat(Vector3.back, positions.Length).ToArray());
            }

            return mesh;
        }

        private Mesh Triangle()
        {
            Mesh mesh = NewMesh(ThreePositions, true);
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            return mesh;
        }

        /// <summary>Two submeshes over six vertices: one triangle at base vertex 0, then two triangles at base vertex 3.</summary>
        private Mesh TwoSubMeshMesh()
        {
            Mesh mesh = NewMesh(SixPositions, true);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, true, 0);
            mesh.SetTriangles(new[] { 0, 1, 2, 2, 1, 0 }, 1, true, 3);
            return mesh;
        }

        /// <summary>Three vertices and six indices, rejected only after the six indices are reserved.</summary>
        private Mesh RejectedAfterReserving(string kind)
        {
            switch (kind)
            {
                case "no normals":
                {
                    Mesh mesh = NewMesh(ThreePositions, false);
                    mesh.SetTriangles(new[] { 0, 1, 2, 2, 1, 0 }, 0);
                    return mesh;
                }

                case "lines":
                {
                    Mesh mesh = NewMesh(ThreePositions, true);
                    mesh.SetIndices(new[] { 0, 1, 1, 2, 2, 0 }, MeshTopology.Lines, 0);
                    return mesh;
                }

                default:
                {
                    // Index 3 does not exist; Unity's own index validation is skipped so the mesh can hold it.
                    const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;
                    Mesh mesh = NewMesh(ThreePositions, true);
                    mesh.subMeshCount = 1;
                    mesh.SetIndexBufferParams(6, IndexFormat.UInt32);
                    mesh.SetIndexBufferData(new uint[] { 0, 1, 2, 2, 1, 3 }, 0, 0, 6, flags);
                    mesh.SetSubMesh(0, new SubMeshDescriptor(0, 6, MeshTopology.Triangles), flags);
                    return mesh;
                }
            }
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Mesh mesh)
        {
            Assert.That(storage.TryAppend(mesh, out VpStoredGeometry geometry), Is.True, "append " + mesh.name);
            return geometry;
        }

        private static uint[] ReadIndices(VpCpuGeometryStorage storage, VpIndexRangeHandle indexRange)
        {
            Assert.That(storage.TryAcquireIndexReadLease(indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True, "acquire index lease");
            uint[] indices = view.ToArray();
            Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True, "release index lease");
            return indices;
        }

        private static uint[] Rebased(IEnumerable<int> localIndices, int vertexStart)
        {
            return localIndices.Select(i => (uint)(vertexStart + i)).ToArray();
        }

        private static void AssertVertexRange(VpStoredGeometry geometry, int vertexStart, int vertexCount)
        {
            Assert.That(new[] { geometry.vertexStart, geometry.vertexCount }, Is.EqualTo(new[] { vertexStart, vertexCount }), "vertexStart, vertexCount");
        }

        private static void AssertIndexRange(VpCpuGeometryStorage storage, VpIndexRangeHandle indexRange, VpIndexRangeState state, int indexStart, int indexCount)
        {
            Assert.That(storage.TryGetIndexState(indexRange, out VpIndexRangeState actualState, out int actualStart, out int actualCount), Is.True, "index state query");
            Assert.That(new object[] { actualState, actualStart, actualCount }, Is.EqualTo(new object[] { state, indexStart, indexCount }), "index state, start, count");
        }

        /// <summary>The committed vertices of the geometry reproduce the mesh's positions, normals and uv0 (zero when absent).</summary>
        private static void AssertVerticesHoldMesh(VpCpuGeometryStorage storage, VpStoredGeometry geometry, Mesh mesh)
        {
            NativeArray<VpRenderVertex>.ReadOnly vertices = storage.Vertices;
            Vector3[] positions = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uvs = mesh.uv;
            Assert.That(geometry.vertexCount, Is.EqualTo(positions.Length), "vertex count");
            for (int v = 0; v < positions.Length; v++)
            {
                VpRenderVertex vertex = vertices[geometry.vertexStart + v];
                Assert.That(vertex.position, Is.EqualTo(positions[v]), "position " + v);
                Assert.That(vertex.normal, Is.EqualTo(normals[v]), "normal " + v);
                Assert.That(vertex.uv0, Is.EqualTo(uvs.Length > 0 ? uvs[v] : Vector2.zero), "uv0 " + v);
            }
        }

        private static void AssertRejected(VpCpuGeometryStorage storage, Mesh mesh, VpRenderVertex[] committed)
        {
            Assert.That(storage.TryAppend(mesh, out VpStoredGeometry geometry), Is.False, "append");
            Assert.That(geometry, Is.EqualTo(default(VpStoredGeometry)));
            Assert.That(storage.VertexCount, Is.EqualTo(committed.Length), "vertex count");
            Assert.That(storage.Vertices.ToArray(), Is.EqualTo(committed), "committed vertices");
        }

        [Test]
        public void TheConstructor_SetsTheCapacitiesAndRejectsNegativeOnes()
        {
            using (var storage = new VpCpuGeometryStorage(100, 200, 4, Allocator.Persistent))
            {
                Assert.That(storage.VertexCapacity, Is.EqualTo(100));
                Assert.That(storage.VertexCount, Is.Zero);
                Assert.That(storage.Vertices.Length, Is.Zero);
                Assert.That(storage.IndexCapacity, Is.EqualTo(200));
                Assert.That(storage.IndexDescriptorCapacity, Is.EqualTo(4));
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => new VpCpuGeometryStorage(-1, 200, 4, Allocator.Persistent), "vertices");
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpCpuGeometryStorage(100, -1, 4, Allocator.Persistent), "indices");
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpCpuGeometryStorage(100, 200, -1, Allocator.Persistent), "descriptors");
        }

        [Test]
        public void AppendingOneMesh_StoresItsVerticesAndPublishesItsIndices()
        {
            Mesh cube = BuiltIn("Cube.fbx");
            using (var storage = new VpCpuGeometryStorage(100, 100, 4, Allocator.Persistent))
            {
                VpStoredGeometry geometry = Append(storage, cube);

                AssertVertexRange(geometry, 0, CubeVertices);
                Assert.That(storage.VertexCount, Is.EqualTo(CubeVertices));
                AssertVerticesHoldMesh(storage, geometry, cube);
                AssertIndexRange(storage, geometry.indexRange, VpIndexRangeState.Published, 0, CubeIndices);
                Assert.That(ReadIndices(storage, geometry.indexRange), Is.EqualTo(Rebased(cube.triangles, 0)));
            }
        }

        [Test]
        public void ALaterMesh_HasItsIndicesRebasedOntoItsVertexStart()
        {
            Mesh quad = BuiltIn("Quad.fbx");
            Mesh cube = BuiltIn("Cube.fbx");
            using (var storage = new VpCpuGeometryStorage(100, 100, 4, Allocator.Persistent))
            {
                VpStoredGeometry first = Append(storage, quad);
                VpStoredGeometry second = Append(storage, cube);

                AssertVertexRange(second, QuadVertices, CubeVertices);
                Assert.That(storage.VertexCount, Is.EqualTo(QuadVertices + CubeVertices));
                AssertIndexRange(storage, second.indexRange, VpIndexRangeState.Published, QuadIndices, CubeIndices);
                Assert.That(ReadIndices(storage, second.indexRange), Is.EqualTo(Rebased(cube.triangles, QuadVertices)));
                Assert.That(ReadIndices(storage, first.indexRange), Is.EqualTo(Rebased(quad.triangles, 0)));
                AssertVerticesHoldMesh(storage, first, quad);
                AssertVerticesHoldMesh(storage, second, cube);
            }
        }

        [Test]
        public void SubmeshesAndBaseVertices_AreKeptInOrderAndRebased()
        {
            Mesh quad = BuiltIn("Quad.fbx");
            Mesh mesh = TwoSubMeshMesh();
            using (var storage = new VpCpuGeometryStorage(100, 100, 4, Allocator.Persistent))
            {
                Append(storage, quad);

                VpStoredGeometry geometry = Append(storage, mesh);

                AssertVertexRange(geometry, QuadVertices, SixPositions.Length);
                Assert.That(ReadIndices(storage, geometry.indexRange), Is.EqualTo(Rebased(new[] { 0, 1, 2, 3, 4, 5, 5, 4, 3 }, QuadVertices)));
                AssertVerticesHoldMesh(storage, geometry, mesh);
            }
        }

        [Test]
        public void RetiredIndexSpace_IsReusedWhileVerticesKeepAppending()
        {
            Mesh quad = BuiltIn("Quad.fbx");
            using (var storage = new VpCpuGeometryStorage(100, 2 * QuadIndices, 2, Allocator.Persistent))
            {
                VpStoredGeometry first = Append(storage, quad);
                VpStoredGeometry second = Append(storage, quad);
                VpRenderVertex[] earlier = storage.Vertices.ToArray();
                Assert.That(storage.TryRetireIndices(first.indexRange), Is.True);
                AssertIndexRange(storage, first.indexRange, VpIndexRangeState.Free, 0, QuadIndices);

                VpStoredGeometry third = Append(storage, quad);

                AssertVertexRange(third, 2 * QuadVertices, QuadVertices);
                AssertIndexRange(storage, third.indexRange, VpIndexRangeState.Published, 0, QuadIndices);
                Assert.That(ReadIndices(storage, third.indexRange), Is.EqualTo(Rebased(quad.triangles, 2 * QuadVertices)));
                Assert.That(storage.Vertices.ToArray().Take(earlier.Length), Is.EqualTo(earlier), "earlier vertices");
                Assert.That(ReadIndices(storage, second.indexRange), Is.EqualTo(Rebased(quad.triangles, QuadVertices)));
            }
        }

        [TestCase("no normals")]
        [TestCase("lines")]
        [TestCase("index out of range")]
        public void MeshesRejectedAfterReserving_CancelTheReservationWithoutCommittingVertices(string kind)
        {
            // Room for exactly two quads' indices and two descriptors: the quad after the rejected mesh only fits if the
            // rejected mesh's index range and descriptor were both returned.
            Mesh quad = BuiltIn("Quad.fbx");
            Mesh rejected = RejectedAfterReserving(kind);
            using (var storage = new VpCpuGeometryStorage(100, 2 * QuadIndices, 2, Allocator.Persistent))
            {
                Append(storage, quad);

                AssertRejected(storage, rejected, storage.Vertices.ToArray());

                VpStoredGeometry next = Append(storage, quad);
                AssertVertexRange(next, QuadVertices, QuadVertices);
                AssertIndexRange(storage, next.indexRange, VpIndexRangeState.Published, QuadIndices, QuadIndices);
                Assert.That(ReadIndices(storage, next.indexRange), Is.EqualTo(Rebased(quad.triangles, QuadVertices)));
            }
        }

        [Test]
        public void TooFewFreeVertices_RejectTheMeshWithoutChangingAnything()
        {
            using (var storage = new VpCpuGeometryStorage(CubeVertices + QuadVertices - 1, 100, 4, Allocator.Persistent))
            {
                Append(storage, BuiltIn("Cube.fbx"));

                AssertRejected(storage, BuiltIn("Quad.fbx"), storage.Vertices.ToArray());

                VpStoredGeometry triangle = Append(storage, Triangle());
                AssertVertexRange(triangle, CubeVertices, 3);
                AssertIndexRange(storage, triangle.indexRange, VpIndexRangeState.Published, CubeIndices, 3);
            }
        }

        [Test]
        public void TooFewFreeIndices_RejectTheMeshWithoutChangingAnything()
        {
            using (var storage = new VpCpuGeometryStorage(100, CubeIndices + QuadIndices - 1, 4, Allocator.Persistent))
            {
                Append(storage, BuiltIn("Cube.fbx"));

                AssertRejected(storage, BuiltIn("Quad.fbx"), storage.Vertices.ToArray());

                VpStoredGeometry triangle = Append(storage, Triangle());
                AssertVertexRange(triangle, CubeVertices, 3);
                AssertIndexRange(storage, triangle.indexRange, VpIndexRangeState.Published, CubeIndices, 3);
            }
        }

        [Test]
        public void TooFewDescriptors_RejectTheMeshWithoutChangingAnything()
        {
            Mesh quad = BuiltIn("Quad.fbx");
            using (var storage = new VpCpuGeometryStorage(100, 100, 1, Allocator.Persistent))
            {
                VpStoredGeometry cube = Append(storage, BuiltIn("Cube.fbx"));

                AssertRejected(storage, quad, storage.Vertices.ToArray());

                Assert.That(storage.TryRetireIndices(cube.indexRange), Is.True);
                VpStoredGeometry next = Append(storage, quad);
                AssertVertexRange(next, CubeVertices, QuadVertices);
                AssertIndexRange(storage, next.indexRange, VpIndexRangeState.Published, 0, QuadIndices);
            }
        }

        [Test]
        public void ANullMesh_IsRejectedWithoutChangingAnything()
        {
            Mesh quad = BuiltIn("Quad.fbx");
            using (var storage = new VpCpuGeometryStorage(100, 2 * QuadIndices, 2, Allocator.Persistent))
            {
                Append(storage, quad);

                AssertRejected(storage, null, storage.Vertices.ToArray());

                VpStoredGeometry next = Append(storage, quad);
                AssertVertexRange(next, QuadVertices, QuadVertices);
                AssertIndexRange(storage, next.indexRange, VpIndexRangeState.Published, QuadIndices, QuadIndices);
            }
        }

        [Test]
        public void IndexViews_AreOnlyReachableThroughReadLeases()
        {
            var yieldsIndexView = new HashSet<Type> { typeof(NativeArray<uint>), typeof(NativeArray<uint>.ReadOnly) };
            var methods = typeof(VpCpuGeometryStorage).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            var viewMethods = methods
                .Where(m => yieldsIndexView.Contains(m.ReturnType)
                    || m.GetParameters().Any(p => yieldsIndexView.Contains(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType)))
                .ToArray();

            Assert.That(viewMethods.Select(m => m.Name), Is.EquivalentTo(new[] { "TryAcquireIndexReadLease", "TryGetIndexReadView" }));
            foreach (MethodInfo method in viewMethods)
            {
                Assert.That(
                    method.GetParameters().Any(p => (p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType) == typeof(VpIndexReadLease)),
                    Is.True,
                    method.Name + " involves a read lease");
            }
        }

        [Test]
        public void AHeldIndexLease_KeepsReadingWhileTheRangeRetires()
        {
            Mesh quad = BuiltIn("Quad.fbx");
            using (var storage = new VpCpuGeometryStorage(100, 100, 2, Allocator.Persistent))
            {
                VpStoredGeometry geometry = Append(storage, quad);
                uint[] expected = Rebased(quad.triangles, 0);
                Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True);
                Assert.That(view.ToArray(), Is.EqualTo(expected));

                Assert.That(storage.TryRetireIndices(geometry.indexRange), Is.True);

                AssertIndexRange(storage, geometry.indexRange, VpIndexRangeState.Retiring, 0, QuadIndices);
                Assert.That(storage.TryGetIndexReadView(lease, out NativeArray<uint>.ReadOnly retiringView), Is.True);
                Assert.That(retiringView.ToArray(), Is.EqualTo(expected));
                Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out _, out _), Is.False, "new lease while retiring");

                Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);
                Assert.That(storage.TryGetIndexReadView(lease, out _), Is.False, "returned lease");
                AssertIndexRange(storage, geometry.indexRange, VpIndexRangeState.Free, 0, QuadIndices);
            }
        }

        [Test]
        public void StaleIndexHandlesAndLeases_AreRejected()
        {
            // One descriptor and one quad's indices, so the second quad reuses the descriptor, lease slot and index space.
            Mesh quad = BuiltIn("Quad.fbx");
            using (var storage = new VpCpuGeometryStorage(100, QuadIndices, 1, Allocator.Persistent))
            {
                VpStoredGeometry old = Append(storage, quad);
                Assert.That(storage.TryAcquireIndexReadLease(old.indexRange, out VpIndexReadLease oldLease, out _), Is.True);
                Assert.That(storage.TryRetireIndices(old.indexRange), Is.True);
                Assert.That(storage.TryReleaseIndexReadLease(oldLease), Is.True);

                VpStoredGeometry current = Append(storage, quad);

                AssertIndexRange(storage, current.indexRange, VpIndexRangeState.Published, 0, QuadIndices);
                Assert.That(storage.TryGetIndexState(old.indexRange, out _, out _, out _), Is.False, "old state");
                Assert.That(storage.TryAcquireIndexReadLease(old.indexRange, out _, out _), Is.False, "old acquire");
                Assert.That(storage.TryRetireIndices(old.indexRange), Is.False, "old retire");
                Assert.That(storage.TryGetIndexReadView(oldLease, out _), Is.False, "old lease view");
                Assert.That(storage.TryReleaseIndexReadLease(oldLease), Is.False, "old lease release");
                AssertIndexRange(storage, current.indexRange, VpIndexRangeState.Published, 0, QuadIndices);
                Assert.That(ReadIndices(storage, current.indexRange), Is.EqualTo(Rebased(quad.triangles, QuadVertices)));
            }
        }

        [Test]
        public void AfterDispose_OperationsThrowAndDisposingAgainDoesNothing()
        {
            Mesh quad = BuiltIn("Quad.fbx");
            var storage = new VpCpuGeometryStorage(100, 100, 2, Allocator.Persistent);
            VpStoredGeometry geometry = Append(storage, quad);
            Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out _), Is.True);

            storage.Dispose();

            Assert.Throws<ObjectDisposedException>(() => storage.TryAppend(quad, out _), "append");
            Assert.Throws<ObjectDisposedException>(() => _ = storage.Vertices, "vertices");
            Assert.Throws<ObjectDisposedException>(() => storage.TryAcquireIndexReadLease(geometry.indexRange, out _, out _), "acquire");
            Assert.Throws<ObjectDisposedException>(() => storage.TryGetIndexReadView(lease, out _), "read view");
            Assert.Throws<ObjectDisposedException>(() => storage.TryRetireIndices(geometry.indexRange), "retire");
            Assert.Throws<ObjectDisposedException>(() => storage.TryReleaseIndexReadLease(lease), "release");
            Assert.Throws<ObjectDisposedException>(() => storage.TryGetIndexState(geometry.indexRange, out _, out _, out _), "state");
            Assert.DoesNotThrow(storage.Dispose, "dispose again");
        }
    }
}
