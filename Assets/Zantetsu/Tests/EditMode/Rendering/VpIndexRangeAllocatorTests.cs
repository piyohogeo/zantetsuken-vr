using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Index range allocator (DESIGN 4.5.3): address-ordered first-fit reservation and splitting, reuse after
    /// cancellation and retirement (held back while leases remain), merging with free neighbours, partial publishes
    /// freeing the unused tail, empty ranges without index space, and failures that change neither the free ranges
    /// nor the lifecycle table: fragmentation, too few or used-up descriptors, stale, foreign and default tokens.
    /// </summary>
    public class VpIndexRangeAllocatorTests
    {
        private static void AssertFree(VpIndexRangeAllocator allocator, params (int start, int count)[] expected)
        {
            Assert.That(allocator.CopyFreeRanges(), Is.EqualTo(expected), "free ranges");
        }

        private static void AssertRange(
            VpIndexRangeAllocator allocator,
            VpIndexRangeHandle handle,
            VpIndexRangeState state,
            int indexStart,
            int indexCount,
            int readers,
            string label = "")
        {
            Assert.That(allocator.TryGetState(handle, out VpIndexRangeState actualState, out int actualStart, out int actualCount), Is.True, label + " state query");
            Assert.That(allocator.TryGetReaderCount(handle, out int actualReaders), Is.True, label + " reader query");
            Assert.That(
                new object[] { actualState, actualStart, actualCount, actualReaders },
                Is.EqualTo(new object[] { state, indexStart, indexCount, readers }),
                label + " state, indexStart, indexCount, readers");
        }

        private static VpIndexRangeHandle Reserve(VpIndexRangeAllocator allocator, int indexCount)
        {
            Assert.That(allocator.TryReserve(indexCount, out VpIndexRangeHandle handle), Is.True, "reserve " + indexCount);
            return handle;
        }

        private static VpIndexRangeHandle ReservePublished(VpIndexRangeAllocator allocator, int indexCount)
        {
            VpIndexRangeHandle handle = Reserve(allocator, indexCount);
            Assert.That(allocator.TryPublish(handle), Is.True, "publish");
            return handle;
        }

        private static VpIndexReadLease Lease(VpIndexRangeAllocator allocator, VpIndexRangeHandle handle)
        {
            Assert.That(allocator.TryAcquireReadLease(handle, out VpIndexReadLease lease), Is.True, "acquire lease");
            return lease;
        }

        /// <summary>[0, 10), [10, 20) and [20, 30) reserved in a 40-index allocator, leaving [30, 40) free.</summary>
        private static VpIndexRangeHandle[] ThreeReservations(out VpIndexRangeAllocator allocator)
        {
            allocator = new VpIndexRangeAllocator(40, 4);
            VpIndexRangeHandle[] handles = { Reserve(allocator, 10), Reserve(allocator, 10), Reserve(allocator, 10) };
            AssertFree(allocator, (30, 10));
            return handles;
        }

        [Test]
        public void TheConstructor_StartsWithTheWholeCapacityFree()
        {
            var allocator = new VpIndexRangeAllocator(100, 4);

            Assert.That(allocator.IndexCapacity, Is.EqualTo(100));
            Assert.That(allocator.DescriptorCapacity, Is.EqualTo(4));
            AssertFree(allocator, (0, 100));
            AssertFree(new VpIndexRangeAllocator(0, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndexRangeAllocator(-1, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndexRangeAllocator(100, -1));
        }

        [Test]
        public void Reserving_TakesTheStartOfTheFreeRangeAndLeavesTheRestFree()
        {
            var allocator = new VpIndexRangeAllocator(100, 4);

            VpIndexRangeHandle first = Reserve(allocator, 10);
            AssertRange(allocator, first, VpIndexRangeState.Reserved, 0, 10, 0);
            AssertFree(allocator, (10, 90));

            VpIndexRangeHandle second = Reserve(allocator, 90);
            AssertRange(allocator, second, VpIndexRangeState.Reserved, 10, 90, 0);
            AssertFree(allocator);
        }

        [Test]
        public void Reserving_UsesTheLowestFreeRangeThatFits()
        {
            var allocator = new VpIndexRangeAllocator(40, 8);
            VpIndexRangeHandle a = Reserve(allocator, 10);
            Reserve(allocator, 5);
            VpIndexRangeHandle c = Reserve(allocator, 20);
            Reserve(allocator, 5);
            Assert.That(allocator.TryCancelReservation(a), Is.True);
            Assert.That(allocator.TryCancelReservation(c), Is.True);
            AssertFree(allocator, (0, 10), (15, 20));

            AssertRange(allocator, Reserve(allocator, 15), VpIndexRangeState.Reserved, 15, 15, 0);
            AssertFree(allocator, (0, 10), (30, 5));

            AssertRange(allocator, Reserve(allocator, 8), VpIndexRangeState.Reserved, 0, 8, 0);
            AssertFree(allocator, (8, 2), (30, 5));

            AssertRange(allocator, Reserve(allocator, 5), VpIndexRangeState.Reserved, 30, 5, 0);
            AssertFree(allocator, (8, 2));
        }

        [Test]
        public void ACancelledReservation_IsReused()
        {
            var allocator = new VpIndexRangeAllocator(30, 4);
            VpIndexRangeHandle first = Reserve(allocator, 10);
            Reserve(allocator, 10);

            Assert.That(allocator.TryCancelReservation(first), Is.True);

            AssertRange(allocator, first, VpIndexRangeState.Free, 0, 10, 0);
            AssertFree(allocator, (0, 10), (20, 10));
            AssertRange(allocator, Reserve(allocator, 10), VpIndexRangeState.Reserved, 0, 10, 0);
            AssertFree(allocator, (20, 10));
        }

        [Test]
        public void FreeingNextToAFreeRangeOnTheLeft_MergesThem()
        {
            VpIndexRangeHandle[] handles = ThreeReservations(out VpIndexRangeAllocator allocator);
            Assert.That(allocator.TryCancelReservation(handles[0]), Is.True);
            AssertFree(allocator, (0, 10), (30, 10));

            Assert.That(allocator.TryCancelReservation(handles[1]), Is.True);

            AssertFree(allocator, (0, 20), (30, 10));
        }

        [Test]
        public void FreeingNextToAFreeRangeOnTheRight_MergesThem()
        {
            VpIndexRangeHandle[] handles = ThreeReservations(out VpIndexRangeAllocator allocator);
            Assert.That(allocator.TryCancelReservation(handles[1]), Is.True);
            AssertFree(allocator, (10, 10), (30, 10));

            Assert.That(allocator.TryCancelReservation(handles[0]), Is.True);

            AssertFree(allocator, (0, 20), (30, 10));
        }

        [Test]
        public void FreeingBetweenTwoFreeRanges_MergesAllThree()
        {
            VpIndexRangeHandle[] handles = ThreeReservations(out VpIndexRangeAllocator allocator);
            Assert.That(allocator.TryCancelReservation(handles[0]), Is.True);
            Assert.That(allocator.TryCancelReservation(handles[2]), Is.True);
            AssertFree(allocator, (0, 10), (20, 20));

            Assert.That(allocator.TryCancelReservation(handles[1]), Is.True);

            AssertFree(allocator, (0, 40));
        }

        [Test]
        public void APartialPublish_KeepsTheStartAndFreesTheUnusedTail()
        {
            var allocator = new VpIndexRangeAllocator(40, 4);
            VpIndexRangeHandle partial = Reserve(allocator, 20);
            VpIndexRangeHandle next = Reserve(allocator, 10);

            Assert.That(allocator.TryPublish(partial, 12), Is.True);

            AssertRange(allocator, partial, VpIndexRangeState.Published, 0, 12, 0);
            AssertFree(allocator, (12, 8), (30, 10));
            Assert.That(allocator.TryCancelReservation(next), Is.True);
            AssertFree(allocator, (12, 28));
            AssertRange(allocator, Reserve(allocator, 28), VpIndexRangeState.Reserved, 12, 28, 0);
        }

        [Test]
        public void APartialPublishTail_MergesWithTheFreeRangeAfterIt()
        {
            var allocator = new VpIndexRangeAllocator(30, 2);
            VpIndexRangeHandle handle = Reserve(allocator, 20);

            Assert.That(allocator.TryPublish(handle, 12), Is.True);

            AssertRange(allocator, handle, VpIndexRangeState.Published, 0, 12, 0);
            AssertFree(allocator, (12, 18));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void PublishingTheWholeReservation_FreesNothing(bool withCount)
        {
            var allocator = new VpIndexRangeAllocator(30, 2);
            VpIndexRangeHandle handle = Reserve(allocator, 20);

            Assert.That(withCount ? allocator.TryPublish(handle, 20) : allocator.TryPublish(handle), Is.True);

            AssertRange(allocator, handle, VpIndexRangeState.Published, 0, 20, 0);
            AssertFree(allocator, (20, 10));
        }

        [Test]
        public void PublishingNothing_FreesTheReservationAndLeavesAnEmptyRangeAt0()
        {
            var allocator = new VpIndexRangeAllocator(30, 2);
            Reserve(allocator, 5);
            VpIndexRangeHandle handle = Reserve(allocator, 20);

            Assert.That(allocator.TryPublish(handle, 0), Is.True);

            AssertRange(allocator, handle, VpIndexRangeState.Published, 0, 0, 0);
            AssertFree(allocator, (5, 25));
            VpIndexReadLease lease = Lease(allocator, handle);
            Assert.That(allocator.TryRetire(handle), Is.True);
            Assert.That(allocator.TryReleaseReadLease(lease), Is.True);
            AssertRange(allocator, handle, VpIndexRangeState.Free, 0, 0, 0);
            AssertFree(allocator, (5, 25));
        }

        [TestCase(-1)]
        [TestCase(21)]
        [TestCase(int.MaxValue)]
        public void PublishingOutsideTheReservation_ChangesNothing(int publishedCount)
        {
            var allocator = new VpIndexRangeAllocator(30, 2);
            VpIndexRangeHandle handle = Reserve(allocator, 20);

            Assert.That(allocator.TryPublish(handle, publishedCount), Is.False);

            AssertRange(allocator, handle, VpIndexRangeState.Reserved, 0, 20, 0);
            AssertFree(allocator, (20, 10));
        }

        [Test]
        public void APartialPublishOfAPublishedRange_ChangesNothing()
        {
            var allocator = new VpIndexRangeAllocator(30, 2);
            VpIndexRangeHandle handle = ReservePublished(allocator, 20);
            Lease(allocator, handle);

            Assert.That(allocator.TryPublish(handle, 10), Is.False);

            AssertRange(allocator, handle, VpIndexRangeState.Published, 0, 20, 1);
            AssertFree(allocator, (20, 10));
        }

        [Test]
        public void RetiringWithoutReaders_ReusesTheRangeAtOnce()
        {
            var allocator = new VpIndexRangeAllocator(20, 2);
            VpIndexRangeHandle handle = ReservePublished(allocator, 10);
            Reserve(allocator, 10);
            Assert.That(allocator.TryReleaseReadLease(Lease(allocator, handle)), Is.True);
            AssertFree(allocator);

            Assert.That(allocator.TryRetire(handle), Is.True);

            AssertRange(allocator, handle, VpIndexRangeState.Free, 0, 10, 0);
            AssertFree(allocator, (0, 10));
            AssertRange(allocator, Reserve(allocator, 10), VpIndexRangeState.Reserved, 0, 10, 0);
        }

        [Test]
        public void RetiringWithReaders_HoldsTheRangeUntilTheLastLeaseReturns()
        {
            var allocator = new VpIndexRangeAllocator(30, 4);
            VpIndexRangeHandle handle = ReservePublished(allocator, 10);
            Reserve(allocator, 10);
            VpIndexReadLease first = Lease(allocator, handle);
            VpIndexReadLease second = Lease(allocator, handle);

            Assert.That(allocator.TryRetire(handle), Is.True);

            AssertRange(allocator, handle, VpIndexRangeState.Retiring, 0, 10, 2);
            AssertFree(allocator, (20, 10));
            AssertRange(allocator, Reserve(allocator, 10), VpIndexRangeState.Reserved, 20, 10, 0);
            Assert.That(allocator.TryReserve(1, out _), Is.False, "the retiring range is not reused");

            Assert.That(allocator.TryReleaseReadLease(first), Is.True);
            AssertRange(allocator, handle, VpIndexRangeState.Retiring, 0, 10, 1);
            AssertFree(allocator);

            Assert.That(allocator.TryReleaseReadLease(second), Is.True);
            AssertRange(allocator, handle, VpIndexRangeState.Free, 0, 10, 0);
            AssertFree(allocator, (0, 10));
            AssertRange(allocator, Reserve(allocator, 10), VpIndexRangeState.Reserved, 0, 10, 0);
        }

        [Test]
        public void ReturningALeaseOfAPublishedRange_FreesNothing()
        {
            var allocator = new VpIndexRangeAllocator(30, 2);
            VpIndexRangeHandle handle = ReservePublished(allocator, 10);

            Assert.That(allocator.TryReleaseReadLease(Lease(allocator, handle)), Is.True);

            AssertRange(allocator, handle, VpIndexRangeState.Published, 0, 10, 0);
            AssertFree(allocator, (10, 20));
        }

        [Test]
        public void FragmentedFreeSpace_RejectsAReservationWithoutChangingAnything()
        {
            VpIndexRangeHandle[] handles = ThreeReservations(out VpIndexRangeAllocator allocator);
            Assert.That(allocator.TryPublish(handles[1]), Is.True);
            Lease(allocator, handles[1]);
            Assert.That(allocator.TryCancelReservation(handles[0]), Is.True);
            Assert.That(allocator.TryCancelReservation(handles[2]), Is.True);
            AssertFree(allocator, (0, 10), (20, 20));

            // 30 indices are free in total, but no 21 of them in a row.
            Assert.That(allocator.TryReserve(21, out VpIndexRangeHandle rejected), Is.False);

            Assert.That(rejected, Is.EqualTo(default(VpIndexRangeHandle)));
            AssertFree(allocator, (0, 10), (20, 20));
            AssertRange(allocator, handles[1], VpIndexRangeState.Published, 10, 10, 1);
            AssertRange(allocator, Reserve(allocator, 20), VpIndexRangeState.Reserved, 20, 20, 0);
        }

        [Test]
        public void TooFewDescriptors_RejectAReservationWithoutChangingAnything()
        {
            var allocator = new VpIndexRangeAllocator(100, 2);
            VpIndexRangeHandle published = ReservePublished(allocator, 10);
            Lease(allocator, published);
            VpIndexRangeHandle reserved = Reserve(allocator, 10);

            Assert.That(allocator.TryReserve(10, out VpIndexRangeHandle rejected), Is.False);
            Assert.That(allocator.TryReserve(0, out _), Is.False, "empty");

            Assert.That(rejected, Is.EqualTo(default(VpIndexRangeHandle)));
            AssertFree(allocator, (20, 80));
            AssertRange(allocator, published, VpIndexRangeState.Published, 0, 10, 1);
            AssertRange(allocator, reserved, VpIndexRangeState.Reserved, 10, 10, 0);
            Assert.That(allocator.TryCancelReservation(reserved), Is.True);
            AssertRange(allocator, Reserve(allocator, 10), VpIndexRangeState.Reserved, 10, 10, 0);
        }

        [Test]
        public void AUsedUpDescriptor_RejectsAReservationWithoutChangingAnything()
        {
            // One descriptor with one generation: it cannot be registered again after its first range is freed.
            var allocator = new VpIndexRangeAllocator(100, new VpIndexRangeLifecycleTable(1, 1, int.MaxValue, int.MaxValue));
            Assert.That(allocator.TryCancelReservation(Reserve(allocator, 10)), Is.True);
            AssertFree(allocator, (0, 100));

            Assert.That(allocator.TryReserve(10, out VpIndexRangeHandle rejected), Is.False);

            Assert.That(rejected, Is.EqualTo(default(VpIndexRangeHandle)));
            AssertFree(allocator, (0, 100));
        }

        [TestCase(-1)]
        [TestCase(101)]
        [TestCase(int.MaxValue)]
        public void NegativeOrTooLargeCounts_AreRejectedWithoutTakingADescriptor(int indexCount)
        {
            var allocator = new VpIndexRangeAllocator(100, 1);

            Assert.That(allocator.TryReserve(indexCount, out VpIndexRangeHandle rejected), Is.False);

            Assert.That(rejected, Is.EqualTo(default(VpIndexRangeHandle)));
            AssertFree(allocator, (0, 100));
            AssertRange(allocator, Reserve(allocator, 100), VpIndexRangeState.Reserved, 0, 100, 0);
        }

        [Test]
        public void StaleHandlesAndLeases_AreRejectedWithoutChangingAnything()
        {
            // One descriptor, so the new registration reuses both the descriptor and the space of the old one.
            var allocator = new VpIndexRangeAllocator(30, 1);
            VpIndexRangeHandle oldHandle = ReservePublished(allocator, 10);
            VpIndexReadLease oldLease = Lease(allocator, oldHandle);
            Assert.That(allocator.TryRetire(oldHandle), Is.True);
            Assert.That(allocator.TryReleaseReadLease(oldLease), Is.True);
            AssertFree(allocator, (0, 30));

            VpIndexRangeHandle newHandle = Reserve(allocator, 20);
            AssertRange(allocator, newHandle, VpIndexRangeState.Reserved, 0, 20, 0);

            Assert.That(allocator.TryGetState(oldHandle, out _, out _, out _), Is.False, "state");
            Assert.That(allocator.TryPublish(oldHandle), Is.False, "publish");
            Assert.That(allocator.TryPublish(oldHandle, 5), Is.False, "partial publish");
            Assert.That(allocator.TryCancelReservation(oldHandle), Is.False, "cancel");
            AssertRange(allocator, newHandle, VpIndexRangeState.Reserved, 0, 20, 0);
            AssertFree(allocator, (20, 10));

            Assert.That(allocator.TryPublish(newHandle, 5), Is.True);
            Lease(allocator, newHandle);
            Assert.That(allocator.TryAcquireReadLease(oldHandle, out _), Is.False, "acquire");
            Assert.That(allocator.TryRetire(oldHandle), Is.False, "retire");
            Assert.That(allocator.TryReleaseReadLease(oldLease), Is.False, "lease");
            AssertRange(allocator, newHandle, VpIndexRangeState.Published, 0, 5, 1);
            AssertFree(allocator, (5, 25));
        }

        [Test]
        public void ForeignAndDefaultTokens_AreRejectedWithoutChangingAnything()
        {
            var allocator = new VpIndexRangeAllocator(30, 1);
            var other = new VpIndexRangeAllocator(30, 1);
            VpIndexRangeHandle foreign = ReservePublished(other, 10);
            VpIndexReadLease foreignLease = Lease(other, foreign);
            VpIndexRangeHandle handle = Reserve(allocator, 10);

            foreach ((VpIndexRangeHandle token, VpIndexReadLease lease, string label) in new[]
                     {
                         (foreign, foreignLease, "foreign"),
                         (default(VpIndexRangeHandle), default(VpIndexReadLease), "default"),
                     })
            {
                Assert.That(allocator.TryGetState(token, out _, out _, out _), Is.False, label + " state");
                Assert.That(allocator.TryPublish(token), Is.False, label + " publish");
                Assert.That(allocator.TryPublish(token, 5), Is.False, label + " partial publish");
                Assert.That(allocator.TryCancelReservation(token), Is.False, label + " cancel");
                Assert.That(allocator.TryAcquireReadLease(token, out _), Is.False, label + " acquire");
                Assert.That(allocator.TryRetire(token), Is.False, label + " retire");
                Assert.That(allocator.TryReleaseReadLease(lease), Is.False, label + " lease");
            }

            AssertRange(allocator, handle, VpIndexRangeState.Reserved, 0, 10, 0);
            AssertFree(allocator, (10, 20));
            AssertRange(other, foreign, VpIndexRangeState.Published, 0, 10, 1);
            AssertFree(other, (10, 20));
        }

        [Test]
        public void EmptyRanges_TakeADescriptorButNoIndexSpace()
        {
            var allocator = new VpIndexRangeAllocator(10, 2);
            VpIndexRangeHandle full = Reserve(allocator, 10);
            AssertFree(allocator);

            VpIndexRangeHandle empty = Reserve(allocator, 0);

            AssertRange(allocator, empty, VpIndexRangeState.Reserved, 0, 0, 0);
            Assert.That(allocator.TryReserve(0, out _), Is.False, "both descriptors are taken");
            Assert.That(allocator.TryPublish(empty, 1), Is.False, "beyond the empty reservation");
            Assert.That(allocator.TryPublish(empty, 0), Is.True);
            VpIndexReadLease lease = Lease(allocator, empty);
            Assert.That(allocator.TryRetire(empty), Is.True);
            AssertRange(allocator, empty, VpIndexRangeState.Retiring, 0, 0, 1);
            Assert.That(allocator.TryReleaseReadLease(lease), Is.True);
            AssertRange(allocator, empty, VpIndexRangeState.Free, 0, 0, 0);
            AssertFree(allocator);

            Assert.That(allocator.TryCancelReservation(full), Is.True);
            AssertFree(allocator, (0, 10));
            VpIndexRangeHandle again = Reserve(allocator, 0);
            AssertRange(allocator, again, VpIndexRangeState.Reserved, 0, 0, 0);
            AssertFree(allocator, (0, 10));
            Assert.That(allocator.TryCancelReservation(again), Is.True);
            AssertFree(allocator, (0, 10));
        }

        [Test]
        public void ManyOperations_KeepTheFreeRangesExactlyTheSpaceNoLiveRangeUses()
        {
            const int Capacity = 64;
            const int Descriptors = 12;
            const int Steps = 5000;
            var allocator = new VpIndexRangeAllocator(Capacity, Descriptors);
            var random = new Random(20260915);
            var live = new List<VpIndexRangeHandle>();
            var leases = new List<VpIndexReadLease>();

            for (int step = 0; step < Steps; step++)
            {
                string label = "step " + step;
                int operation = random.Next(6);
                if (operation == 0)
                {
                    int count = random.Next(0, 24);
                    int expectedStart = count == 0 ? 0 : -1;
                    foreach ((int start, int freeCount) in allocator.CopyFreeRanges())
                    {
                        if (expectedStart < 0 && freeCount >= count)
                        {
                            expectedStart = start;
                        }
                    }

                    bool expected = expectedStart >= 0 && live.Count < Descriptors;
                    Assert.That(allocator.TryReserve(count, out VpIndexRangeHandle handle), Is.EqualTo(expected), label + " reserve");
                    if (expected)
                    {
                        live.Add(handle);
                        AssertRange(allocator, handle, VpIndexRangeState.Reserved, expectedStart, count, 0, label);
                    }
                }
                else if (operation == 5)
                {
                    if (leases.Count > 0)
                    {
                        int index = random.Next(leases.Count);
                        VpIndexReadLease lease = leases[index];
                        leases.RemoveAt(index);
                        Assert.That(allocator.TryReleaseReadLease(lease), Is.True, label + " release");
                        Assert.That(allocator.TryReleaseReadLease(lease), Is.False, label + " release again");
                        if (allocator.TryGetState(lease.range, out VpIndexRangeState state, out _, out _)
                            && state == VpIndexRangeState.Free)
                        {
                            live.Remove(lease.range);
                        }
                    }
                }
                else if (live.Count > 0)
                {
                    int index = random.Next(live.Count);
                    VpIndexRangeHandle handle = live[index];
                    Assert.That(allocator.TryGetState(handle, out VpIndexRangeState state, out _, out int count), Is.True, label);
                    Assert.That(allocator.TryGetReaderCount(handle, out int readers), Is.True, label);
                    switch (operation)
                    {
                        case 1:
                            int publishedCount = random.Next(0, count + 2);
                            Assert.That(
                                allocator.TryPublish(handle, publishedCount),
                                Is.EqualTo(state == VpIndexRangeState.Reserved && publishedCount <= count),
                                label + " publish");
                            break;
                        case 2:
                            bool cancelled = allocator.TryCancelReservation(handle);
                            Assert.That(cancelled, Is.EqualTo(state == VpIndexRangeState.Reserved), label + " cancel");
                            if (cancelled)
                            {
                                live.RemoveAt(index);
                            }

                            break;
                        case 3:
                            bool acquired = allocator.TryAcquireReadLease(handle, out VpIndexReadLease lease);
                            Assert.That(acquired, Is.EqualTo(state == VpIndexRangeState.Published), label + " acquire");
                            if (acquired)
                            {
                                leases.Add(lease);
                            }

                            break;
                        default:
                            bool retired = allocator.TryRetire(handle);
                            Assert.That(retired, Is.EqualTo(state == VpIndexRangeState.Published), label + " retire");
                            if (retired && readers == 0)
                            {
                                live.RemoveAt(index);
                            }

                            break;
                    }
                }

                AssertFreeRangesAreTheUnusedSpace(allocator, live, Capacity, label);
            }
        }

        private static void AssertFreeRangesAreTheUnusedSpace(
            VpIndexRangeAllocator allocator,
            List<VpIndexRangeHandle> live,
            int capacity,
            string label)
        {
            var used = new bool[capacity];
            foreach (VpIndexRangeHandle handle in live)
            {
                Assert.That(allocator.TryGetState(handle, out VpIndexRangeState state, out int start, out int count), Is.True, label + " live query");
                Assert.That(state, Is.Not.EqualTo(VpIndexRangeState.Free), label + " live state");
                if (count == 0)
                {
                    Assert.That(start, Is.Zero, label + " empty start");
                }

                for (int i = start; i < start + count; i++)
                {
                    if (used[i])
                    {
                        Assert.Fail(label + ": live ranges overlap at " + i);
                    }

                    used[i] = true;
                }
            }

            var expected = new List<(int start, int count)>();
            for (int i = 0; i < capacity;)
            {
                if (used[i])
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i < capacity && !used[i])
                {
                    i++;
                }

                expected.Add((start, i - start));
            }

            Assert.That(allocator.CopyFreeRanges(), Is.EqualTo(expected.ToArray()), label + " free ranges");
        }
    }
}
