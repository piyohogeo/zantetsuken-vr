using System;
using System.Collections.Generic;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the ordinary round-robin drain: how many records one
    /// drain takes, in what order they come out, and where the next drain
    /// starts.
    /// </summary>
    /// <remarks>
    /// The publication order between one producer and one consumer belongs to
    /// the lane itself and is not re-tested here. These cases use a few small
    /// lanes and short payloads, and look only at order and at the limit.
    /// </remarks>
    public unsafe class TraceLaneDrainContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;
        private const TraceEventType KindB = TraceEventType.TaskStarted;
        private const TraceEventType KindC = TraceEventType.TaskCompleted;

        private const int MaxPayloadLength = 4;

        // The history is not this fixture's subject; these are the smallest
        // valid settings that carry a record of MaxPayloadLength.
        private const int HistoryPageSize = 64;
        private const int HistoryPageCount = 2;

        // -------------------------------------------------------------------
        // How much one drain takes
        // -------------------------------------------------------------------

        [Test]
        public void OneDrainTakesNoMoreThanTheProfileAllows()
        {
            using (TraceLaneSet set = Set(3, Lane(KindA, 64, 8), Lane(KindB, 64, 8)))
            {
                TraceLaneWriter first = set.CreateWriter(0);
                TraceLaneWriter second = set.CreateWriter(1);
                for (int record = 0; record < 4; record++)
                {
                    Assert.That(Write(first, KindA, (byte)record), Is.True);
                    Assert.That(Write(second, KindB, (byte)record), Is.True);
                }

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();

                Assert.That(drainer.Drain(destination), Is.EqualTo(3));
                Assert.That(destination.Count, Is.EqualTo(3));
            }
        }

        [Test]
        public void RepeatedDrainsReachEveryRecord()
        {
            const int PerLane = 5;

            using (TraceLaneSet set = Set(2, Lane(KindA, 64, 8), Lane(KindB, 64, 8)))
            {
                TraceLaneWriter first = set.CreateWriter(0);
                TraceLaneWriter second = set.CreateWriter(1);
                for (int record = 0; record < PerLane; record++)
                {
                    Assert.That(Write(first, KindA, (byte)record), Is.True);
                    Assert.That(Write(second, KindB, (byte)record), Is.True);
                }

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();

                int drains = 0;
                int drained;
                do
                {
                    drained = drainer.Drain(destination);
                    drains++;
                    Assert.That(drains, Is.LessThan(20), "the drains were not converging");
                }
                while (drained > 0);

                Assert.That(destination.Count, Is.EqualTo(PerLane * 2));
                Assert.That(
                    drainer.Drain(destination), Is.EqualTo(0),
                    "there should be nothing left to take");
            }
        }

        // -------------------------------------------------------------------
        // What order they come out in
        // -------------------------------------------------------------------

        [Test]
        public void RecordsComeOutInLaneOrder_WithEachLaneKeepingItsOwn()
        {
            // Three records in lane 0, two in lane 1, one in lane 2. Going
            // round in ordinal order and skipping the lanes that have run out
            // gives A B C, A B, A.
            using (TraceLaneSet set = Set(16, Lane(KindA, 64, 8), Lane(KindB, 64, 8), Lane(KindC, 64, 8)))
            {
                TraceLaneWriter first = set.CreateWriter(0);
                TraceLaneWriter second = set.CreateWriter(1);
                TraceLaneWriter third = set.CreateWriter(2);

                Assert.That(Write(first, KindA, 1), Is.True);
                Assert.That(Write(first, KindA, 2), Is.True);
                Assert.That(Write(first, KindA, 3), Is.True);
                Assert.That(Write(second, KindB, 11), Is.True);
                Assert.That(Write(second, KindB, 12), Is.True);
                Assert.That(Write(third, KindC, 21), Is.True);

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();

                Assert.That(drainer.Drain(destination), Is.EqualTo(6));

                destination.AssertRecord(0, KindA, 1);
                destination.AssertRecord(1, KindB, 11);
                destination.AssertRecord(2, KindC, 21);
                destination.AssertRecord(3, KindA, 2);
                destination.AssertRecord(4, KindB, 12);
                destination.AssertRecord(5, KindA, 3);
            }
        }

        [Test]
        public void TheNextDrainResumesAfterTheLaneItLastTookFrom()
        {
            // One record per drain, so a drain that always began at lane 0
            // would take lane 0 every time and never reach lane 1 while lane 0
            // still has anything.
            using (TraceLaneSet set = Set(1, Lane(KindA, 64, 8), Lane(KindB, 64, 8)))
            {
                TraceLaneWriter first = set.CreateWriter(0);
                TraceLaneWriter second = set.CreateWriter(1);

                for (int record = 0; record < 4; record++)
                {
                    Assert.That(Write(first, KindA, (byte)record), Is.True);
                }

                Assert.That(Write(second, KindB, 100), Is.True);
                Assert.That(Write(second, KindB, 101), Is.True);

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();

                for (int drain = 0; drain < 6; drain++)
                {
                    Assert.That(
                        drainer.Drain(destination), Is.EqualTo(1),
                        "drain " + drain + " should have taken exactly one record");
                }

                // The lanes take turns, and lane 1 is not left behind while
                // lane 0 still has records waiting.
                destination.AssertRecord(0, KindA, 0);
                destination.AssertRecord(1, KindB, 100);
                destination.AssertRecord(2, KindA, 1);
                destination.AssertRecord(3, KindB, 101);
                destination.AssertRecord(4, KindA, 2);
                destination.AssertRecord(5, KindA, 3);
            }
        }

        [Test]
        public void EmptyLanesAreSkipped_AndAnEmptySetTakesNothing()
        {
            using (TraceLaneSet set = Set(8, Lane(KindA, 64, 8), Lane(KindB, 64, 8), Lane(KindC, 64, 8)))
            {
                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();

                Assert.That(
                    drainer.Drain(destination), Is.EqualTo(0),
                    "no lane had anything, so nothing should have been taken");
                Assert.That(destination.Count, Is.EqualTo(0));

                // Only the middle lane has anything to give.
                TraceLaneWriter second = set.CreateWriter(1);
                Assert.That(Write(second, KindB, 7), Is.True);
                Assert.That(Write(second, KindB, 8), Is.True);

                Assert.That(drainer.Drain(destination), Is.EqualTo(2));
                destination.AssertRecord(0, KindB, 7);
                destination.AssertRecord(1, KindB, 8);

                Assert.That(drainer.Drain(destination), Is.EqualTo(0));
            }
        }

        // -------------------------------------------------------------------
        // What the destination gets, and what the lane gets back
        // -------------------------------------------------------------------

        [Test]
        public void TheDestinationSeesTheOriginalBytes_AndTheRoomComesBackAfterwards()
        {
            // Eight payload bytes and two index slots: two four-byte records
            // fill it completely.
            using (TraceLaneSet set = Set(8, Lane(KindA, 8, 2)))
            {
                TraceLaneWriter writer = set.CreateWriter(0);

                Assert.That(Write(writer, KindA, 1, 2, 3, 4), Is.True);
                Assert.That(Write(writer, KindA, 5, 6, 7, 8), Is.True);
                Assert.That(Write(writer, KindA, 9), Is.False, "the lane is full");

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();

                Assert.That(drainer.Drain(destination), Is.EqualTo(2));
                destination.AssertRecord(0, KindA, new byte[] { 1, 2, 3, 4 });
                destination.AssertRecord(1, KindA, new byte[] { 5, 6, 7, 8 });

                // The room the drained records held is the producer's again.
                Assert.That(Write(writer, KindA, 10, 11, 12, 13), Is.True);
                Assert.That(Write(writer, KindA, 14, 15, 16, 17), Is.True);
                Assert.That(drainer.Drain(destination), Is.EqualTo(2));
                destination.AssertRecord(2, KindA, new byte[] { 10, 11, 12, 13 });
                destination.AssertRecord(3, KindA, new byte[] { 14, 15, 16, 17 });
            }
        }

        [Test]
        public void DrainingLeavesTheDropCountAndTheMaskMeaningWhereTheyWere()
        {
            using (TraceLaneSet set = Set(8, Lane(KindA, 8, 2), Lane(KindB, 64, 8)))
            {
                TraceLaneWriter full = set.CreateWriter(0);
                TraceLaneWriter other = set.CreateWriter(1);

                Assert.That(Write(full, KindA, 1, 2, 3, 4), Is.True);
                Assert.That(Write(full, KindA, 5, 6, 7, 8), Is.True);

                // One refusal for a full lane, and one event the lane does not
                // carry, which is not a refusal at all.
                Assert.That(Write(full, KindA, 9), Is.False);
                Assert.That(Write(full, KindB, 9), Is.False);
                Assert.That(set.DropCountOf(0), Is.EqualTo(1L));
                Assert.That(set.DropCountOf(1), Is.EqualTo(0L));

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();
                Assert.That(drainer.Drain(destination), Is.EqualTo(2));

                // Draining neither clears the count nor counts anything of its
                // own, on that lane or its neighbour.
                Assert.That(
                    set.DropCountOf(0), Is.EqualTo(1L),
                    "the lane's own drop count is not the drain's to change");
                Assert.That(set.DropCountOf(1), Is.EqualTo(0L));

                // And the masks still mean exactly what they meant.
                Assert.That(Write(full, KindB, 1), Is.False);
                Assert.That(Write(other, KindA, 1), Is.False);
                Assert.That(set.DropCountOf(0), Is.EqualTo(1L));
                Assert.That(set.DropCountOf(1), Is.EqualTo(0L));
                Assert.That(Write(full, KindA, 1), Is.True);
                Assert.That(Write(other, KindB, 1), Is.True);
            }
        }

        // -------------------------------------------------------------------
        // Lifetime
        // -------------------------------------------------------------------

        [Test]
        public void DrainingAReleasedSetIsRefused()
        {
            TraceLaneSet set = Set(8, Lane(KindA, 64, 8));
            TraceLaneDrainer drainer = new TraceLaneDrainer(set);
            RecordingDestination destination = new RecordingDestination();
            try
            {
                Assert.That(Write(set.CreateWriter(0), KindA, 1), Is.True);
                Assert.That(drainer.Drain(destination), Is.EqualTo(1));
            }
            finally
            {
                set.Dispose();
            }

            Assert.Throws<ObjectDisposedException>(() => drainer.Drain(destination));
            Assert.Throws<ArgumentNullException>(() => drainer.Drain(null));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static TraceLaneSettings Lane(
            TraceEventType kind, int payloadCapacity, int indexCapacity)
        {
            return new TraceLaneSettings(
                TraceLaneEventMask.None.With(kind), payloadCapacity, indexCapacity);
        }

        private static TraceLaneSet Set(int normalDrainMaxRecordCount, params TraceLaneSettings[] lanes)
        {
            return new TraceLaneSet(
                new TraceLaneSetProfile(
                MaxPayloadLength,
                normalDrainMaxRecordCount,
                HistoryPageSize,
                HistoryPageCount,
                lanes));
        }

        private static bool Write(TraceLaneWriter writer, TraceEventType kind, params byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return writer.TryWrite(kind, null, 0);
            }

            fixed (byte* pinned = bytes)
            {
                return writer.TryWrite(kind, pinned, bytes.Length);
            }
        }

        /// <summary>
        /// A destination that copies each record while it is being handed the
        /// payload, which is all a destination is allowed to do with it, and
        /// keeps the copies so the order can be checked afterwards.
        /// </summary>
        private sealed class RecordingDestination : ITraceRecordDestination
        {
            private readonly List<TraceEventType> _kinds = new List<TraceEventType>();
            private readonly List<byte[]> _payloads = new List<byte[]>();

            internal int Count => _kinds.Count;

            public void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
            {
                byte[] copy = new byte[payloadLength];
                for (int i = 0; i < payloadLength; i++)
                {
                    copy[i] = payload[i];
                }

                _kinds.Add(recordKind);
                _payloads.Add(copy);
            }

            internal void AssertRecord(int position, TraceEventType kind, params byte[] expected)
            {
                Assert.That(
                    _kinds.Count, Is.GreaterThan(position),
                    "record " + position + " never arrived");
                Assert.That(
                    _kinds[position], Is.EqualTo(kind),
                    "record " + position + " came from another lane");
                Assert.That(
                    _payloads[position].Length, Is.EqualTo(expected.Length),
                    "record " + position + " had another length");

                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.That(
                        _payloads[position][i], Is.EqualTo(expected[i]),
                        "record " + position + " byte " + i + " differs");
                }
            }
        }
    }
}
