using NUnit.Framework;
using Unity.Collections;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// One small-capacity scenario over the public CPU VP index storage API (DESIGN 4.5.3): a partial publish read
    /// through two leases, its space held while Retiring and reused only after the last lease returns, a reservation
    /// failing for lack of space without changing anything, neighbouring ranges merging after retirement, a full
    /// re-reservation overwriting the old contents, cancelled space reused, and stale tokens rejected. Placement is
    /// observed only through reservation starts, states and read values.
    /// </summary>
    public class VpCpuIndexStorageReuseScenarioTests
    {
        private const int Capacity = 12;

        private static VpIndexRangeHandle Reserve(VpCpuIndexStorage storage, int indexCount, int expectedStart)
        {
            Assert.That(storage.TryReserve(indexCount, out VpIndexRangeHandle handle), Is.True, "reserve " + indexCount);
            AssertState(storage, handle, VpIndexRangeState.Reserved, expectedStart, indexCount);
            return handle;
        }

        private static void Write(VpCpuIndexStorage storage, VpIndexRangeHandle handle, params uint[] values)
        {
            Assert.That(storage.TryGetReservedWriteView(handle, out NativeArray<uint> view), Is.True, "write view");
            Assert.That(view.Length, Is.GreaterThanOrEqualTo(values.Length), "write view length");
            for (int i = 0; i < values.Length; i++)
            {
                view[i] = values[i];
            }
        }

        private static void AssertState(VpCpuIndexStorage storage, VpIndexRangeHandle handle, VpIndexRangeState state, int indexStart, int indexCount)
        {
            Assert.That(storage.TryGetState(handle, out VpIndexRangeState actualState, out int actualStart, out int actualCount), Is.True, "state query");
            Assert.That(
                new object[] { actualState, actualStart, actualCount },
                Is.EqualTo(new object[] { state, indexStart, indexCount }),
                "state, indexStart, indexCount");
        }

        private static VpIndexReadLease AcquireReading(VpCpuIndexStorage storage, VpIndexRangeHandle handle, params uint[] expected)
        {
            Assert.That(storage.TryAcquireReadLease(handle, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True, "acquire lease");
            Assert.That(view.ToArray(), Is.EqualTo(expected), "lease view");
            return lease;
        }

        private static void AssertLeaseReads(VpCpuIndexStorage storage, VpIndexReadLease lease, params uint[] expected)
        {
            Assert.That(storage.TryGetReadView(lease, out NativeArray<uint>.ReadOnly view), Is.True, "read view");
            Assert.That(view.ToArray(), Is.EqualTo(expected), "read view");
        }

        private static void ReadThroughLease(VpCpuIndexStorage storage, VpIndexRangeHandle handle, params uint[] expected)
        {
            VpIndexReadLease lease = AcquireReading(storage, handle, expected);
            Assert.That(storage.TryReleaseReadLease(lease), Is.True, "release lease");
        }

        private static void AssertReserveFails(VpCpuIndexStorage storage, int indexCount)
        {
            Assert.That(storage.TryReserve(indexCount, out VpIndexRangeHandle rejected), Is.False, "reserve " + indexCount + " fails");
            Assert.That(rejected, Is.EqualTo(default(VpIndexRangeHandle)));
        }

        private static void AssertRejected(VpCpuIndexStorage storage, VpIndexRangeHandle stale, string label)
        {
            Assert.That(storage.TryGetState(stale, out _, out _, out _), Is.False, label + " state");
            Assert.That(storage.TryGetReservedWriteView(stale, out _), Is.False, label + " write view");
            Assert.That(storage.TryPublish(stale), Is.False, label + " publish");
            Assert.That(storage.TryPublish(stale, 0), Is.False, label + " partial publish");
            Assert.That(storage.TryCancelReservation(stale), Is.False, label + " cancel");
            Assert.That(storage.TryAcquireReadLease(stale, out _, out _), Is.False, label + " acquire");
            Assert.That(storage.TryRetire(stale), Is.False, label + " retire");
        }

        [Test]
        public void ASmallStorage_ReusesIndexSpaceOnlyWhenNoLeaseOrReservationHoldsIt()
        {
            using (var storage = new VpCpuIndexStorage(Capacity, 4, Allocator.Persistent))
            {
                // 1. Reserve 8 indices and publish only the 4 written from its start.
                VpIndexRangeHandle partial = Reserve(storage, 8, 0);
                Write(storage, partial, 11, 12, 13, 14);
                Assert.That(storage.TryPublish(partial, 4), Is.True, "partial publish");
                AssertState(storage, partial, VpIndexRangeState.Published, 0, 4);

                // 2. Two leases read the published prefix.
                VpIndexReadLease first = AcquireReading(storage, partial, 11, 12, 13, 14);
                VpIndexReadLease second = AcquireReading(storage, partial, 11, 12, 13, 14);

                // 3. Retired while leased, its 4 indices are not reused: a 4-index reservation lands after them.
                Assert.That(storage.TryRetire(partial), Is.True, "retire partial");
                AssertState(storage, partial, VpIndexRangeState.Retiring, 0, 4);
                VpIndexRangeHandle probe = Reserve(storage, 4, 4);
                Assert.That(storage.TryCancelReservation(probe), Is.True, "cancel probe");

                // 4. The other 8 indices become a separate published range.
                VpIndexRangeHandle rest = Reserve(storage, 8, 4);
                Write(storage, rest, 21, 22, 23, 24, 25, 26, 27, 28);
                Assert.That(storage.TryPublish(rest), Is.True, "publish rest");
                ReadThroughLease(storage, rest, 21, 22, 23, 24, 25, 26, 27, 28);

                // 5. With no index free, a reservation fails and changes nothing.
                AssertReserveFails(storage, 1);
                AssertState(storage, partial, VpIndexRangeState.Retiring, 0, 4);
                AssertState(storage, rest, VpIndexRangeState.Published, 4, 8);
                AssertLeaseReads(storage, first, 11, 12, 13, 14);
                AssertLeaseReads(storage, second, 11, 12, 13, 14);

                // 6. Returning one lease keeps the range Retiring and its space held.
                Assert.That(storage.TryReleaseReadLease(first), Is.True, "release first");
                AssertState(storage, partial, VpIndexRangeState.Retiring, 0, 4);
                AssertReserveFails(storage, 1);
                AssertLeaseReads(storage, second, 11, 12, 13, 14);

                // 7. Returning the last lease frees the range, and only then are its 4 indices reused.
                Assert.That(storage.TryReleaseReadLease(second), Is.True, "release second");
                AssertState(storage, partial, VpIndexRangeState.Free, 0, 4);
                VpIndexRangeHandle reused = Reserve(storage, 4, 0);
                Write(storage, reused, 31, 32, 33, 34);
                Assert.That(storage.TryPublish(reused), Is.True, "publish reused");
                ReadThroughLease(storage, reused, 31, 32, 33, 34);
                ReadThroughLease(storage, rest, 21, 22, 23, 24, 25, 26, 27, 28);

                // 8. Retiring both neighbours merges their space: all 12 indices fit only after the second retires.
                Assert.That(storage.TryRetire(reused), Is.True, "retire reused");
                AssertState(storage, reused, VpIndexRangeState.Free, 0, 4);
                AssertReserveFails(storage, Capacity);
                Assert.That(storage.TryRetire(rest), Is.True, "retire rest");
                AssertState(storage, rest, VpIndexRangeState.Free, 4, 8);

                // 9. All 12 indices are reserved again; the old contents stay until overwritten.
                VpIndexRangeHandle whole = Reserve(storage, Capacity, 0);
                Assert.That(storage.TryGetReservedWriteView(whole, out NativeArray<uint> wholeView), Is.True, "whole write view");
                Assert.That(wholeView.ToArray(), Is.EqualTo(new uint[] { 31, 32, 33, 34, 21, 22, 23, 24, 25, 26, 27, 28 }), "old contents");
                Write(storage, whole, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111);
                Assert.That(storage.TryPublish(whole), Is.True, "publish whole");
                ReadThroughLease(storage, whole, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111);

                // 10. Cancelled space is reused.
                Assert.That(storage.TryRetire(whole), Is.True, "retire whole");
                VpIndexRangeHandle cancelled = Reserve(storage, 6, 0);
                Assert.That(storage.TryCancelReservation(cancelled), Is.True, "cancel");
                AssertState(storage, cancelled, VpIndexRangeState.Free, 0, 6);
                VpIndexRangeHandle current = Reserve(storage, 6, 0);

                // 11. Handles and leases of earlier registrations of the reused descriptors are rejected.
                AssertRejected(storage, partial, "partial");
                AssertRejected(storage, cancelled, "cancelled");
                foreach (VpIndexReadLease stale in new[] { first, second })
                {
                    Assert.That(storage.TryGetReadView(stale, out _), Is.False, "stale lease view");
                    Assert.That(storage.TryReleaseReadLease(stale), Is.False, "stale lease release");
                }

                AssertState(storage, current, VpIndexRangeState.Reserved, 0, 6);
            }
        }
    }
}
