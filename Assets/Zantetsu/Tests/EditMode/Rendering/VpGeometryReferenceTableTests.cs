using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Geometry references and display instances over the CPU VP geometry storage (DESIGN 4.5.3): a geometry shared by
    /// several instances, instances retired once without retiring the geometry, an explicit geometry retirement that
    /// succeeds once and only without instances, index ranges held Retiring by read leases and reused after the last
    /// returns while vertices stay committed, duplicate registration, capacity, default, foreign and stale tokens, slot
    /// generations, and a geometry with an empty index range.
    /// </summary>
    public class VpGeometryReferenceTableTests
    {
        // Built-in Quad: 4 vertices / 6 indices.
        private const int QuadVertices = 4;
        private const int QuadIndices = 6;

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

        private static Mesh Quad()
        {
            Mesh mesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            Assert.That(mesh, Is.Not.Null);
            return mesh;
        }

        /// <summary>Three vertices with normals and a triangle submesh without indices.</summary>
        private Mesh EmptyTriangles()
        {
            var mesh = new Mesh();
            _meshes.Add(mesh);
            mesh.SetVertices(new[] { Vector3.zero, Vector3.right, Vector3.up });
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back });
            mesh.SetTriangles(Array.Empty<int>(), 0);
            return mesh;
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Mesh mesh)
        {
            Assert.That(storage.TryAppend(mesh, out VpStoredGeometry geometry), Is.True, "append");
            return geometry;
        }

        private static VpGeometryReference Register(VpGeometryReferenceTable table, VpStoredGeometry geometry)
        {
            Assert.That(table.TryRegisterGeometry(geometry, out VpGeometryReference reference), Is.True, "register");
            return reference;
        }

        private static VpDisplayInstanceReference AddInstance(VpGeometryReferenceTable table, VpGeometryReference geometry)
        {
            Assert.That(table.TryAddDisplayInstance(geometry, out VpDisplayInstanceReference instance), Is.True, "add instance");
            return instance;
        }

        private static void AssertLive(VpGeometryReferenceTable table, VpGeometryReference geometry, VpStoredGeometry expected, int liveInstances, string label)
        {
            Assert.That(table.TryGetGeometry(geometry, out VpStoredGeometry stored, out int actualInstances), Is.True, label + " live");
            Assert.That(stored, Is.EqualTo(expected), label + " stored geometry");
            Assert.That(actualInstances, Is.EqualTo(liveInstances), label + " live instances");
        }

        private static void AssertEnded(VpGeometryReferenceTable table, VpGeometryReference geometry, string label)
        {
            Assert.That(table.TryGetGeometry(geometry, out VpStoredGeometry stored, out int instances), Is.False, label + " query");
            Assert.That(stored, Is.EqualTo(default(VpStoredGeometry)), label + " stored");
            Assert.That(instances, Is.Zero, label + " instances");
            Assert.That(table.TryAddDisplayInstance(geometry, out VpDisplayInstanceReference instance), Is.False, label + " add instance");
            Assert.That(instance, Is.EqualTo(default(VpDisplayInstanceReference)), label + " instance token");
            Assert.That(table.TryRetireGeometry(geometry), Is.False, label + " retire");
        }

        private static void AssertCounts(VpGeometryReferenceTable table, int geometries, int instances, string label)
        {
            Assert.That(
                new[] { table.LiveGeometryCount, table.LiveDisplayInstanceCount },
                Is.EqualTo(new[] { geometries, instances }),
                label + " live geometries, live instances");
        }

        private static void AssertIndexState(VpCpuGeometryStorage storage, VpStoredGeometry geometry, VpIndexRangeState state, int indexStart, int indexCount, string label)
        {
            Assert.That(storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState actualState, out int actualStart, out int actualCount), Is.True, label + " index query");
            Assert.That(
                new object[] { actualState, actualStart, actualCount },
                Is.EqualTo(new object[] { state, indexStart, indexCount }),
                label + " index state, start, count");
        }

        [Test]
        public void TheConstructor_SetsTheCapacitiesAndRejectsInvalidArguments()
        {
            using (var storage = new VpCpuGeometryStorage(16, 16, 2, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 3, 5);
                Assert.That(table.GeometryCapacity, Is.EqualTo(3));
                Assert.That(table.DisplayInstanceCapacity, Is.EqualTo(5));
                AssertCounts(table, 0, 0, "new table");

                Assert.Throws<ArgumentNullException>(() => new VpGeometryReferenceTable(null, 3, 5));
                Assert.Throws<ArgumentOutOfRangeException>(() => new VpGeometryReferenceTable(storage, -1, 5));
                Assert.Throws<ArgumentOutOfRangeException>(() => new VpGeometryReferenceTable(storage, 3, -1));
            }
        }

        [Test]
        public void AStorage_TakesOnlyOneReferenceTable()
        {
            Mesh quad = Quad();
            using (var storage = new VpCpuGeometryStorage(16, 16, 2, Allocator.Persistent))
            using (var otherStorage = new VpCpuGeometryStorage(16, 16, 2, Allocator.Persistent))
            {
                // Invalid constructions are rejected before the storage is claimed.
                Assert.Throws<ArgumentOutOfRangeException>(() => new VpGeometryReferenceTable(storage, -1, 1), "negative geometry capacity");
                Assert.Throws<ArgumentOutOfRangeException>(() => new VpGeometryReferenceTable(storage, 1, -1), "negative instance capacity");
                var table = new VpGeometryReferenceTable(storage, 1, 1);

                Assert.Throws<InvalidOperationException>(() => new VpGeometryReferenceTable(storage, 1, 1), "a second table");
                Assert.Throws<InvalidOperationException>(() => new VpGeometryReferenceTable(storage, 4, 4), "a second table again");

                // The first table keeps working.
                VpStoredGeometry stored = Append(storage, quad);
                VpGeometryReference geometry = Register(table, stored);
                VpDisplayInstanceReference instance = AddInstance(table, geometry);
                AssertLive(table, geometry, stored, 1, "the first table's geometry");
                Assert.That(table.TryRetireDisplayInstance(instance), Is.True, "retire instance");
                Assert.That(table.TryRetireGeometry(geometry), Is.True, "retire geometry");
                AssertIndexState(storage, stored, VpIndexRangeState.Free, 0, QuadIndices, "retired through the first table");

                // Another storage takes its own single table.
                var otherTable = new VpGeometryReferenceTable(otherStorage, 1, 1);
                VpStoredGeometry otherStored = Append(otherStorage, quad);
                AssertLive(otherTable, Register(otherTable, otherStored), otherStored, 0, "the other storage's table");
                Assert.Throws<InvalidOperationException>(() => new VpGeometryReferenceTable(otherStorage, 1, 1), "a second table of the other storage");
            }
        }

        [Test]
        public void ASharedGeometry_OutlivesItsInstancesUntilRetiredExplicitlyOnce()
        {
            using (var storage = new VpCpuGeometryStorage(16, 16, 2, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 2, 4);
                VpStoredGeometry stored = Append(storage, Quad());
                VpGeometryReference geometry = Register(table, stored);
                VpDisplayInstanceReference first = AddInstance(table, geometry);
                VpDisplayInstanceReference second = AddInstance(table, geometry);
                VpDisplayInstanceReference third = AddInstance(table, geometry);
                AssertLive(table, geometry, stored, 3, "shared by three");
                AssertCounts(table, 1, 3, "shared by three");
                Assert.That(table.TryGetDisplayInstanceGeometry(second, out VpGeometryReference referenced), Is.True);
                Assert.That(referenced, Is.EqualTo(geometry), "the instance references the geometry");

                // The first instance ends once; the geometry stays live.
                Assert.That(table.TryRetireDisplayInstance(first), Is.True, "retire first");
                AssertLive(table, geometry, stored, 2, "after the first instance");
                Assert.That(table.TryRetireDisplayInstance(first), Is.False, "retire first twice");
                Assert.That(table.TryGetDisplayInstanceGeometry(first, out _), Is.False, "retired instance query");
                AssertCounts(table, 1, 2, "after retiring first twice");

                // While instances remain, the geometry cannot retire and nothing changes.
                Assert.That(table.TryRetireGeometry(geometry), Is.False, "retire geometry with instances");
                AssertLive(table, geometry, stored, 2, "after the refused retirement");
                AssertCounts(table, 1, 2, "after the refused retirement");
                AssertIndexState(storage, stored, VpIndexRangeState.Published, 0, QuadIndices, "after the refused retirement");

                // Retiring the last instance does not retire the geometry, which can gain instances again.
                Assert.That(table.TryRetireDisplayInstance(second), Is.True, "retire second");
                Assert.That(table.TryRetireDisplayInstance(third), Is.True, "retire third");
                AssertLive(table, geometry, stored, 0, "without instances");
                AssertIndexState(storage, stored, VpIndexRangeState.Published, 0, QuadIndices, "without instances");
                VpDisplayInstanceReference again = AddInstance(table, geometry);
                AssertLive(table, geometry, stored, 1, "an instance added again");
                Assert.That(table.TryRetireDisplayInstance(again), Is.True, "retire the added instance");

                // The explicit retirement succeeds once and retires the index range; then no instance can be added.
                Assert.That(table.TryRetireGeometry(geometry), Is.True, "retire geometry");
                AssertIndexState(storage, stored, VpIndexRangeState.Free, 0, QuadIndices, "retired without leases");
                AssertEnded(table, geometry, "retired geometry");
                AssertCounts(table, 0, 0, "after retirement");
            }
        }

        [Test]
        public void ARetiredGeometryWithALease_KeepsItsIndicesRetiringUntilTheLeaseReturnsAndThenTheirSpaceIsReused()
        {
            // Room for one quad's indices only, so the next geometry fits only in the retired geometry's space.
            Mesh quad = Quad();
            using (var storage = new VpCpuGeometryStorage(16, QuadIndices, 2, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 1, 2);
                VpStoredGeometry oldStored = Append(storage, quad);
                VpGeometryReference oldGeometry = Register(table, oldStored);
                VpDisplayInstanceReference first = AddInstance(table, oldGeometry);
                VpDisplayInstanceReference second = AddInstance(table, oldGeometry);
                Assert.That(storage.TryAcquireIndexReadLease(oldStored.indexRange, out VpIndexReadLease lease, out _), Is.True, "lease");
                VpRenderVertex[] committed = storage.Vertices.ToArray();

                Assert.That(table.TryRetireDisplayInstance(first), Is.True, "retire first");
                Assert.That(table.TryRetireDisplayInstance(second), Is.True, "retire second");
                Assert.That(table.TryRetireGeometry(oldGeometry), Is.True, "retire geometry while the lease is held");

                AssertEnded(table, oldGeometry, "retired geometry");
                AssertCounts(table, 0, 0, "after retirement");
                AssertIndexState(storage, oldStored, VpIndexRangeState.Retiring, 0, QuadIndices, "retired with a lease");
                Assert.That(table.TryRetireGeometry(oldGeometry), Is.False, "retire geometry twice");
                AssertIndexState(storage, oldStored, VpIndexRangeState.Retiring, 0, QuadIndices, "after retiring twice");
                Assert.That(storage.TryGetIndexReadView(lease, out NativeArray<uint>.ReadOnly view), Is.True, "the lease still reads");
                Assert.That(view.ToArray(), Is.EqualTo(quad.triangles.Select(i => (uint)i)), "the lease reads the old indices");

                Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True, "return the lease");
                AssertIndexState(storage, oldStored, VpIndexRangeState.Free, 0, QuadIndices, "after the lease returns");

                VpStoredGeometry newStored = Append(storage, quad);
                AssertIndexState(storage, newStored, VpIndexRangeState.Published, 0, QuadIndices, "the next geometry reuses the space");
                Assert.That(newStored.vertexStart, Is.EqualTo(QuadVertices), "vertices keep appending");
                Assert.That(storage.Vertices.ToArray().Take(committed.Length), Is.EqualTo(committed), "earlier vertices unchanged");
                VpGeometryReference newGeometry = Register(table, newStored);
                AssertLive(table, newGeometry, newStored, 0, "the next geometry");
                AssertEnded(table, oldGeometry, "the old token after its slot is reused");
            }
        }

        [Test]
        public void RegistrationOfTheSameOrAnUnpublishedIndexRange_IsRejected()
        {
            using (var storage = new VpCpuGeometryStorage(16, 32, 4, Allocator.Persistent))
            using (var other = new VpCpuGeometryStorage(16, 32, 4, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 4, 4);
                VpStoredGeometry stored = Append(storage, Quad());
                VpStoredGeometry unregistered = Append(storage, Quad());
                VpStoredGeometry retired = Append(storage, Quad());
                Assert.That(storage.TryRetireIndices(retired.indexRange), Is.True, "retire an unregistered index range");
                VpStoredGeometry foreign = Append(other, Quad());
                VpGeometryReference geometry = Register(table, stored);

                foreach ((VpStoredGeometry candidate, string label) in new[]
                         {
                             (stored, "the same index range"),
                             (default(VpStoredGeometry), "default"),
                             (foreign, "another storage's geometry"),
                             (retired, "a retired index range"),
                             (new VpStoredGeometry(storage.VertexCount - 1, 2, unregistered.indexRange), "uncommitted vertices"),
                             (new VpStoredGeometry(-1, QuadVertices, unregistered.indexRange), "negative vertex start"),
                         })
                {
                    Assert.That(table.TryRegisterGeometry(candidate, out VpGeometryReference rejected), Is.False, label);
                    Assert.That(rejected, Is.EqualTo(default(VpGeometryReference)), label + " token");
                }

                AssertCounts(table, 1, 0, "after the rejected registrations");
                AssertLive(table, geometry, stored, 0, "the registered geometry");
                AssertLive(table, Register(table, unregistered), unregistered, 0, "the published range with its own vertices");
            }
        }

        [Test]
        public void AnIndexRangeRetiredOutsideTheTable_LeavesTheGeometryLiveAndItsRetirementRefused()
        {
            using (var storage = new VpCpuGeometryStorage(16, 16, 2, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 1, 1);
                VpStoredGeometry stored = Append(storage, Quad());
                VpGeometryReference geometry = Register(table, stored);
                Assert.That(storage.TryRetireIndices(stored.indexRange), Is.True, "retire the index range directly");

                Assert.That(table.TryRetireGeometry(geometry), Is.False, "the storage refuses a second retirement");

                AssertLive(table, geometry, stored, 0, "still registered");
                AssertCounts(table, 1, 0, "unchanged");
            }
        }

        [Test]
        public void FullTables_RejectGeometriesAndInstancesWithoutChangingAnything()
        {
            using (var storage = new VpCpuGeometryStorage(16, 16, 4, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 1, 2);
                VpStoredGeometry stored = Append(storage, Quad());
                VpGeometryReference geometry = Register(table, stored);
                VpDisplayInstanceReference first = AddInstance(table, geometry);
                AddInstance(table, geometry);

                Assert.That(table.TryRegisterGeometry(Append(storage, Quad()), out VpGeometryReference rejectedGeometry), Is.False, "geometry capacity");
                Assert.That(table.TryAddDisplayInstance(geometry, out VpDisplayInstanceReference rejectedInstance), Is.False, "instance capacity");

                Assert.That(rejectedGeometry, Is.EqualTo(default(VpGeometryReference)));
                Assert.That(rejectedInstance, Is.EqualTo(default(VpDisplayInstanceReference)));
                AssertLive(table, geometry, stored, 2, "full");
                AssertCounts(table, 1, 2, "full");
                Assert.That(table.TryRetireDisplayInstance(first), Is.True);
                AddInstance(table, geometry);
                AssertLive(table, geometry, stored, 2, "a freed instance slot is used again");
            }
        }

        [Test]
        public void DefaultAndForeignTokens_AreRejectedWithoutChangingAnything()
        {
            using (var storage = new VpCpuGeometryStorage(16, 16, 2, Allocator.Persistent))
            using (var otherStorage = new VpCpuGeometryStorage(16, 16, 2, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 1, 1);
                var other = new VpGeometryReferenceTable(otherStorage, 1, 1);
                VpStoredGeometry stored = Append(storage, Quad());
                VpGeometryReference geometry = Register(table, stored);
                VpDisplayInstanceReference instance = AddInstance(table, geometry);
                VpStoredGeometry otherStored = Append(otherStorage, Quad());
                VpGeometryReference foreignGeometry = Register(other, otherStored);
                VpDisplayInstanceReference foreignInstance = AddInstance(other, foreignGeometry);

                foreach ((VpGeometryReference token, VpDisplayInstanceReference instanceToken, string label) in new[]
                         {
                             (default(VpGeometryReference), default(VpDisplayInstanceReference), "default"),
                             (foreignGeometry, foreignInstance, "foreign"),
                         })
                {
                    AssertEnded(table, token, label + " geometry");
                    Assert.That(table.TryRetireDisplayInstance(instanceToken), Is.False, label + " retire instance");
                    Assert.That(table.TryGetDisplayInstanceGeometry(instanceToken, out _), Is.False, label + " instance query");
                }

                AssertLive(table, geometry, stored, 1, "own geometry");
                AssertCounts(table, 1, 1, "own table");
                Assert.That(table.TryGetDisplayInstanceGeometry(instance, out _), Is.True, "own instance");
                AssertLive(other, foreignGeometry, otherStored, 1, "other geometry");
                AssertIndexState(storage, stored, VpIndexRangeState.Published, 0, QuadIndices, "own index range");
            }
        }

        [Test]
        public void ReusedSlots_RejectTheTokensOfTheirEarlierUses()
        {
            using (var storage = new VpCpuGeometryStorage(16, 16, 4, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 1, 1);
                VpStoredGeometry oldStored = Append(storage, Quad());
                VpGeometryReference oldGeometry = Register(table, oldStored);
                VpDisplayInstanceReference oldInstance = AddInstance(table, oldGeometry);
                Assert.That(table.TryRetireDisplayInstance(oldInstance), Is.True);
                VpDisplayInstanceReference newInstance = AddInstance(table, oldGeometry);

                Assert.That(newInstance, Is.Not.EqualTo(oldInstance), "the instance slot has a new generation");
                Assert.That(table.TryRetireDisplayInstance(oldInstance), Is.False, "old instance token");
                AssertLive(table, oldGeometry, oldStored, 1, "the geometry counts only the new instance");

                Assert.That(table.TryRetireDisplayInstance(newInstance), Is.True);
                Assert.That(table.TryRetireGeometry(oldGeometry), Is.True);
                VpStoredGeometry newStored = Append(storage, Quad());
                VpGeometryReference newGeometry = Register(table, newStored);

                Assert.That(newGeometry, Is.Not.EqualTo(oldGeometry), "the geometry slot has a new generation");
                AssertEnded(table, oldGeometry, "old geometry token");
                AssertLive(table, newGeometry, newStored, 0, "the new geometry");
                Assert.That(table.TryRetireDisplayInstance(newInstance), Is.False, "an instance of the earlier geometry");
            }
        }

        [Test]
        public void SlotsAtTheLastGeneration_AreNotUsedAgainAndTheirTokensStayRejected()
        {
            // One generation per slot: each slot is used up after its first use.
            using (var storage = new VpCpuGeometryStorage(16, 16, 4, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 1, 1, 1);
                VpGeometryReference geometry = Register(table, Append(storage, Quad()));
                VpDisplayInstanceReference instance = AddInstance(table, geometry);
                Assert.That(table.TryRetireDisplayInstance(instance), Is.True);

                Assert.That(table.TryAddDisplayInstance(geometry, out _), Is.False, "the used-up instance slot");
                Assert.That(table.TryRetireDisplayInstance(instance), Is.False, "the old instance token");

                Assert.That(table.TryRetireGeometry(geometry), Is.True);
                Assert.That(table.TryRegisterGeometry(Append(storage, Quad()), out _), Is.False, "the used-up geometry slot");
                AssertEnded(table, geometry, "the old geometry token");
                AssertCounts(table, 0, 0, "used up");
            }
        }

        [Test]
        public void AGeometryWithAnEmptyIndexRange_GoesThroughTheSameLifetime()
        {
            using (var storage = new VpCpuGeometryStorage(16, 16, 2, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 1, 2);
                VpStoredGeometry stored = Append(storage, EmptyTriangles());
                AssertIndexState(storage, stored, VpIndexRangeState.Published, 0, 0, "empty index range");

                VpGeometryReference geometry = Register(table, stored);
                VpDisplayInstanceReference instance = AddInstance(table, geometry);
                Assert.That(storage.TryAcquireIndexReadLease(stored.indexRange, out VpIndexReadLease lease, out _), Is.True, "lease");
                Assert.That(table.TryRetireGeometry(geometry), Is.False, "retire with an instance");
                Assert.That(table.TryRetireDisplayInstance(instance), Is.True);
                Assert.That(table.TryRetireGeometry(geometry), Is.True, "retire");

                AssertIndexState(storage, stored, VpIndexRangeState.Retiring, 0, 0, "retired with a lease");
                AssertEnded(table, geometry, "retired empty geometry");
                Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);
                AssertIndexState(storage, stored, VpIndexRangeState.Free, 0, 0, "after the lease returns");
                Assert.That(storage.VertexCount, Is.EqualTo(3), "its vertices stay committed");
            }
        }
    }
}
