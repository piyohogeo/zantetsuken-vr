using System;
using NUnit.Framework;
using Unity.Collections;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// CPU VP index storage over the index range allocator (DESIGN 4.5.3): write views of exactly the reserved range and
    /// only while Reserved, separated ranges, partial publishes read as their prefix, reused space holding the new
    /// values, reading through a lease while Retiring, no reading after the lease returns, stale, foreign and default
    /// tokens, empty ranges, and disposal.
    /// </summary>
    public class VpCpuIndexStorageTests
    {
        private static VpIndexRangeHandle Reserve(VpCpuIndexStorage storage, int indexCount)
        {
            Assert.That(storage.TryReserve(indexCount, out VpIndexRangeHandle handle), Is.True, "reserve " + indexCount);
            return handle;
        }

        /// <summary>Writes the values from the start of the reserved range, whose write view must be at least as long.</summary>
        private static void Write(VpCpuIndexStorage storage, VpIndexRangeHandle handle, params uint[] values)
        {
            Assert.That(storage.TryGetReservedWriteView(handle, out NativeArray<uint> view), Is.True, "write view");
            Assert.That(view.Length, Is.GreaterThanOrEqualTo(values.Length), "write view length");
            for (int i = 0; i < values.Length; i++)
            {
                view[i] = values[i];
            }
        }

        private static VpIndexRangeHandle Published(VpCpuIndexStorage storage, params uint[] values)
        {
            VpIndexRangeHandle handle = Reserve(storage, values.Length);
            Write(storage, handle, values);
            Assert.That(storage.TryPublish(handle), Is.True, "publish");
            return handle;
        }

        private static VpIndexReadLease AssertReads(VpCpuIndexStorage storage, VpIndexRangeHandle handle, params uint[] expected)
        {
            Assert.That(storage.TryAcquireReadLease(handle, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True, "acquire lease");
            Assert.That(view.ToArray(), Is.EqualTo(expected), "lease view");
            AssertLeaseReads(storage, lease, expected);
            return lease;
        }

        private static void AssertLeaseReads(VpCpuIndexStorage storage, VpIndexReadLease lease, params uint[] expected)
        {
            Assert.That(storage.TryGetReadView(lease, out NativeArray<uint>.ReadOnly view), Is.True, "read view");
            Assert.That(view.ToArray(), Is.EqualTo(expected), "read view");
        }

        private static void AssertNoReadView(VpCpuIndexStorage storage, VpIndexReadLease lease, string label)
        {
            Assert.That(storage.TryGetReadView(lease, out NativeArray<uint>.ReadOnly view), Is.False, label);
            Assert.That(view.Length, Is.Zero, label + " view");
        }

        private static void AssertNoWriteView(VpCpuIndexStorage storage, VpIndexRangeHandle handle, string label)
        {
            Assert.That(storage.TryGetReservedWriteView(handle, out NativeArray<uint> view), Is.False, label);
            Assert.That(view.IsCreated, Is.False, label + " view");
        }

        private static void AssertState(VpCpuIndexStorage storage, VpIndexRangeHandle handle, VpIndexRangeState state, int indexStart, int indexCount)
        {
            Assert.That(storage.TryGetState(handle, out VpIndexRangeState actualState, out int actualStart, out int actualCount), Is.True, "state query");
            Assert.That(new object[] { actualState, actualStart, actualCount }, Is.EqualTo(new object[] { state, indexStart, indexCount }));
        }

        [Test]
        public void TheConstructor_SetsTheCapacitiesAndRejectsNegativeOnes()
        {
            using (var storage = new VpCpuIndexStorage(100, 4, Allocator.Persistent))
            {
                Assert.That(storage.IndexCapacity, Is.EqualTo(100));
                Assert.That(storage.DescriptorCapacity, Is.EqualTo(4));
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => new VpCpuIndexStorage(-1, 4, Allocator.Persistent));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpCpuIndexStorage(100, -1, Allocator.Persistent));
        }

        [Test]
        public void AWriteView_CoversExactlyTheReservedRange()
        {
            using (var storage = new VpCpuIndexStorage(20, 4, Allocator.Persistent))
            {
                Reserve(storage, 3);
                VpIndexRangeHandle handle = Reserve(storage, 4);

                Assert.That(storage.TryGetReservedWriteView(handle, out NativeArray<uint> view), Is.True);

                Assert.That(view.Length, Is.EqualTo(4));
                AssertState(storage, handle, VpIndexRangeState.Reserved, 3, 4);
            }
        }

        [Test]
        public void WritingOneRange_LeavesItsNeighboursUnchanged()
        {
            using (var storage = new VpCpuIndexStorage(20, 4, Allocator.Persistent))
            {
                VpIndexRangeHandle before = Reserve(storage, 3);
                VpIndexRangeHandle middle = Reserve(storage, 4);
                VpIndexRangeHandle after = Reserve(storage, 3);
                Write(storage, before, 1, 2, 3);
                Write(storage, after, 7, 8, 9);

                Write(storage, middle, 100, 101, 102, 103);

                foreach (VpIndexRangeHandle handle in new[] { before, middle, after })
                {
                    Assert.That(storage.TryPublish(handle), Is.True);
                }

                AssertReads(storage, before, 1, 2, 3);
                AssertReads(storage, middle, 100, 101, 102, 103);
                AssertReads(storage, after, 7, 8, 9);
            }
        }

        [Test]
        public void WriteViews_AreOnlyAvailableWhileReserved()
        {
            using (var storage = new VpCpuIndexStorage(20, 4, Allocator.Persistent))
            {
                VpIndexRangeHandle published = Published(storage, 1, 2);
                AssertNoWriteView(storage, published, "published");

                VpIndexReadLease lease = AssertReads(storage, published, 1, 2);
                Assert.That(storage.TryRetire(published), Is.True);
                AssertNoWriteView(storage, published, "retiring");
                Assert.That(storage.TryReleaseReadLease(lease), Is.True);
                AssertNoWriteView(storage, published, "retired");

                VpIndexRangeHandle cancelled = Reserve(storage, 3);
                Assert.That(storage.TryCancelReservation(cancelled), Is.True);
                AssertNoWriteView(storage, cancelled, "cancelled");
            }
        }

        [Test]
        public void APartialPublish_ReadsOnlyThePublishedPrefixAndReusesTheTail()
        {
            using (var storage = new VpCpuIndexStorage(8, 4, Allocator.Persistent))
            {
                VpIndexRangeHandle handle = Reserve(storage, 6);
                Write(storage, handle, 10, 11, 12);

                Assert.That(storage.TryPublish(handle, 3), Is.True);

                AssertState(storage, handle, VpIndexRangeState.Published, 0, 3);
                AssertReads(storage, handle, 10, 11, 12);
                VpIndexRangeHandle tail = Reserve(storage, 5);
                AssertState(storage, tail, VpIndexRangeState.Reserved, 3, 5);
                AssertReads(storage, handle, 10, 11, 12);
            }
        }

        [Test]
        public void PublishingNothing_ReadsAnEmptyViewAt0()
        {
            using (var storage = new VpCpuIndexStorage(8, 4, Allocator.Persistent))
            {
                Published(storage, 1, 2);
                VpIndexRangeHandle handle = Reserve(storage, 4);

                Assert.That(storage.TryPublish(handle, 0), Is.True);

                AssertState(storage, handle, VpIndexRangeState.Published, 0, 0);
                AssertReads(storage, handle);
            }
        }

        [Test]
        public void ReusedSpace_KeepsTheOldValuesUntilOverwrittenAndReadsTheNewOnes()
        {
            using (var storage = new VpCpuIndexStorage(4, 2, Allocator.Persistent))
            {
                VpIndexRangeHandle old = Published(storage, 1, 2, 3, 4);
                Assert.That(storage.TryRetire(old), Is.True);

                VpIndexRangeHandle reused = Reserve(storage, 4);

                AssertState(storage, reused, VpIndexRangeState.Reserved, 0, 4);
                Assert.That(storage.TryGetReservedWriteView(reused, out NativeArray<uint> view), Is.True);
                Assert.That(view.ToArray(), Is.EqualTo(new uint[] { 1, 2, 3, 4 }), "reused space is not cleared");
                Write(storage, reused, 50, 60, 70, 80);
                Assert.That(storage.TryPublish(reused), Is.True);
                AssertReads(storage, reused, 50, 60, 70, 80);
            }
        }

        [Test]
        public void AHeldLease_KeepsReadingWhileTheRangeRetires()
        {
            using (var storage = new VpCpuIndexStorage(8, 2, Allocator.Persistent))
            {
                VpIndexRangeHandle handle = Published(storage, 5, 6, 7);
                VpIndexReadLease lease = AssertReads(storage, handle, 5, 6, 7);

                Assert.That(storage.TryRetire(handle), Is.True);

                AssertState(storage, handle, VpIndexRangeState.Retiring, 0, 3);
                AssertLeaseReads(storage, lease, 5, 6, 7);
                Assert.That(storage.TryAcquireReadLease(handle, out VpIndexReadLease refused, out NativeArray<uint>.ReadOnly refusedView), Is.False);
                Assert.That(refused, Is.EqualTo(default(VpIndexReadLease)));
                Assert.That(refusedView.Length, Is.Zero);

                Assert.That(storage.TryReleaseReadLease(lease), Is.True);
                AssertNoReadView(storage, lease, "returned while retiring");
                AssertState(storage, handle, VpIndexRangeState.Free, 0, 3);
            }
        }

        [Test]
        public void AReturnedLease_CannotReadAgain()
        {
            using (var storage = new VpCpuIndexStorage(8, 2, Allocator.Persistent))
            {
                VpIndexRangeHandle handle = Published(storage, 5, 6, 7);
                VpIndexReadLease returned = AssertReads(storage, handle, 5, 6, 7);
                VpIndexReadLease kept = AssertReads(storage, handle, 5, 6, 7);

                Assert.That(storage.TryReleaseReadLease(returned), Is.True);

                AssertNoReadView(storage, returned, "returned");
                Assert.That(storage.TryReleaseReadLease(returned), Is.False, "returned twice");
                AssertLeaseReads(storage, kept, 5, 6, 7);
            }
        }

        [Test]
        public void StaleHandlesAndLeases_AreRejected()
        {
            // One descriptor and four indices, so the new registration reuses the descriptor, the lease slot and the space.
            using (var storage = new VpCpuIndexStorage(4, 1, Allocator.Persistent))
            {
                VpIndexRangeHandle oldHandle = Published(storage, 1, 2, 3, 4);
                VpIndexReadLease oldLease = AssertReads(storage, oldHandle, 1, 2, 3, 4);
                Assert.That(storage.TryRetire(oldHandle), Is.True);
                Assert.That(storage.TryReleaseReadLease(oldLease), Is.True);

                VpIndexRangeHandle newHandle = Reserve(storage, 4);
                AssertNoWriteView(storage, oldHandle, "old handle write");
                Write(storage, newHandle, 9, 8, 7, 6);
                Assert.That(storage.TryPublish(newHandle), Is.True);
                VpIndexReadLease newLease = AssertReads(storage, newHandle, 9, 8, 7, 6);

                Assert.That(storage.TryGetState(oldHandle, out _, out _, out _), Is.False, "old state");
                Assert.That(storage.TryAcquireReadLease(oldHandle, out _, out NativeArray<uint>.ReadOnly oldView), Is.False, "old acquire");
                Assert.That(oldView.Length, Is.Zero, "old acquire view");
                AssertNoReadView(storage, oldLease, "old lease");
                Assert.That(storage.TryReleaseReadLease(oldLease), Is.False, "old lease release");
                AssertLeaseReads(storage, newLease, 9, 8, 7, 6);
            }
        }

        [Test]
        public void ForeignAndDefaultTokens_AreRejected()
        {
            using (var storage = new VpCpuIndexStorage(8, 2, Allocator.Persistent))
            using (var other = new VpCpuIndexStorage(8, 2, Allocator.Persistent))
            {
                VpIndexRangeHandle reserved = Reserve(storage, 2);
                VpIndexRangeHandle published = Published(storage, 1, 2);
                VpIndexRangeHandle foreignReserved = Reserve(other, 2);
                VpIndexRangeHandle foreignPublished = Published(other, 3, 4);
                VpIndexReadLease foreignLease = AssertReads(other, foreignPublished, 3, 4);

                AssertNoWriteView(storage, foreignReserved, "foreign write");
                AssertNoWriteView(storage, default, "default write");
                Assert.That(storage.TryAcquireReadLease(foreignPublished, out _, out _), Is.False, "foreign acquire");
                Assert.That(storage.TryAcquireReadLease(default, out _, out _), Is.False, "default acquire");
                AssertNoReadView(storage, foreignLease, "foreign lease");
                AssertNoReadView(storage, default, "default lease");

                AssertState(storage, reserved, VpIndexRangeState.Reserved, 0, 2);
                AssertState(storage, published, VpIndexRangeState.Published, 2, 2);
                AssertLeaseReads(other, foreignLease, 3, 4);
            }
        }

        [Test]
        public void EmptyRanges_HaveEmptyViewsThroughTheWholeLifecycle()
        {
            using (var storage = new VpCpuIndexStorage(4, 2, Allocator.Persistent))
            {
                Published(storage, 1, 2, 3, 4);
                VpIndexRangeHandle empty = Reserve(storage, 0);
                AssertState(storage, empty, VpIndexRangeState.Reserved, 0, 0);
                Assert.That(storage.TryGetReservedWriteView(empty, out NativeArray<uint> writeView), Is.True);
                Assert.That(writeView.Length, Is.Zero);

                Assert.That(storage.TryPublish(empty), Is.True);
                VpIndexReadLease lease = AssertReads(storage, empty);
                Assert.That(storage.TryRetire(empty), Is.True);
                AssertLeaseReads(storage, lease);
                Assert.That(storage.TryReleaseReadLease(lease), Is.True);

                AssertNoReadView(storage, lease, "returned");
                AssertState(storage, empty, VpIndexRangeState.Free, 0, 0);
            }
        }

        [Test]
        public void AfterDispose_OperationsThrowAndDisposingAgainDoesNothing()
        {
            var storage = new VpCpuIndexStorage(8, 2, Allocator.Persistent);
            VpIndexRangeHandle reserved = Reserve(storage, 2);
            VpIndexRangeHandle published = Published(storage, 1, 2);
            VpIndexReadLease lease = AssertReads(storage, published, 1, 2);

            storage.Dispose();

            Assert.Throws<ObjectDisposedException>(() => storage.TryReserve(1, out _), "reserve");
            Assert.Throws<ObjectDisposedException>(() => storage.TryGetReservedWriteView(reserved, out _), "write view");
            Assert.Throws<ObjectDisposedException>(() => storage.TryPublish(reserved), "publish");
            Assert.Throws<ObjectDisposedException>(() => storage.TryPublish(reserved, 1), "partial publish");
            Assert.Throws<ObjectDisposedException>(() => storage.TryCancelReservation(reserved), "cancel");
            Assert.Throws<ObjectDisposedException>(() => storage.TryAcquireReadLease(published, out _, out _), "acquire");
            Assert.Throws<ObjectDisposedException>(() => storage.TryGetReadView(lease, out _), "read view");
            Assert.Throws<ObjectDisposedException>(() => storage.TryRetire(published), "retire");
            Assert.Throws<ObjectDisposedException>(() => storage.TryReleaseReadLease(lease), "release");
            Assert.Throws<ObjectDisposedException>(() => storage.TryGetState(published, out _, out _, out _), "state");
            Assert.DoesNotThrow(storage.Dispose, "dispose again");
        }
    }
}
