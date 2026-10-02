using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the final drain: taking everything the lanes still
    /// hold once the producers have stopped, adding up what was dropped, and
    /// sealing the drainer.
    /// </summary>
    /// <remarks>
    /// Producers having stopped is the caller's promise, so nothing here races
    /// a writer against a final drain or uses a writer afterwards. These cases
    /// are about what is collected, what is counted, and what is refused once
    /// the drainer is sealed.
    /// </remarks>
    public unsafe class TraceFinalDrainContractTests
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
        // Taking everything that is left
        // -------------------------------------------------------------------

        [Test]
        public void TheFinalDrainTakesMoreThanOneOrdinaryDrainEverCould()
        {
            const int Records = 7;

            // An ordinary drain stops at two records; there are seven waiting.
            using (TraceLaneSet set = Set(2, Lane(KindA, 64, 8)))
            {
                TraceLaneWriter writer = set.CreateWriter(0);
                for (int record = 0; record < Records; record++)
                {
                    Assert.That(Write(writer, KindA, (byte)record), Is.True);
                }

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();

                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(destination);

                Assert.That(result.RecordCount, Is.EqualTo((long)Records));
                Assert.That(destination.Count, Is.EqualTo(Records));
                for (int record = 0; record < Records; record++)
                {
                    destination.AssertRecord(record, KindA, (byte)record);
                }
            }
        }

        [Test]
        public void TheFinalDrainCarriesOnFromWhereTheOrdinaryOneStopped()
        {
            using (TraceLaneSet set = Set(2, Lane(KindA, 64, 8), Lane(KindB, 64, 8)))
            {
                TraceLaneWriter first = set.CreateWriter(0);
                TraceLaneWriter second = set.CreateWriter(1);
                for (int record = 0; record < 3; record++)
                {
                    Assert.That(Write(first, KindA, (byte)record), Is.True);
                    Assert.That(Write(second, KindB, (byte)(100 + record)), Is.True);
                }

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();

                // One ordinary drain first, which takes one record from each
                // lane and leaves the round at lane 0.
                Assert.That(drainer.Drain(destination), Is.EqualTo(2));
                destination.AssertRecord(0, KindA, 0);
                destination.AssertRecord(1, KindB, 100);

                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(destination);

                Assert.That(result.RecordCount, Is.EqualTo(4L));
                destination.AssertRecord(2, KindA, 1);
                destination.AssertRecord(3, KindB, 101);
                destination.AssertRecord(4, KindA, 2);
                destination.AssertRecord(5, KindB, 102);
            }
        }

        [Test]
        public void EveryLaneIsEmptyAfterwards_EmptyOnesIncluded()
        {
            using (TraceLaneSet set = Set(2, Lane(KindA, 64, 8), Lane(KindB, 64, 8), Lane(KindC, 64, 8)))
            {
                TraceLaneWriter first = set.CreateWriter(0);
                TraceLaneWriter third = set.CreateWriter(2);

                Assert.That(Write(first, KindA, 1), Is.True);
                Assert.That(Write(first, KindA, 2), Is.True);
                Assert.That(Write(first, KindA, 3), Is.True);
                Assert.That(Write(third, KindC, 9), Is.True);

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();

                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(destination);

                Assert.That(result.RecordCount, Is.EqualTo(4L));
                for (int ordinal = 0; ordinal < 3; ordinal++)
                {
                    Assert.That(
                        set.TryPeek(ordinal, out TraceLaneIndexEntry _, out byte* _), Is.False,
                        "lane " + ordinal + " still had a record in it");
                }
            }
        }

        // -------------------------------------------------------------------
        // What was dropped
        // -------------------------------------------------------------------

        [Test]
        public void TheDropCountsOfEveryLaneAreAddedUp()
        {
            using (TraceLaneSet set = Set(8, Lane(KindA, 8, 1), Lane(KindB, 8, 4)))
            {
                TraceLaneWriter first = set.CreateWriter(0);
                TraceLaneWriter second = set.CreateWriter(1);

                // One index slot: the second record has nowhere to go.
                Assert.That(Write(first, KindA, 1, 2, 3, 4), Is.True);
                Assert.That(Write(first, KindA, 5, 6, 7, 8), Is.False);

                // Eight payload bytes: the third record has no room.
                Assert.That(Write(second, KindB, 1, 2, 3, 4), Is.True);
                Assert.That(Write(second, KindB, 5, 6, 7, 8), Is.True);
                Assert.That(Write(second, KindB, 9, 10, 11, 12), Is.False);
                Assert.That(Write(second, KindB, 13, 14, 15, 16), Is.False);

                Assert.That(set.DropCountOf(0), Is.EqualTo(1L));
                Assert.That(set.DropCountOf(1), Is.EqualTo(2L));

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(new RecordingDestination());

                Assert.That(result.RecordCount, Is.EqualTo(3L));
                Assert.That(result.DropCount, Is.EqualTo(3L));
            }
        }

        [Test]
        public void TheDropTotalStopsAtItsLargestValueRatherThanTurningOver()
        {
            using (TraceLaneSet set = Set(8, Lane(KindA, 64, 4), Lane(KindB, 64, 4)))
            {
                // Counts this big cannot be reached by dropping records one at
                // a time, so they are put there directly.
                SetDropCount(set, 0, long.MaxValue - 1L);
                SetDropCount(set, 1, 5L);

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(new RecordingDestination());

                Assert.That(result.RecordCount, Is.EqualTo(0L));
                Assert.That(
                    result.DropCount, Is.EqualTo(long.MaxValue),
                    "the total stops at the largest value instead of turning over");
            }
        }

        // -------------------------------------------------------------------
        // Sealing
        // -------------------------------------------------------------------

        [Test]
        public void OnceSealed_BothOrdinaryAndFinalDrainsAreRefused()
        {
            using (TraceLaneSet set = Set(2, Lane(KindA, 64, 8)))
            {
                TraceLaneWriter writer = set.CreateWriter(0);
                Assert.That(Write(writer, KindA, 1), Is.True);
                Assert.That(Write(writer, KindA, 2), Is.True);
                Assert.That(Write(writer, KindA, 3), Is.True);

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                RecordingDestination destination = new RecordingDestination();

                Assert.That(drainer.DrainToEndAndSeal(destination).RecordCount, Is.EqualTo(3L));

                Assert.Throws<InvalidOperationException>(() => drainer.Drain(destination));
                Assert.Throws<InvalidOperationException>(
                    () => drainer.DrainToEndAndSeal(destination));
                Assert.That(
                    destination.Count, Is.EqualTo(3),
                    "a refused drain hands over nothing");
            }
        }

        [Test]
        public void ADestinationThatThrows_LeavesItsRecordInTheLaneAndNothingSealed()
        {
            using (TraceLaneSet set = Set(2, Lane(KindA, 64, 8)))
            {
                TraceLaneWriter writer = set.CreateWriter(0);
                for (int record = 0; record < 5; record++)
                {
                    Assert.That(Write(writer, KindA, (byte)record), Is.True);
                }

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                ThrowingDestination throwing = new ThrowingDestination(3);

                Assert.Throws<InvalidOperationException>(
                    () => drainer.DrainToEndAndSeal(throwing));
                Assert.That(throwing.Count, Is.EqualTo(3), "three records got through");

                // The record the destination refused is still the lane's, and
                // the drainer was never sealed, so the rest can still be had.
                RecordingDestination destination = new RecordingDestination();
                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(destination);

                Assert.That(
                    result.RecordCount, Is.EqualTo(2L),
                    "the record that was thrown on must not have been consumed");
                destination.AssertRecord(0, KindA, 3);
                destination.AssertRecord(1, KindA, 4);
            }
        }

        // -------------------------------------------------------------------
        // Lifetime
        // -------------------------------------------------------------------

        [Test]
        public void SealingKeepsTheLaneStorage_WhichIsStillTheSetsToRelease()
        {
            TraceLaneSetProfile profile = new TraceLaneSetProfile(
                MaxPayloadLength, 2, HistoryPageSize, HistoryPageCount, new[] { Lane(KindA, 64, 8) });
            TraceLaneSet set = new TraceLaneSet(profile);
            try
            {
                Assert.That(Write(set.CreateWriter(0), KindA, 1), Is.True);

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                Assert.That(
                    drainer.DrainToEndAndSeal(new RecordingDestination()).RecordCount,
                    Is.EqualTo(1L));

                Assert.That(
                    set.AllocatedBytes, Is.EqualTo(profile.LaneStorageBytes),
                    "sealing the drainer releases no lane storage");
            }
            finally
            {
                set.Dispose();
            }

            Assert.That(set.AllocatedBytes, Is.EqualTo(0L));
        }

        [Test]
        public void AFinalDrainOfAReleasedSetIsRefused()
        {
            TraceLaneSet set = Set(2, Lane(KindA, 64, 8));
            TraceLaneDrainer drainer = new TraceLaneDrainer(set);
            set.Dispose();

            Assert.Throws<ObjectDisposedException>(
                () => drainer.DrainToEndAndSeal(new RecordingDestination()));
            Assert.Throws<ArgumentNullException>(() => drainer.DrainToEndAndSeal(null));
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
        /// Puts one lane's drop count near its limit, so the saturating step
        /// can be seen instead of being counted to one record at a time.
        /// </summary>
        private static void SetDropCount(TraceLaneSet set, int ordinal, long value)
        {
            FieldInfo lanesField = typeof(TraceLaneSet).GetField(
                "_lanes", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(lanesField, Is.Not.Null, "_lanes field not found");

            Array lanes = (Array)lanesField.GetValue(set);
            Assert.That(lanes, Is.Not.Null, "the set held no lanes");
            object lane = lanes.GetValue(ordinal);

            FieldInfo cursorsField = lane.GetType().GetField(
                "_cursors", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(cursorsField, Is.Not.Null, "_cursors field not found");

            long* cursors = (long*)Pointer.Unbox(cursorsField.GetValue(lane));
            Assert.That(cursors != null, Is.True, "the lane's cursor block was not readable");
            cursors[TraceLaneCursors.DropCount] = value;
        }

        /// <summary>
        /// Copies each record while it is being handed over, which is all a
        /// destination may do with the payload.
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

        /// <summary>
        /// Takes a few records and then refuses one, to see what a failure
        /// part way through leaves behind.
        /// </summary>
        private sealed class ThrowingDestination : ITraceRecordDestination
        {
            private readonly int _throwOn;
            private int _count;

            internal ThrowingDestination(int throwOn)
            {
                _throwOn = throwOn;
            }

            internal int Count => _count;

            public void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
            {
                if (_count == _throwOn)
                {
                    throw new InvalidOperationException("this destination refuses this record");
                }

                _count++;
            }
        }
    }
}
