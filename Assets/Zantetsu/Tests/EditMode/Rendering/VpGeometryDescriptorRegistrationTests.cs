using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering.Tests
{
    public class VpGeometryDescriptorRegistrationTests
    {
        private static VpStoredGeometry Append(VpCpuGeometryStorage storage)
        {
            Assert.That(storage.TryAppend(Resources.GetBuiltinResource<Mesh>("Quad.fbx"), out VpStoredGeometry stored), Is.True);
            return stored;
        }

        private static VpGeometryReference Register(VpGeometryReferenceTable table, VpStoredGeometry stored)
        {
            Assert.That(table.TryRegisterGeometry(stored, out VpGeometryReference reference), Is.True);
            return reference;
        }

        [Test]
        public void AnExternallyReusedDescriptorCanRegisterWhileItsOldGeometryRemainsLive()
        {
            using (var storage = new VpCpuGeometryStorage(32, 32, 2, 8, 16, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 4, 1);
                VpStoredGeometry oldStored = Append(storage);
                VpGeometryReference oldReference = Register(table, oldStored);
                Assert.That(storage.TryRetireIndices(oldStored.indexRange), Is.True);

                VpStoredGeometry current = Append(storage);
                Assert.That(current.indexRange.descriptor, Is.EqualTo(oldStored.indexRange.descriptor));
                Assert.That(current.indexRange.generation, Is.Not.EqualTo(oldStored.indexRange.generation));
                Assert.That(table.TryRegisterGeometryWithDisplayInstance(current, out VpGeometryReference reference, out VpDisplayInstanceReference instance), Is.True);
                Assert.That(table.TryRegisterGeometry(oldStored, out _), Is.False, "stale storage generation");
                Assert.That(table.TryRetireGeometry(oldReference), Is.False, "stale index cannot retire the current registration");
                Assert.That(table.TryRegisterGeometry(current, out _), Is.False, "current descriptor stays registered");
                Assert.That(table.TryGetGeometry(oldReference, out VpStoredGeometry stillOld, out _), Is.True);
                Assert.That(stillOld, Is.EqualTo(oldStored), "external retirement does not change the old geometry token");

                Assert.That(table.TryRetireDisplayInstance(instance), Is.True);
                Assert.That(table.TryRetireGeometry(reference), Is.True);
                VpStoredGeometry next = Append(storage);
                VpGeometryReference nextReference = Register(table, next);
                Assert.That(nextReference.slot, Is.EqualTo(reference.slot));
                Assert.That(next.indexRange.descriptor, Is.EqualTo(current.indexRange.descriptor));
                Assert.That(table.TryRegisterGeometry(next, out _), Is.False);
                Assert.That(table.TryRetireGeometry(oldReference), Is.False);
                Assert.That(table.TryRegisterGeometry(next, out _), Is.False, "old token still cannot clear the newest mapping");
                Assert.That(table.LiveGeometryCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void RetiringADescriptorClearsItsMappingBeforeItsGeometrySlotIsReused()
        {
            using (var storage = new VpCpuGeometryStorage(32, 32, 3, 8, 16, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 4, 0);
                VpStoredGeometry first = Append(storage);
                VpGeometryReference firstReference = Register(table, first);
                Assert.That(storage.TryAcquireIndexReadLease(first.indexRange, out VpIndexReadLease lease, out _), Is.True);

                // Both descriptors will next reach generation 2. A stale mapping to the reused geometry slot
                // would then mistake the second descriptor's generation for the first one's registration.
                VpStoredGeometry discarded = Append(storage);
                Assert.That(storage.TryRetireIndices(discarded.indexRange), Is.True);
                Assert.That(table.TryRetireGeometry(firstReference), Is.True);
                VpStoredGeometry second = Append(storage);
                VpGeometryReference secondReference = Register(table, second);
                Assert.That(secondReference.slot, Is.EqualTo(firstReference.slot));
                Assert.That(second.indexRange.descriptor, Is.Not.EqualTo(first.indexRange.descriptor));

                Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);
                VpStoredGeometry firstReused = Append(storage);
                Assert.That(firstReused.indexRange.descriptor, Is.EqualTo(first.indexRange.descriptor));
                Assert.That(firstReused.indexRange.generation, Is.EqualTo(second.indexRange.generation));
                Register(table, firstReused);
                Assert.That(table.TryRegisterGeometry(second, out _), Is.False);
                Assert.That(table.TryRegisterGeometry(firstReused, out _), Is.False);
                Assert.That(table.LiveGeometryCount, Is.EqualTo(2));
            }
        }
    }
}
