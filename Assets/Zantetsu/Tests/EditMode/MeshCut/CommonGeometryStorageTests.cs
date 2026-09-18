using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut.ReferenceIntake;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The adapter that hands a prepared common geometry to the CPU VP geometry storage (DESIGN 4.5.3 / 10.2.3): the
    /// 32 byte vertices, the index range, the render vertex to topology vertex mapping and the submesh descriptors
    /// arrive as one owned append, the source material index travels while the material name does not, and anything
    /// that is not a valid geometry already in Unity's basis is refused without touching the storage.
    /// </summary>
    public class CommonGeometryStorageTests
    {
        private const int TopologyVertices = 4;

        /// <summary>
        /// A closed tetrahedron in Unity's basis: four control points, six render vertices with two attribute seams
        /// (control points 1 and 3 each carry a second render vertex with another uv), two submeshes. Closed and
        /// consistently wound on the control points, since the adapter now appends through the cut input gate; the
        /// seams are told apart by the control point each render vertex names, never by position.
        /// </summary>
        private static PreparedCommonGeometry Prepared(CommonGeometryBasis basis = CommonGeometryBasis.Unity)
        {
            return new PreparedCommonGeometry
            {
                Name = "tetrahedron",
                Basis = basis,
                Vertices = new[]
                {
                    Vertex(0f, 0f, 0f, 0f, 0f), Vertex(1f, 0f, 0f, 1f, 0f), Vertex(0f, 1f, 0f, 1f, 1f),
                    Vertex(0f, 0f, 1f, 0f, 1f), Vertex(1f, 0f, 0f, 0.25f, 0.5f), Vertex(0f, 0f, 1f, 0.75f, 0.5f),
                },
                // faces on the control points: (0,2,1) | (0,1,3), (0,3,2), (1,2,3) -- every edge shared once each way
                Indices = new uint[] { 0, 2, 1, 0, 4, 5, 0, 3, 2, 4, 2, 3 },
                TopologyOfVertex = new[] { 0, 1, 2, 3, 1, 3 },
                TopologyVertexCount = TopologyVertices,
                Submeshes = new[]
                {
                    new CommonGeometrySubmesh { IndexStart = 0, IndexCount = 3, MaterialIndex = 7, MaterialName = "Side" },
                    new CommonGeometrySubmesh { IndexStart = 3, IndexCount = 9, MaterialIndex = 2, MaterialName = "EndCap" },
                },
            };
        }

        private static VpRenderVertex Vertex(float x, float y, float z, float u, float v)
        {
            return new VpRenderVertex { position = new Vector3(x, y, z), normal = Vector3.back, uv0 = new Vector2(u, v) };
        }

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(64, 64, 4, 8, 16, Allocator.Persistent);
        }

        [Test]
        public void APreparedUnityBasisGeometry_ArrivesInTheStorageAsOneOwnedAppend()
        {
            PreparedCommonGeometry prepared = Prepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(CommonGeometryStorage.TryAppend(storage, prepared, out VpStoredGeometry stored), Is.True, "append");

                Assert.That(stored.vertexCount, Is.EqualTo(prepared.Vertices.Length));
                Assert.That(stored.hasTopology, Is.True);
                Assert.That(stored.topologyVertexCount, Is.EqualTo(TopologyVertices));
                Assert.That(stored.submeshCount, Is.EqualTo(prepared.Submeshes.Length));

                Assert.That(storage.Vertices.ToArray(), Is.EqualTo(prepared.Vertices), "the vertices are the prepared ones");
                Assert.That(storage.TryAcquireIndexReadLease(stored.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indices), Is.True);
                Assert.That(indices.ToArray(), Is.EqualTo(prepared.Indices), "the first geometry needs no rebasing");
                Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);

                Assert.That(storage.TryGetTopology(stored, out NativeArray<int>.ReadOnly topology, out int topologyVertexCount), Is.True);
                Assert.That(topology.ToArray(), Is.EqualTo(prepared.TopologyOfVertex), "the control point of every render vertex");
                Assert.That(topologyVertexCount, Is.EqualTo(prepared.TopologyVertexCount));

                Assert.That(storage.TryGetSubmeshes(stored, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);
                Assert.That(
                    submeshes.ToArray(),
                    Is.EqualTo(new[] { new VpGeometrySubmesh(0, 3, 7), new VpGeometrySubmesh(3, 9, 2) }),
                    "index ranges and source material indices");
            }
        }

        [Test]
        public void TheStoredSubmeshes_CarryTheMaterialIndexAndNoMaterialName()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(CommonGeometryStorage.TryAppend(storage, Prepared(), out VpStoredGeometry stored), Is.True);
                Assert.That(storage.TryGetSubmeshes(stored, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);

                Assert.That(submeshes.ToArray().Select(s => s.materialIndex), Is.EqualTo(new[] { 7, 2 }), "source material indices");
                Assert.That(
                    typeof(VpGeometrySubmesh).GetFields().Select(f => f.Name),
                    Is.EquivalentTo(new[] { "indexOffset", "indexCount", "materialIndex" }),
                    "the runtime descriptor holds nothing else: no material name, Material or texture");
            }
        }

        [Test]
        public void AGeometryInTheFbxBasis_IsRefusedWithoutConvertingIt()
        {
            PreparedCommonGeometry fbx = Prepared(CommonGeometryBasis.FbxFile);
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(CommonGeometryStorage.TryAppend(storage, fbx, out VpStoredGeometry stored), Is.False, "fbx basis");

                Assert.That(stored, Is.EqualTo(default(VpStoredGeometry)));
                Assert.That(storage.VertexCount, Is.Zero, "nothing committed");
                Assert.That(storage.SubmeshCount, Is.Zero);
                Assert.That(fbx.Basis, Is.EqualTo(CommonGeometryBasis.FbxFile), "the input is not converted");
            }
        }

        [Test]
        public void AGeometryWithAStructuralProblem_IsRefusedAndTheStorageStaysUnchanged()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(CommonGeometryStorage.TryAppend(storage, Prepared(), out VpStoredGeometry first), Is.True, "a good one first");
                VpRenderVertex[] committed = storage.Vertices.ToArray();

                foreach ((PreparedCommonGeometry candidate, string label) in new[]
                         {
                             ((PreparedCommonGeometry)null, "null geometry"),
                             (Damaged(g => g.TopologyVertexCount = 1), "topology ids outside the count"),
                             (Damaged(g => g.Submeshes[1] = new CommonGeometrySubmesh { IndexStart = 4, IndexCount = 5, MaterialIndex = 2 }), "ragged submesh ranges"),
                             (Damaged(g => g.Submeshes[0] = new CommonGeometrySubmesh { IndexStart = 0, IndexCount = 3, MaterialIndex = -1 }), "negative material index"),
                             (Damaged(g => g.Indices[0] = (uint)g.Vertices.Length), "an index past the vertices"),
                             (Damaged(g => g.Basis = (CommonGeometryBasis)7), "an undefined basis"),
                         })
                {
                    Assert.That(CommonGeometryStorage.TryAppend(storage, candidate, out VpStoredGeometry rejected), Is.False, label);
                    Assert.That(rejected, Is.EqualTo(default(VpStoredGeometry)), label + " result");
                }

                Assert.That(storage.Vertices.ToArray(), Is.EqualTo(committed), "committed vertices unchanged");
                Assert.That(storage.SubmeshCount, Is.EqualTo(2), "submesh count unchanged");
                Assert.That(storage.TryGetTopology(first, out NativeArray<int>.ReadOnly topology, out _), Is.True);
                Assert.That(topology.ToArray(), Is.EqualTo(Prepared().TopologyOfVertex), "the stored geometry keeps its mapping");
            }
        }

        [Test]
        public void AnOpenSurface_IsRefusedByTheInputGate_AndTheStorageStaysUnchanged()
        {
            PreparedCommonGeometry open = Prepared();
            // drop the last face: three of the control-point edges now have a single face
            System.Array.Resize(ref open.Indices, open.Indices.Length - 3);
            open.Submeshes[1] = new CommonGeometrySubmesh { IndexStart = 3, IndexCount = 6, MaterialIndex = 2, MaterialName = "EndCap" };
            Assert.That(open.Validate(), Is.Empty, "structurally still a valid prepared geometry");
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(CommonGeometryStorage.TryAppend(storage, open, out VpStoredGeometry stored, out VpCutInputVerdict verdict), Is.False);
                Assert.That(verdict.rejection, Is.EqualTo(VpCutInputRejection.EdgeFaceCount), verdict.ToString());
                Assert.That(stored, Is.EqualTo(default(VpStoredGeometry)));
                Assert.That(storage.VertexCount, Is.Zero, "nothing committed");
                Assert.That(storage.SubmeshCount, Is.Zero);
            }
        }

        [Test]
        public void AnAcceptedGeometry_IsRecordedAsACutInput()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Assert.That(CommonGeometryStorage.TryAppend(storage, Prepared(), out VpStoredGeometry stored, out VpCutInputVerdict verdict), Is.True);
                Assert.That(verdict.Accepted, Is.True, verdict.ToString());
                Assert.That(stored.cutInputAccepted, Is.True);
            }
        }

        [Test]
        public void ANullStorage_IsRefused()
        {
            Assert.That(CommonGeometryStorage.TryAppend(null, Prepared(), out VpStoredGeometry stored), Is.False);
            Assert.That(stored, Is.EqualTo(default(VpStoredGeometry)));
        }

        [Test]
        public void TheAppendedGeometry_CanBeRegisteredAndRetiredThroughTheReferenceTable()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 2, 2);
                Assert.That(CommonGeometryStorage.TryAppend(storage, Prepared(), out VpStoredGeometry stored), Is.True);

                Assert.That(table.TryRegisterGeometry(stored, out VpGeometryReference reference), Is.True, "register");
                Assert.That(table.TryAddDisplayInstance(reference, out VpDisplayInstanceReference instance), Is.True, "instance");
                Assert.That(table.TryRetireGeometry(reference), Is.False, "an instance still references it");
                Assert.That(Storage(storage, stored), Is.EqualTo(new[] { new VpGeometrySubmesh(0, 3, 7), new VpGeometrySubmesh(3, 9, 2) }), "submeshes while live");

                Assert.That(table.TryRetireDisplayInstance(instance), Is.True, "retire the instance");
                Assert.That(table.TryRetireGeometry(reference), Is.True, "retire the geometry");

                Assert.That(storage.TryGetIndexState(stored.indexRange, out VpIndexRangeState state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Free), "its index range is retired once");
                Assert.That(storage.TryGetSubmeshes(stored, out _), Is.False, "and its metadata is no longer readable");
                Assert.That(storage.VertexCount, Is.EqualTo(6), "its vertices stay committed");
            }
        }

        private static PreparedCommonGeometry Damaged(System.Action<PreparedCommonGeometry> damage)
        {
            PreparedCommonGeometry geometry = Prepared();
            damage(geometry);
            return geometry;
        }

        private static VpGeometrySubmesh[] Storage(VpCpuGeometryStorage storage, VpStoredGeometry stored)
        {
            Assert.That(storage.TryGetSubmeshes(stored, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);
            return submeshes.ToArray();
        }
    }
}
