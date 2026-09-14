using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Index range lifecycle and read leases (DESIGN 4.5.3): Free / Reserved / Published / Retiring transitions, many
    /// leases per range, retirement held until the last lease returns, and rejection without any change of default,
    /// foreign, stale and double-returned handles and leases, invalid transitions, invalid ranges and a full table.
    /// </summary>
    public class VpIndexRangeLifecycleTableTests
    {
        private static void AssertRange(
            VpIndexRangeLifecycleTable table,
            VpIndexRangeHandle handle,
            VpIndexRangeState state,
            int indexStart,
            int indexCount,
            int readers)
        {
            Assert.That(table.TryGetState(handle, out VpIndexRangeState actualState, out int actualStart, out int actualCount), Is.True, "state query");
            Assert.That(table.TryGetReaderCount(handle, out int actualReaders), Is.True, "reader query");
            Assert.That(
                new object[] { actualState, actualStart, actualCount, actualReaders },
                Is.EqualTo(new object[] { state, indexStart, indexCount, readers }),
                "state, indexStart, indexCount, readers");
        }

        private static void AssertRejected(VpIndexRangeLifecycleTable table, VpIndexRangeHandle handle)
        {
            Assert.That(table.TryGetState(handle, out VpIndexRangeState state, out int indexStart, out int indexCount), Is.False, "state query");
            Assert.That(new object[] { state, indexStart, indexCount }, Is.EqualTo(new object[] { VpIndexRangeState.Free, 0, 0 }));
            Assert.That(table.TryGetReaderCount(handle, out int readers), Is.False, "reader query");
            Assert.That(readers, Is.Zero);
        }

        private static VpIndexRangeHandle Reserved(VpIndexRangeLifecycleTable table, int indexStart, int indexCount)
        {
            Assert.That(table.TryReserve(indexStart, indexCount, out VpIndexRangeHandle handle), Is.True, "reserve");
            return handle;
        }

        private static VpIndexRangeHandle Published(VpIndexRangeLifecycleTable table, int indexStart, int indexCount)
        {
            VpIndexRangeHandle handle = Reserved(table, indexStart, indexCount);
            Assert.That(table.TryPublish(handle), Is.True, "publish");
            return handle;
        }

        private static VpIndexReadLease Lease(VpIndexRangeLifecycleTable table, VpIndexRangeHandle handle)
        {
            Assert.That(table.TryAcquireReadLease(handle, out VpIndexReadLease lease), Is.True, "acquire lease");
            return lease;
        }

        /// <summary>A range at [10, 30) brought into the state; a Retiring range keeps one reader.</summary>
        private static VpIndexRangeHandle InState(VpIndexRangeLifecycleTable table, VpIndexRangeState state)
        {
            VpIndexRangeHandle handle = Reserved(table, 10, 20);
            switch (state)
            {
                case VpIndexRangeState.Reserved:
                    break;
                case VpIndexRangeState.Published:
                    Assert.That(table.TryPublish(handle), Is.True);
                    break;
                case VpIndexRangeState.Retiring:
                    Assert.That(table.TryPublish(handle), Is.True);
                    Lease(table, handle);
                    Assert.That(table.TryRetire(handle), Is.True);
                    break;
                case VpIndexRangeState.Free:
                    Assert.That(table.TryCancelReservation(handle), Is.True);
                    break;
            }

            AssertRange(table, handle, state, 10, 20, state == VpIndexRangeState.Retiring ? 1 : 0);
            return handle;
        }

        [Test]
        public void TheConstructor_SetsTheCapacityAndRejectsANegativeOne()
        {
            Assert.That(new VpIndexRangeLifecycleTable(3).DescriptorCapacity, Is.EqualTo(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndexRangeLifecycleTable(-1));
        }

        [Test]
        public void Reserving_RegistersTheRangeAsReserved()
        {
            var table = new VpIndexRangeLifecycleTable(2);

            VpIndexRangeHandle handle = Reserved(table, 24, 36);

            Assert.That(handle, Is.Not.EqualTo(default(VpIndexRangeHandle)));
            AssertRange(table, handle, VpIndexRangeState.Reserved, 24, 36, 0);
        }

        [Test]
        public void AReservedRange_CanBePublished()
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Reserved(table, 0, 6);

            Assert.That(table.TryPublish(handle), Is.True);

            AssertRange(table, handle, VpIndexRangeState.Published, 0, 6, 0);
        }

        [Test]
        public void AReservedRange_RefusesReadLeases()
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Reserved(table, 0, 6);

            Assert.That(table.TryAcquireReadLease(handle, out VpIndexReadLease lease), Is.False);

            Assert.That(lease, Is.EqualTo(default(VpIndexReadLease)));
            AssertRange(table, handle, VpIndexRangeState.Reserved, 0, 6, 0);
        }

        [Test]
        public void APublishedRange_LendsManySeparateLeasesThatStayPublishedWhenReturned()
        {
            const int Readers = 1000;
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Published(table, 0, 6);

            var leases = new List<VpIndexReadLease>();
            for (int i = 0; i < Readers; i++)
            {
                leases.Add(Lease(table, handle));
            }

            Assert.That(new HashSet<VpIndexReadLease>(leases).Count, Is.EqualTo(Readers), "distinct leases");
            AssertRange(table, handle, VpIndexRangeState.Published, 0, 6, Readers);

            foreach (VpIndexReadLease lease in leases)
            {
                Assert.That(table.TryReleaseReadLease(lease), Is.True);
            }

            AssertRange(table, handle, VpIndexRangeState.Published, 0, 6, 0);
        }

        [Test]
        public void RetiringWithoutReaders_FreesTheRangeAtOnce()
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Published(table, 0, 6);
            Assert.That(table.TryReleaseReadLease(Lease(table, handle)), Is.True);

            Assert.That(table.TryRetire(handle), Is.True);

            AssertRange(table, handle, VpIndexRangeState.Free, 0, 6, 0);
        }

        [Test]
        public void RetiringWithReaders_KeepsTheRangeRetiring()
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Published(table, 0, 6);
            Lease(table, handle);

            Assert.That(table.TryRetire(handle), Is.True);

            AssertRange(table, handle, VpIndexRangeState.Retiring, 0, 6, 1);
        }

        [Test]
        public void ARetiringRange_RefusesNewLeases()
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = InState(table, VpIndexRangeState.Retiring);

            Assert.That(table.TryAcquireReadLease(handle, out VpIndexReadLease lease), Is.False);

            Assert.That(lease, Is.EqualTo(default(VpIndexReadLease)));
            AssertRange(table, handle, VpIndexRangeState.Retiring, 10, 20, 1);
        }

        [Test]
        public void ReturningOneOfSeveralLeases_KeepsTheRangeRetiring()
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Published(table, 0, 6);
            VpIndexReadLease first = Lease(table, handle);
            VpIndexReadLease second = Lease(table, handle);
            Lease(table, handle);
            Assert.That(table.TryRetire(handle), Is.True);

            Assert.That(table.TryReleaseReadLease(second), Is.True);
            AssertRange(table, handle, VpIndexRangeState.Retiring, 0, 6, 2);

            Assert.That(table.TryReleaseReadLease(first), Is.True);
            AssertRange(table, handle, VpIndexRangeState.Retiring, 0, 6, 1);
        }

        [Test]
        public void ReturningTheLastLease_FreesTheRange()
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Published(table, 0, 6);
            VpIndexReadLease first = Lease(table, handle);
            VpIndexReadLease second = Lease(table, handle);
            Assert.That(table.TryRetire(handle), Is.True);
            Assert.That(table.TryReleaseReadLease(first), Is.True);

            Assert.That(table.TryReleaseReadLease(second), Is.True);

            AssertRange(table, handle, VpIndexRangeState.Free, 0, 6, 0);
        }

        [Test]
        public void CancellingAReservation_FreesTheRange()
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Reserved(table, 0, 6);

            Assert.That(table.TryCancelReservation(handle), Is.True);

            AssertRange(table, handle, VpIndexRangeState.Free, 0, 6, 0);
        }

        [Test]
        public void ReturningALeaseTwice_IsRejectedWithoutChangingTheRange()
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Published(table, 0, 6);
            VpIndexReadLease first = Lease(table, handle);
            VpIndexReadLease second = Lease(table, handle);
            Assert.That(table.TryReleaseReadLease(first), Is.True);

            Assert.That(table.TryReleaseReadLease(first), Is.False, "published");
            AssertRange(table, handle, VpIndexRangeState.Published, 0, 6, 1);

            Assert.That(table.TryRetire(handle), Is.True);
            Assert.That(table.TryReleaseReadLease(first), Is.False, "retiring");
            AssertRange(table, handle, VpIndexRangeState.Retiring, 0, 6, 1);

            Assert.That(table.TryReleaseReadLease(second), Is.True);
            Assert.That(table.TryReleaseReadLease(second), Is.False, "free");
            AssertRange(table, handle, VpIndexRangeState.Free, 0, 6, 0);
        }

        [Test]
        public void AfterTheDescriptorIsRegisteredAgain_TheOldHandleAndLeasesAreRejected()
        {
            // One descriptor, so the new registration reuses it and the new leases reuse the old lease slots.
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle oldHandle = Published(table, 0, 6);
            VpIndexReadLease oldFirst = Lease(table, oldHandle);
            VpIndexReadLease oldSecond = Lease(table, oldHandle);
            Assert.That(table.TryRetire(oldHandle), Is.True);
            Assert.That(table.TryReleaseReadLease(oldFirst), Is.True);
            Assert.That(table.TryReleaseReadLease(oldSecond), Is.True);
            AssertRange(table, oldHandle, VpIndexRangeState.Free, 0, 6, 0);

            VpIndexRangeHandle newHandle = Reserved(table, 6, 12);
            Assert.That(newHandle, Is.Not.EqualTo(oldHandle));
            AssertRejected(table, oldHandle);
            Assert.That(table.TryPublish(oldHandle), Is.False, "publish");
            Assert.That(table.TryCancelReservation(oldHandle), Is.False, "cancel");
            AssertRange(table, newHandle, VpIndexRangeState.Reserved, 6, 12, 0);

            Assert.That(table.TryPublish(newHandle), Is.True);
            VpIndexReadLease newFirst = Lease(table, newHandle);
            Lease(table, newHandle);
            Assert.That(table.TryAcquireReadLease(oldHandle, out VpIndexReadLease rejected), Is.False, "acquire");
            Assert.That(rejected, Is.EqualTo(default(VpIndexReadLease)));
            Assert.That(table.TryRetire(oldHandle), Is.False, "retire");
            Assert.That(table.TryReleaseReadLease(oldFirst), Is.False, "old first lease");
            Assert.That(table.TryReleaseReadLease(oldSecond), Is.False, "old second lease");
            AssertRange(table, newHandle, VpIndexRangeState.Published, 6, 12, 2);

            Assert.That(table.TryReleaseReadLease(newFirst), Is.True, "new lease");
        }

        [Test]
        public void HandlesAndLeasesOfAnotherTable_AreRejected()
        {
            // Both tables register their first descriptor once, so only the table tells the handles and leases apart.
            var table = new VpIndexRangeLifecycleTable(1);
            var other = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Published(table, 0, 6);
            VpIndexReadLease lease = Lease(table, handle);
            VpIndexRangeHandle otherHandle = Reserved(other, 0, 6);

            AssertRejected(other, handle);
            Assert.That(other.TryPublish(handle), Is.False, "publish");
            Assert.That(other.TryCancelReservation(handle), Is.False, "cancel");
            AssertRange(other, otherHandle, VpIndexRangeState.Reserved, 0, 6, 0);

            Assert.That(other.TryPublish(otherHandle), Is.True);
            Lease(other, otherHandle);
            Assert.That(other.TryAcquireReadLease(handle, out _), Is.False, "acquire");
            Assert.That(other.TryRetire(handle), Is.False, "retire");
            Assert.That(other.TryReleaseReadLease(lease), Is.False, "lease");
            AssertRange(other, otherHandle, VpIndexRangeState.Published, 0, 6, 1);
            AssertRange(table, handle, VpIndexRangeState.Published, 0, 6, 1);
        }

        [Test]
        public void TheDefaultHandleAndLease_AreRejected()
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = Published(table, 0, 6);
            Lease(table, handle);

            AssertRejected(table, default);
            Assert.That(table.TryPublish(default), Is.False, "publish");
            Assert.That(table.TryCancelReservation(default), Is.False, "cancel");
            Assert.That(table.TryAcquireReadLease(default, out _), Is.False, "acquire");
            Assert.That(table.TryRetire(default), Is.False, "retire");
            Assert.That(table.TryReleaseReadLease(default), Is.False, "lease");

            AssertRange(table, handle, VpIndexRangeState.Published, 0, 6, 1);
        }

        [TestCase("publish", VpIndexRangeState.Published)]
        [TestCase("publish", VpIndexRangeState.Retiring)]
        [TestCase("publish", VpIndexRangeState.Free)]
        [TestCase("cancel", VpIndexRangeState.Published)]
        [TestCase("cancel", VpIndexRangeState.Retiring)]
        [TestCase("cancel", VpIndexRangeState.Free)]
        [TestCase("retire", VpIndexRangeState.Reserved)]
        [TestCase("retire", VpIndexRangeState.Retiring)]
        [TestCase("retire", VpIndexRangeState.Free)]
        [TestCase("acquire", VpIndexRangeState.Reserved)]
        [TestCase("acquire", VpIndexRangeState.Retiring)]
        [TestCase("acquire", VpIndexRangeState.Free)]
        public void InvalidTransitions_AreRejectedWithoutChangingTheRange(string operation, VpIndexRangeState state)
        {
            var table = new VpIndexRangeLifecycleTable(1);
            VpIndexRangeHandle handle = InState(table, state);

            bool accepted;
            switch (operation)
            {
                case "publish":
                    accepted = table.TryPublish(handle);
                    break;
                case "cancel":
                    accepted = table.TryCancelReservation(handle);
                    break;
                case "retire":
                    accepted = table.TryRetire(handle);
                    break;
                default:
                    accepted = table.TryAcquireReadLease(handle, out _);
                    break;
            }

            Assert.That(accepted, Is.False);
            AssertRange(table, handle, state, 10, 20, state == VpIndexRangeState.Retiring ? 1 : 0);
        }

        [TestCase(-1, 6)]
        [TestCase(0, -1)]
        [TestCase(int.MinValue, 0)]
        [TestCase(int.MaxValue, 1)]
        [TestCase(1, int.MaxValue)]
        [TestCase(int.MaxValue - 5, 6)]
        [TestCase(int.MaxValue, int.MaxValue)]
        public void InvalidOrOverflowingRanges_AreRejectedWithoutTakingADescriptor(int indexStart, int indexCount)
        {
            var table = new VpIndexRangeLifecycleTable(1);

            Assert.That(table.TryReserve(indexStart, indexCount, out VpIndexRangeHandle handle), Is.False);

            Assert.That(handle, Is.EqualTo(default(VpIndexRangeHandle)));
            Assert.That(table.TryReserve(0, 6, out _), Is.True, "the descriptor is still free");
        }

        [TestCase(0, 0)]
        [TestCase(12, 0)]
        [TestCase(int.MaxValue, 0)]
        [TestCase(0, int.MaxValue)]
        [TestCase(int.MaxValue - 6, 6)]
        public void RangesEndingWithinInt_IncludingEmptyOnes_GoThroughTheLifecycle(int indexStart, int indexCount)
        {
            var table = new VpIndexRangeLifecycleTable(1);

            VpIndexRangeHandle handle = Published(table, indexStart, indexCount);
            VpIndexReadLease lease = Lease(table, handle);
            Assert.That(table.TryRetire(handle), Is.True);
            AssertRange(table, handle, VpIndexRangeState.Retiring, indexStart, indexCount, 1);

            Assert.That(table.TryReleaseReadLease(lease), Is.True);
            AssertRange(table, handle, VpIndexRangeState.Free, indexStart, indexCount, 0);
        }

        [Test]
        public void AFullTable_RejectsRegistrationWithoutChangingItsRanges()
        {
            var table = new VpIndexRangeLifecycleTable(2);
            VpIndexRangeHandle reserved = Reserved(table, 0, 6);
            VpIndexRangeHandle published = Published(table, 6, 36);
            Lease(table, published);

            Assert.That(table.TryReserve(42, 3, out VpIndexRangeHandle rejected), Is.False);

            Assert.That(rejected, Is.EqualTo(default(VpIndexRangeHandle)));
            AssertRange(table, reserved, VpIndexRangeState.Reserved, 0, 6, 0);
            AssertRange(table, published, VpIndexRangeState.Published, 6, 36, 1);

            Assert.That(table.TryCancelReservation(reserved), Is.True);
            AssertRange(table, Reserved(table, 42, 3), VpIndexRangeState.Reserved, 42, 3, 0);
        }

        [Test]
        public void ADescriptorAtTheLastGeneration_IsNotRegisteredAgainAndItsOldHandlesStayRejected()
        {
            // Generations 1 and 2 only: the first descriptor is used up after two registrations.
            var table = new VpIndexRangeLifecycleTable(2, 2, int.MaxValue, int.MaxValue);
            VpIndexRangeHandle first = Reserved(table, 0, 6);
            Assert.That(table.TryCancelReservation(first), Is.True);
            VpIndexRangeHandle last = Published(table, 6, 6);
            Assert.That(last.descriptor, Is.EqualTo(first.descriptor), "the second registration reuses the descriptor");
            Assert.That(table.TryRetire(last), Is.True);

            VpIndexRangeHandle other = Reserved(table, 12, 6);

            Assert.That(other.descriptor, Is.Not.EqualTo(last.descriptor), "the used-up descriptor is skipped");
            Assert.That(table.TryReserve(18, 6, out VpIndexRangeHandle rejected), Is.False, "no usable descriptor is free");
            Assert.That(rejected, Is.EqualTo(default(VpIndexRangeHandle)));
            AssertRejected(table, first);
            AssertRange(table, last, VpIndexRangeState.Free, 6, 6, 0);
            Assert.That(table.TryPublish(last), Is.False);
            Assert.That(table.TryAcquireReadLease(last, out _), Is.False);
            AssertRange(table, other, VpIndexRangeState.Reserved, 12, 6, 0);
        }

        [Test]
        public void ALeaseSlotAtTheLastGeneration_IsNotReusedAndItsOldLeasesStayRejected()
        {
            // Generations 1 and 2 only: the first lease slot is used up after two loans.
            var table = new VpIndexRangeLifecycleTable(1, 2, int.MaxValue, int.MaxValue);
            VpIndexRangeHandle handle = Published(table, 0, 6);
            VpIndexReadLease first = Lease(table, handle);
            Assert.That(table.TryReleaseReadLease(first), Is.True);
            VpIndexReadLease last = Lease(table, handle);
            Assert.That(last.slot, Is.EqualTo(first.slot), "the second loan reuses the slot");
            Assert.That(table.TryReleaseReadLease(last), Is.True);

            VpIndexReadLease next = Lease(table, handle);

            Assert.That(next.slot, Is.Not.EqualTo(last.slot), "the used-up slot is not reused");
            Assert.That(table.TryReleaseReadLease(first), Is.False, "first");
            Assert.That(table.TryReleaseReadLease(last), Is.False, "last");
            AssertRange(table, handle, VpIndexRangeState.Published, 0, 6, 1);
            Assert.That(table.TryReleaseReadLease(next), Is.True, "next");
        }

        [Test]
        public void ARangeAtTheReaderLimit_RefusesLeasesWithoutTakingASlot()
        {
            // One reader per range and two lease slots: a refused loan that took the second slot would leave none for
            // the other range.
            var table = new VpIndexRangeLifecycleTable(2, uint.MaxValue, 1, 2);
            VpIndexRangeHandle handle = Published(table, 0, 6);
            VpIndexRangeHandle other = Published(table, 6, 6);
            VpIndexReadLease first = Lease(table, handle);

            Assert.That(table.TryAcquireReadLease(handle, out VpIndexReadLease refused), Is.False);

            Assert.That(refused, Is.EqualTo(default(VpIndexReadLease)));
            AssertRange(table, handle, VpIndexRangeState.Published, 0, 6, 1);
            Lease(table, other);
            AssertRange(table, other, VpIndexRangeState.Published, 6, 6, 1);
            Assert.That(table.TryReleaseReadLease(first), Is.True);
            Lease(table, handle);
            AssertRange(table, handle, VpIndexRangeState.Published, 0, 6, 1);
        }

        [Test]
        public void UsedUpLeaseSlots_RefuseLeasesWithoutChangingTheRange()
        {
            // One lease slot with one generation: after its only loan returns, no slot is left.
            var table = new VpIndexRangeLifecycleTable(1, 1, int.MaxValue, 1);
            VpIndexRangeHandle handle = Published(table, 0, 6);
            VpIndexReadLease only = Lease(table, handle);
            Assert.That(table.TryReleaseReadLease(only), Is.True);

            Assert.That(table.TryAcquireReadLease(handle, out VpIndexReadLease refused), Is.False);

            Assert.That(refused, Is.EqualTo(default(VpIndexReadLease)));
            Assert.That(table.TryReleaseReadLease(only), Is.False, "the old lease");
            AssertRange(table, handle, VpIndexRangeState.Published, 0, 6, 0);
            Assert.That(table.TryRetire(handle), Is.True);
            AssertRange(table, handle, VpIndexRangeState.Free, 0, 6, 0);
        }

        [Test]
        public void TableIds_StopAtTheLastIdInsteadOfWrapping()
        {
            int lastTableId = 0;
            Assert.That(VpIndexRangeLifecycleTable.NextTableId(ref lastTableId), Is.EqualTo(1));

            lastTableId = int.MaxValue - 1;
            Assert.That(VpIndexRangeLifecycleTable.NextTableId(ref lastTableId), Is.EqualTo(int.MaxValue));

            Assert.Throws<InvalidOperationException>(() => VpIndexRangeLifecycleTable.NextTableId(ref lastTableId));
            Assert.That(lastTableId, Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void ATableWithoutDescriptors_RejectsRegistration()
        {
            var table = new VpIndexRangeLifecycleTable(0);

            Assert.That(table.TryReserve(0, 6, out VpIndexRangeHandle handle), Is.False);

            Assert.That(handle, Is.EqualTo(default(VpIndexRangeHandle)));
        }
    }
}
