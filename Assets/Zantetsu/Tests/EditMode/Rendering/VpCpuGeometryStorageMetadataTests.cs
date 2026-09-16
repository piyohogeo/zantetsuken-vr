using System;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The metadata a prepared geometry brings into the CPU VP geometry storage as one owned append (DESIGN 4.5.3):
    /// the 32 byte vertices, the index range rebased onto the global vertex numbers, the render vertex to topology
    /// vertex mapping with its topology vertex count, and the submesh descriptors with their relative offsets and
    /// source material indices. Also what refuses an append without committing anything, what keeps the metadata
    /// readable while the index range retires, and what stops it being readable once the range is free or reused.
    /// </summary>
    public class VpCpuGeometryStorageMetadataTests
    {
        private const int Vertices = 6;
        private const int Indices = 9;
        private const int TopologyVertices = 4;
        private const int Submeshes = 2;

        /// <summary>
        /// A Phase 0.21 shaped geometry: four topology vertices (the FBX control points), six render vertices of which
        /// two are attribute seams sharing a control point with another, and nine indices in two submeshes whose
        /// material indices are not their ordinals.
        /// </summary>
        private static VpRenderVertex[] PreparedVertices()
        {
            return new[]
            {
                Vertex(0f, 0f, 0f, 0f, 0f),
                Vertex(1f, 0f, 0f, 1f, 0f),
                Vertex(1f, 1f, 0f, 1f, 1f),
                Vertex(0f, 1f, 0f, 0f, 1f),
                Vertex(1f, 0f, 0f, 0.25f, 0.5f),   // the seam of control point 1
                Vertex(0f, 1f, 0f, 0.75f, 0.5f),   // the seam of control point 3
            };
        }

        private static VpRenderVertex Vertex(float x, float y, float z, float u, float v)
        {
            return new VpRenderVertex { position = new Vector3(x, y, z), normal = Vector3.back, uv0 = new Vector2(u, v) };
        }

        private static uint[] PreparedIndices()
        {
            return new uint[] { 0, 1, 2, 0, 2, 3, 4, 5, 0 };
        }

        private static int[] PreparedTopology()
        {
            return new[] { 0, 1, 2, 3, 1, 3 };
        }

        private static VpGeometrySubmesh[] PreparedSubmeshes()
        {
            // relative to the geometry's own index range, with source material indices that are not the ordinals
            return new[] { new VpGeometrySubmesh(0, 3, 7), new VpGeometrySubmesh(3, 6, 2) };
        }

        private static VpCpuGeometryStorage NewStorage(int vertexCapacity = 64, int indexCapacity = 64, int descriptorCapacity = 4, int submeshCapacity = 8)
        {
            return new VpCpuGeometryStorage(vertexCapacity, indexCapacity, descriptorCapacity, submeshCapacity, 16, Allocator.Persistent);
        }

        private static VpStoredGeometry AppendPrepared(VpCpuGeometryStorage storage)
        {
            Assert.That(
                storage.TryAppendPrepared(PreparedVertices(), PreparedIndices(), PreparedTopology(), TopologyVertices, PreparedSubmeshes(), out VpStoredGeometry geometry),
                Is.True,
                "append prepared");
            return geometry;
        }

        private static uint[] ReadIndices(VpCpuGeometryStorage storage, VpIndexRangeHandle indexRange)
        {
            Assert.That(storage.TryAcquireIndexReadLease(indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True, "acquire lease");
            uint[] indices = view.ToArray();
            Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True, "release lease");
            return indices;
        }

        private static void AssertNoMetadata(VpCpuGeometryStorage storage, VpStoredGeometry geometry, string label)
        {
            Assert.That(storage.TryGetTopology(geometry, out NativeArray<int>.ReadOnly topology, out int topologyVertexCount), Is.False, label + " topology");
            Assert.That(topology.Length, Is.Zero, label + " topology view");
            Assert.That(topologyVertexCount, Is.Zero, label + " topology count");
            Assert.That(storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.False, label + " submeshes");
            Assert.That(submeshes.Length, Is.Zero, label + " submesh view");
        }

        private static void AssertUnchanged(VpCpuGeometryStorage storage, VpRenderVertex[] vertices, int submeshCount, string label)
        {
            Assert.That(storage.VertexCount, Is.EqualTo(vertices.Length), label + " vertex count");
            Assert.That(storage.Vertices.ToArray(), Is.EqualTo(vertices), label + " committed vertices");
            Assert.That(storage.SubmeshCount, Is.EqualTo(submeshCount), label + " submesh count");
        }

        [Test]
        public void APreparedGeometry_IsStoredWithItsVerticesIndicesTopologyAndSubmeshes()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpRenderVertex[] source = PreparedVertices();

                VpStoredGeometry geometry = AppendPrepared(storage);

                Assert.That(geometry.vertexStart, Is.Zero);
                Assert.That(geometry.vertexCount, Is.EqualTo(Vertices));
                Assert.That(geometry.hasTopology, Is.True);
                Assert.That(geometry.topologyVertexCount, Is.EqualTo(TopologyVertices));
                Assert.That(geometry.submeshStart, Is.Zero);
                Assert.That(geometry.submeshCount, Is.EqualTo(Submeshes));
                Assert.That(storage.VertexCount, Is.EqualTo(Vertices));
                Assert.That(storage.SubmeshCount, Is.EqualTo(Submeshes));

                Assert.That(storage.Vertices.ToArray(), Is.EqualTo(source), "the vertices are stored as given");
                Assert.That(ReadIndices(storage, geometry.indexRange), Is.EqualTo(PreparedIndices()), "the first geometry starts at vertex 0, so rebasing changes nothing");

                Assert.That(storage.TryGetTopology(geometry, out NativeArray<int>.ReadOnly topology, out int topologyVertexCount), Is.True);
                Assert.That(topology.ToArray(), Is.EqualTo(PreparedTopology()), "topology mapping");
                Assert.That(topologyVertexCount, Is.EqualTo(TopologyVertices));
                Assert.That(topology.Length, Is.EqualTo(geometry.vertexCount), "one entry per render vertex");

                Assert.That(storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);
                Assert.That(submeshes.ToArray(), Is.EqualTo(PreparedSubmeshes()), "submesh ranges and material indices");
                Assert.That(submeshes[0].indexOffset + submeshes[0].indexCount, Is.EqualTo(submeshes[1].indexOffset), "offsets are relative and contiguous");
                Assert.That(submeshes.ToArray().Sum(s => s.indexCount), Is.EqualTo(Indices), "the submeshes cover every index");
            }
        }

        [Test]
        public void AttributeSeams_KeepSharingOneTopologyVertex()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry geometry = AppendPrepared(storage);

                Assert.That(storage.TryGetTopology(geometry, out NativeArray<int>.ReadOnly topology, out _), Is.True);
                int[] map = topology.ToArray();
                NativeArray<VpRenderVertex>.ReadOnly vertices = storage.Vertices;

                Assert.That(map[4], Is.EqualTo(map[1]), "the seam shares control point 1");
                Assert.That(map[5], Is.EqualTo(map[3]), "the seam shares control point 3");
                Assert.That(vertices[4].position, Is.EqualTo(vertices[1].position), "a seam is one position");
                Assert.That(vertices[4].uv0, Is.Not.EqualTo(vertices[1].uv0), "and two attribute values");
                Assert.That(map.Distinct().Count(), Is.EqualTo(TopologyVertices), "four topology vertices for six render vertices");
            }
        }

        [Test]
        public void ASecondGeometry_RebasesItsIndicesAndKeepsItsTopologyIdsLocal()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry first = AppendPrepared(storage);

                VpStoredGeometry second = AppendPrepared(storage);

                Assert.That(second.vertexStart, Is.EqualTo(Vertices), "the second geometry starts after the first");
                Assert.That(second.submeshStart, Is.EqualTo(Submeshes), "its submeshes are appended after the first's");
                Assert.That(ReadIndices(storage, second.indexRange), Is.EqualTo(PreparedIndices().Select(i => i + Vertices)), "indices rebased onto the global vertex numbers");

                Assert.That(storage.TryGetTopology(second, out NativeArray<int>.ReadOnly topology, out int topologyVertexCount), Is.True);
                Assert.That(topology.ToArray(), Is.EqualTo(PreparedTopology()), "topology ids stay geometry-local");
                Assert.That(topologyVertexCount, Is.EqualTo(TopologyVertices), "and so does the topology vertex count");
                Assert.That(storage.TryGetTopology(first, out NativeArray<int>.ReadOnly firstTopology, out _), Is.True);
                Assert.That(firstTopology.ToArray(), Is.EqualTo(PreparedTopology()), "the first geometry's mapping is untouched");
                Assert.That(ReadIndices(storage, first.indexRange), Is.EqualTo(PreparedIndices()), "and so are its indices");
            }
        }

        [Test]
        public void ReusedIndexSpace_LeavesTheAppendOnlyMetadataOfTheOthersUntouched()
        {
            // Room for exactly two geometries' indices, so the third can only fit in the retired one's space.
            using (VpCpuGeometryStorage storage = NewStorage(indexCapacity: 2 * Indices, descriptorCapacity: 2))
            {
                VpStoredGeometry first = AppendPrepared(storage);
                VpStoredGeometry second = AppendPrepared(storage);
                VpRenderVertex[] committed = storage.Vertices.ToArray();
                int[] secondTopologyBefore = Topology(storage, second);
                VpGeometrySubmesh[] secondSubmeshesBefore = SubmeshesOf(storage, second);
                Assert.That(storage.TryRetireIndices(first.indexRange), Is.True, "retire the first index range");

                VpStoredGeometry third = AppendPrepared(storage);

                Assert.That(third.vertexStart, Is.EqualTo(2 * Vertices), "vertices keep appending");
                Assert.That(third.submeshStart, Is.EqualTo(2 * Submeshes), "submesh descriptors keep appending");
                Assert.That(storage.Vertices.ToArray().Take(committed.Length), Is.EqualTo(committed), "earlier vertices unchanged");
                Assert.That(Topology(storage, second), Is.EqualTo(secondTopologyBefore), "the second geometry's mapping unchanged");
                Assert.That(SubmeshesOf(storage, second), Is.EqualTo(secondSubmeshesBefore), "its submeshes unchanged");
                Assert.That(Topology(storage, third), Is.EqualTo(PreparedTopology()), "the third geometry has its own mapping");
                AssertNoMetadata(storage, first, "the retired and reused geometry");
            }
        }

        [Test]
        public void TheUnityMeshPath_KeepsItsSubmeshesAndHasNoTopology()
        {
            Mesh mesh = TwoSubMeshMesh();
            try
            {
                using (VpCpuGeometryStorage storage = NewStorage())
                {
                    Assert.That(storage.TryAppend(mesh, out VpStoredGeometry geometry), Is.True, "append mesh");

                    Assert.That(geometry.hasTopology, Is.False, "a Unity Mesh brings no source topology");
                    Assert.That(geometry.topologyVertexCount, Is.Zero);
                    Assert.That(storage.TryGetTopology(geometry, out _, out _), Is.False, "no mapping to read");

                    Assert.That(geometry.submeshCount, Is.EqualTo(2));
                    Assert.That(storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);
                    Assert.That(
                        submeshes.ToArray(),
                        Is.EqualTo(new[] { new VpGeometrySubmesh(0, 3, 0), new VpGeometrySubmesh(3, 6, 1) }),
                        "submesh order kept, offsets relative, material index is the ordinal");
                }
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void AnEmptyGeometryWithAMapping_IsToldApartFromOneWithout()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(
                    storage.TryAppendPrepared(Array.Empty<VpRenderVertex>(), Array.Empty<uint>(), Array.Empty<int>(), 0, Array.Empty<VpGeometrySubmesh>(), out VpStoredGeometry empty),
                    Is.True,
                    "append an empty prepared geometry");

                Assert.That(empty.hasTopology, Is.True, "it carried a mapping, empty as it is");
                Assert.That(empty.topologyVertexCount, Is.Zero);
                Assert.That(storage.TryGetTopology(empty, out NativeArray<int>.ReadOnly topology, out int topologyVertexCount), Is.True, "its mapping is readable");
                Assert.That(topology.Length, Is.Zero);
                Assert.That(topologyVertexCount, Is.Zero);
                Assert.That(storage.TryGetSubmeshes(empty, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);
                Assert.That(submeshes.Length, Is.Zero);
            }
        }

        [TestCase("vertices")]
        [TestCase("indices")]
        [TestCase("descriptors")]
        [TestCase("submeshes")]
        public void ACapacityShortage_CommitsNothing(string kind)
        {
            int vertexCapacity = kind == "vertices" ? Vertices - 1 : 64;
            int indexCapacity = kind == "indices" ? Indices - 1 : 64;
            int descriptorCapacity = kind == "descriptors" ? 0 : 4;
            int submeshCapacity = kind == "submeshes" ? Submeshes - 1 : 8;
            using (VpCpuGeometryStorage storage = NewStorage(vertexCapacity, indexCapacity, descriptorCapacity, submeshCapacity))
            {
                VpRenderVertex[] vertices = PreparedVertices();
                uint[] indices = PreparedIndices();
                int[] topology = PreparedTopology();
                VpGeometrySubmesh[] submeshes = PreparedSubmeshes();

                Assert.That(storage.TryAppendPrepared(vertices, indices, topology, TopologyVertices, submeshes, out VpStoredGeometry geometry), Is.False, kind);

                Assert.That(geometry, Is.EqualTo(default(VpStoredGeometry)), "default result");
                AssertUnchanged(storage, Array.Empty<VpRenderVertex>(), 0, kind);
                Assert.That(vertices, Is.EqualTo(PreparedVertices()), "input vertices unchanged");
                Assert.That(indices, Is.EqualTo(PreparedIndices()), "input indices unchanged");
                Assert.That(topology, Is.EqualTo(PreparedTopology()), "input topology unchanged");
                Assert.That(submeshes, Is.EqualTo(PreparedSubmeshes()), "input submeshes unchanged");
            }
        }

        [TestCase("null vertices")]
        [TestCase("null indices")]
        [TestCase("null topology")]
        [TestCase("null submeshes")]
        [TestCase("topology length")]
        [TestCase("topology id too large")]
        [TestCase("negative topology id")]
        [TestCase("negative topology count")]
        [TestCase("index out of range")]
        [TestCase("submesh gap")]
        [TestCase("submesh overlap")]
        [TestCase("submeshes too short")]
        [TestCase("negative material index")]
        [TestCase("negative submesh count")]
        public void AnInvalidPreparedGeometry_IsRejectedWithoutTouchingTheStorageOrTheInput(string kind)
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry existing = AppendPrepared(storage);
                VpRenderVertex[] committed = storage.Vertices.ToArray();

                VpRenderVertex[] vertices = PreparedVertices();
                uint[] indices = PreparedIndices();
                int[] topology = PreparedTopology();
                int topologyVertexCount = TopologyVertices;
                VpGeometrySubmesh[] submeshes = PreparedSubmeshes();
                switch (kind)
                {
                    case "null vertices": vertices = null; break;
                    case "null indices": indices = null; break;
                    case "null topology": topology = null; break;
                    case "null submeshes": submeshes = null; break;
                    case "topology length": topology = topology.Take(Vertices - 1).ToArray(); break;
                    case "topology id too large": topology[2] = TopologyVertices; break;
                    case "negative topology id": topology[0] = -1; break;
                    case "negative topology count": topologyVertexCount = -1; break;
                    case "index out of range": indices[4] = Vertices; break;
                    case "submesh gap": submeshes = new[] { new VpGeometrySubmesh(0, 3, 7), new VpGeometrySubmesh(4, 5, 2) }; break;
                    case "submesh overlap": submeshes = new[] { new VpGeometrySubmesh(0, 3, 7), new VpGeometrySubmesh(2, 7, 2) }; break;
                    case "submeshes too short": submeshes = new[] { new VpGeometrySubmesh(0, 3, 7) }; break;
                    case "negative material index": submeshes = new[] { new VpGeometrySubmesh(0, 3, -1), new VpGeometrySubmesh(3, 6, 2) }; break;
                    case "negative submesh count": submeshes = new[] { new VpGeometrySubmesh(0, -1, 7), new VpGeometrySubmesh(3, 6, 2) }; break;
                }

                VpRenderVertex[] verticesBefore = vertices?.ToArray();
                uint[] indicesBefore = indices?.ToArray();
                int[] topologyBefore = topology?.ToArray();
                VpGeometrySubmesh[] submeshesBefore = submeshes?.ToArray();

                Assert.That(storage.TryAppendPrepared(vertices, indices, topology, topologyVertexCount, submeshes, out VpStoredGeometry rejected), Is.False, kind);

                Assert.That(rejected, Is.EqualTo(default(VpStoredGeometry)), kind + " result");
                AssertUnchanged(storage, committed, Submeshes, kind);
                Assert.That(vertices, Is.EqualTo(verticesBefore), kind + " input vertices");
                Assert.That(indices, Is.EqualTo(indicesBefore), kind + " input indices");
                Assert.That(topology, Is.EqualTo(topologyBefore), kind + " input topology");
                Assert.That(submeshes, Is.EqualTo(submeshesBefore), kind + " input submeshes");
                Assert.That(Topology(storage, existing), Is.EqualTo(PreparedTopology()), kind + ": the stored geometry keeps its mapping");
                Assert.That(SubmeshesOf(storage, existing), Is.EqualTo(PreparedSubmeshes()), kind + ": and its submeshes");

                // the storage still works afterwards
                VpStoredGeometry next = AppendPrepared(storage);
                Assert.That(next.vertexStart, Is.EqualTo(Vertices), kind + ": the next append follows the first");
            }
        }

        [Test]
        public void DefaultForeignAndStaleGeometries_HaveNoMetadata()
        {
            using (VpCpuGeometryStorage storage = NewStorage(indexCapacity: Indices, descriptorCapacity: 1))
            using (VpCpuGeometryStorage other = NewStorage())
            {
                VpStoredGeometry stale = AppendPrepared(storage);
                VpStoredGeometry foreign = AppendPrepared(other);
                Assert.That(storage.TryRetireIndices(stale.indexRange), Is.True, "retire");
                VpStoredGeometry current = AppendPrepared(storage);

                AssertNoMetadata(storage, default, "default");
                AssertNoMetadata(storage, foreign, "foreign");
                AssertNoMetadata(storage, stale, "stale");
                Assert.That(storage.TryGetTopology(current, out _, out _), Is.True, "the current geometry still reads");
                Assert.That(storage.TryGetSubmeshes(current, out _), Is.True, "the current geometry's submeshes still read");
            }
        }

        [Test]
        public void Metadata_StaysReadableWhileRetiringAndIsRefusedOnceFree()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry geometry = AppendPrepared(storage);
                Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out _), Is.True, "lease");

                Assert.That(storage.TryRetireIndices(geometry.indexRange), Is.True, "retire while the lease is held");

                Assert.That(storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Retiring));
                Assert.That(Topology(storage, geometry), Is.EqualTo(PreparedTopology()), "the mapping reads while retiring");
                Assert.That(SubmeshesOf(storage, geometry), Is.EqualTo(PreparedSubmeshes()), "the submeshes read while retiring");

                Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True, "return the lease");

                Assert.That(storage.TryGetIndexState(geometry.indexRange, out state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Free));
                AssertNoMetadata(storage, geometry, "after the range is free");
            }
        }

        [Test]
        public void OneAppendsHandleWithAnotherAppendsMetadata_IsRefusedForReadingAndForRegistration()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry a = AppendPrepared(storage);
                VpStoredGeometry b = AppendPrepared(storage);
                var table = new VpGeometryReferenceTable(storage, 4, 4);

                var mixed = new VpStoredGeometry(b.vertexStart, b.vertexCount, a.indexRange, b.hasTopology, b.topologyVertexCount, b.submeshStart, b.submeshCount, b.blockStart, b.blockCount);

                AssertNoMetadata(storage, mixed, "A's index handle with B's ranges");
                Assert.That(table.TryRegisterGeometry(mixed, out VpGeometryReference reference), Is.False, "registration");
                Assert.That(reference, Is.EqualTo(default(VpGeometryReference)), "registration token");
                Assert.That(Topology(storage, a), Is.EqualTo(PreparedTopology()), "A itself is unaffected");
                Assert.That(Topology(storage, b), Is.EqualTo(PreparedTopology()), "and so is B");
            }
        }

        [TestCase("vertex start")]
        [TestCase("vertex count")]
        [TestCase("submesh start")]
        [TestCase("submesh count")]
        [TestCase("topology dropped")]
        [TestCase("topology count")]
        [TestCase("block start")]
        [TestCase("block count")]
        public void ADescriptionAlteredWithinTheStorage_IsRefused(string kind)
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry a = AppendPrepared(storage);
                VpStoredGeometry b = AppendPrepared(storage);
                var table = new VpGeometryReferenceTable(storage, 4, 4);

                // every alteration stays inside what the storage holds, so only the append record can tell them apart
                VpStoredGeometry altered;
                switch (kind)
                {
                    case "vertex start":
                        altered = new VpStoredGeometry(b.vertexStart, a.vertexCount, a.indexRange, a.hasTopology, a.topologyVertexCount, a.submeshStart, a.submeshCount, a.blockStart, a.blockCount);
                        break;
                    case "vertex count":
                        altered = new VpStoredGeometry(a.vertexStart, a.vertexCount - 1, a.indexRange, a.hasTopology, a.topologyVertexCount, a.submeshStart, a.submeshCount, a.blockStart, a.blockCount);
                        break;
                    case "submesh start":
                        altered = new VpStoredGeometry(a.vertexStart, a.vertexCount, a.indexRange, a.hasTopology, a.topologyVertexCount, b.submeshStart, a.submeshCount, a.blockStart, a.blockCount);
                        break;
                    case "submesh count":
                        altered = new VpStoredGeometry(a.vertexStart, a.vertexCount, a.indexRange, a.hasTopology, a.topologyVertexCount, a.submeshStart, a.submeshCount - 1, a.blockStart, a.blockCount);
                        break;
                    case "topology dropped":
                        altered = new VpStoredGeometry(a.vertexStart, a.vertexCount, a.indexRange, false, 0, a.submeshStart, a.submeshCount, a.blockStart, a.blockCount);
                        break;
                    case "topology count":
                        altered = new VpStoredGeometry(a.vertexStart, a.vertexCount, a.indexRange, a.hasTopology, a.topologyVertexCount - 1, a.submeshStart, a.submeshCount, a.blockStart, a.blockCount);
                        break;
                    case "block start":
                        altered = new VpStoredGeometry(a.vertexStart, a.vertexCount, a.indexRange, a.hasTopology, a.topologyVertexCount, a.submeshStart, a.submeshCount, b.blockStart, a.blockCount);
                        break;
                    default:
                        altered = new VpStoredGeometry(a.vertexStart, a.vertexCount, a.indexRange, a.hasTopology, a.topologyVertexCount, a.submeshStart, a.submeshCount, a.blockStart, a.blockCount + 1);
                        break;
                }

                AssertNoMetadata(storage, altered, kind);
                Assert.That(table.TryRegisterGeometry(altered, out VpGeometryReference reference), Is.False, kind + " registration");
                Assert.That(reference, Is.EqualTo(default(VpGeometryReference)), kind + " token");
                Assert.That(table.TryRegisterGeometry(a, out _), Is.True, kind + ": the geometry as returned still registers");
            }
        }

        [Test]
        public void AReusedDescriptor_DoesNotAcceptTheRecordOfItsEarlierRegistration()
        {
            // One descriptor and one geometry's indices, so the next append registers the same descriptor again.
            using (VpCpuGeometryStorage storage = NewStorage(indexCapacity: Indices, descriptorCapacity: 1))
            {
                VpStoredGeometry old = AppendPrepared(storage);
                Assert.That(storage.TryRetireIndices(old.indexRange), Is.True, "retire");

                VpStoredGeometry current = AppendPrepared(storage);

                AssertNoMetadata(storage, old, "the earlier registration");
                var oldRangesNewHandle = new VpStoredGeometry(old.vertexStart, old.vertexCount, current.indexRange, old.hasTopology, old.topologyVertexCount, old.submeshStart, old.submeshCount, old.blockStart, old.blockCount);
                AssertNoMetadata(storage, oldRangesNewHandle, "the new handle with the earlier ranges");
                Assert.That(Topology(storage, current), Is.EqualTo(PreparedTopology()), "the current append reads");
                Assert.That(SubmeshesOf(storage, current), Is.EqualTo(PreparedSubmeshes()), "and its submeshes read");
            }
        }

        [TestCase("total not a multiple of three")]
        [TestCase("a submesh boundary inside a triangle")]
        public void IndexCountsThatBreakTriangles_AreRejectedWithoutTouchingTheStorageOrTheInput(string kind)
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry existing = AppendPrepared(storage);
                VpRenderVertex[] committed = storage.Vertices.ToArray();

                VpRenderVertex[] vertices = PreparedVertices();
                int[] topology = PreparedTopology();
                uint[] indices;
                VpGeometrySubmesh[] submeshes;
                if (kind == "total not a multiple of three")
                {
                    indices = new uint[] { 0, 1, 2, 0, 2, 3, 4, 5 };
                    submeshes = new[] { new VpGeometrySubmesh(0, 3, 7), new VpGeometrySubmesh(3, 5, 2) };
                }
                else
                {
                    // nine indices covered exactly, but the first submesh ends inside the first triangle
                    indices = PreparedIndices();
                    submeshes = new[] { new VpGeometrySubmesh(0, 1, 7), new VpGeometrySubmesh(1, 8, 2) };
                }

                uint[] indicesBefore = indices.ToArray();
                VpGeometrySubmesh[] submeshesBefore = submeshes.ToArray();

                Assert.That(storage.TryAppendPrepared(vertices, indices, topology, TopologyVertices, submeshes, out VpStoredGeometry rejected), Is.False, kind);

                Assert.That(rejected, Is.EqualTo(default(VpStoredGeometry)), kind + " result");
                AssertUnchanged(storage, committed, Submeshes, kind);
                Assert.That(indices, Is.EqualTo(indicesBefore), kind + " input indices");
                Assert.That(submeshes, Is.EqualTo(submeshesBefore), kind + " input submeshes");
                Assert.That(vertices, Is.EqualTo(PreparedVertices()), kind + " input vertices");
                Assert.That(topology, Is.EqualTo(PreparedTopology()), kind + " input topology");
                Assert.That(SubmeshesOf(storage, existing), Is.EqualTo(PreparedSubmeshes()), kind + ": the stored geometry keeps its submeshes");
            }
        }

        [Test]
        public void AfterDispose_TheMetadataOperationsThrow()
        {
            VpCpuGeometryStorage storage = NewStorage();
            VpStoredGeometry geometry = AppendPrepared(storage);

            storage.Dispose();

            Assert.Throws<ObjectDisposedException>(() => storage.TryGetTopology(geometry, out _, out _), "topology");
            Assert.Throws<ObjectDisposedException>(() => storage.TryGetSubmeshes(geometry, out _), "submeshes");
            Assert.Throws<ObjectDisposedException>(
                () => storage.TryAppendPrepared(PreparedVertices(), PreparedIndices(), PreparedTopology(), TopologyVertices, PreparedSubmeshes(), out _),
                "append prepared");
            Assert.DoesNotThrow(storage.Dispose, "dispose again");
        }

        private static int[] Topology(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryGetTopology(geometry, out NativeArray<int>.ReadOnly topology, out _), Is.True, "topology");
            return topology.ToArray();
        }

        private static VpGeometrySubmesh[] SubmeshesOf(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True, "submeshes");
            return submeshes.ToArray();
        }

        /// <summary>Six vertices in two submeshes: one triangle, then two triangles.</summary>
        private static Mesh TwoSubMeshMesh()
        {
            var mesh = new Mesh();
            mesh.SetVertices(new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
                new Vector3(2f, 0f, 1f), new Vector3(3f, 0f, 1f), new Vector3(2f, 1f, 1f),
            });
            mesh.SetNormals(Enumerable.Repeat(Vector3.back, 6).ToArray());
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, true, 0);
            mesh.SetTriangles(new[] { 0, 1, 2, 2, 1, 0 }, 1, true, 3);
            return mesh;
        }
    }
}
