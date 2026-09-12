using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the fixed set of variable-length trace lanes and the
    /// profile it is built from: what is settled before anything is allocated,
    /// what each ordinal is bound to, and that one lane's trouble stays its
    /// own.
    /// </summary>
    /// <remarks>
    /// Two lanes are enough for every case here; nothing sweeps capacities.
    /// Draining, ordering between lanes, and any connection to the existing
    /// trace producers are not this unit's subject and are not exercised.
    /// </remarks>
    public unsafe class TraceLaneSetContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;
        private const TraceEventType KindB = TraceEventType.TaskStarted;

        private const int MaxPayloadLength = 4;
        private const int NormalDrainMaxRecordCount = 16;

        // The history is not this fixture's subject; these are the smallest
        // valid settings that carry a record of MaxPayloadLength.
        private const int HistoryPageSize = 64;
        private const int HistoryPageCount = 2;

        private static TraceLaneEventMask OnlyA => TraceLaneEventMask.None.With(KindA);

        private static TraceLaneEventMask OnlyB => TraceLaneEventMask.None.With(KindB);

        // -------------------------------------------------------------------
        // The profile
        // -------------------------------------------------------------------

        [Test]
        public void TheProfileDoesNotSeeChangesToTheArrayItWasBuiltFrom()
        {
            TraceLaneSettings[] lanes =
            {
                new TraceLaneSettings(OnlyA, 64, 3),
                new TraceLaneSettings(OnlyB, 16, 8),
            };

            TraceLaneSetProfile profile = new TraceLaneSetProfile(
                MaxPayloadLength, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                lanes);
            long totalWhenBuilt = profile.TotalStorageBytes;

            // The caller keeps its own array and edits it afterwards.
            lanes[0] = new TraceLaneSettings(OnlyB, 4096, 999);
            lanes[1] = new TraceLaneSettings(OnlyA, 4096, 999);

            Assert.That(profile.LaneCount, Is.EqualTo(2));
            Assert.That(profile.Lane(0).PayloadCapacity, Is.EqualTo(64));
            Assert.That(profile.Lane(0).IndexCapacity, Is.EqualTo(3));
            Assert.That(profile.Lane(0).Enabled.IsEnabled(KindA), Is.True);
            Assert.That(profile.Lane(0).Enabled.IsEnabled(KindB), Is.False);
            Assert.That(profile.Lane(1).PayloadCapacity, Is.EqualTo(16));
            Assert.That(profile.Lane(1).IndexCapacity, Is.EqualTo(8));
            Assert.That(profile.Lane(1).Enabled.IsEnabled(KindB), Is.True);
            Assert.That(
                profile.TotalStorageBytes, Is.EqualTo(totalWhenBuilt),
                "the total was settled when the profile was built");
        }

        [Test]
        public void TheProfilesTotalStorage_IsWhatTheLanesActuallyHold()
        {
            TraceLaneSetProfile profile = TwoLaneProfile();

            TraceLaneSet set = new TraceLaneSet(profile);
            try
            {
                Assert.That(profile.LaneStorageBytes, Is.GreaterThan(0L));
                Assert.That(
                    set.AllocatedBytes, Is.EqualTo(profile.LaneStorageBytes),
                    "the lanes hold exactly what the profile said they would");
            }
            finally
            {
                set.Dispose();
            }
        }

        [Test]
        public void AProfileThatCannotBeHeldIsRefusedBeforeAnythingIsAllocated()
        {
            Assert.Throws<ArgumentNullException>(
                () => new TraceLaneSetProfile(
                    MaxPayloadLength, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                    null));

            // No lanes at all.
            Assert.Throws<ArgumentException>(
                () => new TraceLaneSetProfile(
                    MaxPayloadLength, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                    new TraceLaneSettings[0]));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLaneSetProfile(
                    0, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                    OneLane(64, 4)));

            // Nothing may be drained at all.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLaneSetProfile(
                    MaxPayloadLength, 0, HistoryPageSize, HistoryPageCount, OneLane(64, 4)));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLaneSetProfile(
                    MaxPayloadLength, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                    OneLane(0, 4)));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLaneSetProfile(
                    MaxPayloadLength, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                    OneLane(64, 0)));

            // A payload ring too small for the longest record the Run allows.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLaneSetProfile(
                    MaxPayloadLength, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                    OneLane(MaxPayloadLength - 1, 4)));

            // A bad lane later in the profile is caught just as early.
            TraceLaneSettings[] secondLaneIsBad =
            {
                new TraceLaneSettings(OnlyA, 64, 4),
                new TraceLaneSettings(OnlyB, 64, 0),
            };
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLaneSetProfile(
                    MaxPayloadLength, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                    secondLaneIsBad));
        }

        // -------------------------------------------------------------------
        // Ordinals
        // -------------------------------------------------------------------

        [Test]
        public void EachOrdinalCarriesItsOwnMaskAndCapacities()
        {
            // Lane 0 runs out of index slots first; lane 1 runs out of payload
            // room first, so each capacity is seen where it binds.
            TraceLaneSetProfile profile = TwoLaneProfile();
            TraceLaneSet set = new TraceLaneSet(profile);
            try
            {
                TraceLaneWriter first = set.CreateWriter(0);
                TraceLaneWriter second = set.CreateWriter(1);

                Assert.That(first.IsEnabled(KindA), Is.True);
                Assert.That(first.IsEnabled(KindB), Is.False);
                Assert.That(second.IsEnabled(KindB), Is.True);
                Assert.That(second.IsEnabled(KindA), Is.False);

                // Three index slots, and payload room to spare.
                Assert.That(Write(first, KindA, 1, 2, 3, 4), Is.True);
                Assert.That(Write(first, KindA, 5, 6, 7, 8), Is.True);
                Assert.That(Write(first, KindA, 9, 10, 11, 12), Is.True);
                Assert.That(
                    Write(first, KindA, 13, 14, 15, 16), Is.False,
                    "lane 0 should have run out of index slots after three records");

                // Sixteen payload bytes, and index slots to spare.
                Assert.That(Write(second, KindB, 1, 2, 3, 4), Is.True);
                Assert.That(Write(second, KindB, 5, 6, 7, 8), Is.True);
                Assert.That(Write(second, KindB, 9, 10, 11, 12), Is.True);
                Assert.That(Write(second, KindB, 13, 14, 15, 16), Is.True);
                Assert.That(
                    Write(second, KindB, 17, 18, 19, 20), Is.False,
                    "lane 1 should have run out of payload room after four records");

                AssertNextRecord(set, 0, KindA, new byte[] { 1, 2, 3, 4 });
                AssertNextRecord(set, 1, KindB, new byte[] { 1, 2, 3, 4 });
            }
            finally
            {
                set.Dispose();
            }
        }

        [Test]
        public void EveryWritersLongestRecord_IsTheRunsMaxPayloadLength()
        {
            TraceLaneSetProfile profile = TwoLaneProfile();
            TraceLaneSet set = new TraceLaneSet(profile);
            try
            {
                Assert.That(set.MaxPayloadLength, Is.EqualTo(MaxPayloadLength));
                Assert.That(set.NormalDrainMaxRecordCount, Is.EqualTo(NormalDrainMaxRecordCount));

                for (int ordinal = 0; ordinal < profile.LaneCount; ordinal++)
                {
                    TraceLaneWriter writer = set.CreateWriter(ordinal);
                    TraceEventType kind = ordinal == 0 ? KindA : KindB;

                    Assert.That(
                        writer.MaxPayloadLength, Is.EqualTo(MaxPayloadLength),
                        "lane " + ordinal + " carries the Run's longest record");
                    Assert.That(
                        Write(writer, kind, new byte[MaxPayloadLength]), Is.True,
                        "lane " + ordinal + " should take a record of exactly that length");
                    Assert.That(
                        Write(writer, kind, new byte[MaxPayloadLength + 1]), Is.False,
                        "lane " + ordinal + " should refuse a longer record than the Run allows");
                }
            }
            finally
            {
                set.Dispose();
            }
        }

        [Test]
        public void AskingForALaneThatIsNotThere_IsRefused()
        {
            TraceLaneSet set = new TraceLaneSet(TwoLaneProfile());
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => set.CreateWriter(2));
                Assert.Throws<ArgumentOutOfRangeException>(() => set.CreateWriter(-1));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => set.TryPeek(2, out TraceLaneIndexEntry _, out byte* _));
            }
            finally
            {
                set.Dispose();
            }
        }

        // -------------------------------------------------------------------
        // One lane's trouble stays its own
        // -------------------------------------------------------------------

        [Test]
        public void AFullLaneLeavesTheOtherLaneUntouched()
        {
            TraceLaneSettings[] lanes =
            {
                new TraceLaneSettings(OnlyA, 8, 1),
                new TraceLaneSettings(OnlyB, 64, 4),
            };
            TraceLaneSet set = new TraceLaneSet(
                new TraceLaneSetProfile(
                    MaxPayloadLength, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                    lanes));
            try
            {
                TraceLaneWriter full = set.CreateWriter(0);
                TraceLaneWriter other = set.CreateWriter(1);

                Assert.That(Write(full, KindA, 1, 2, 3, 4), Is.True);
                Assert.That(Write(full, KindA, 5, 6, 7, 8), Is.False, "lane 0 is full");
                Assert.That(Write(full, KindA, 9, 10, 11, 12), Is.False);

                // The neighbour took no notice at all.
                Assert.That(Write(other, KindB, 21, 22), Is.True);
                Assert.That(Write(other, KindB, 23, 24), Is.True);
                AssertNextRecord(set, 1, KindB, new byte[] { 21, 22 });
                AssertNextRecord(set, 1, KindB, new byte[] { 23, 24 });

                // And the record the full lane already held is still its own.
                AssertNextRecord(set, 0, KindA, new byte[] { 1, 2, 3, 4 });
                Assert.That(Write(full, KindA, 31, 32, 33, 34), Is.True);
                AssertNextRecord(set, 0, KindA, new byte[] { 31, 32, 33, 34 });
            }
            finally
            {
                set.Dispose();
            }
        }

        [Test]
        public void ADisabledEventFollowsTheLanesOwnMask()
        {
            TraceLaneSet set = new TraceLaneSet(TwoLaneProfile());
            try
            {
                TraceLaneWriter first = set.CreateWriter(0);
                TraceLaneWriter second = set.CreateWriter(1);

                // Each lane refuses the event the other one carries, and
                // writes nothing while doing it.
                Assert.That(Write(first, KindB, 1, 2), Is.False);
                Assert.That(Write(second, KindA, 3, 4), Is.False);

                Assert.That(Write(first, KindA, 5, 6), Is.True);
                Assert.That(Write(second, KindB, 7, 8), Is.True);

                Assert.That(set.TryPeek(0, out TraceLaneIndexEntry entry, out byte* payload), Is.True);
                Assert.That(entry.RecordKind, Is.EqualTo(KindA));
                Assert.That(
                    entry.PayloadStart, Is.EqualTo(0L),
                    "the refused event must not have moved lane 0's write position");
                AssertBytes(payload, new byte[] { 5, 6 });
                set.Consume(0, entry);

                AssertNextRecord(set, 1, KindB, new byte[] { 7, 8 });
                Assert.That(set.TryPeek(0, out TraceLaneIndexEntry _, out byte* _), Is.False);
                Assert.That(set.TryPeek(1, out TraceLaneIndexEntry _, out byte* _), Is.False);
            }
            finally
            {
                set.Dispose();
            }
        }

        // -------------------------------------------------------------------
        // Two producers at once
        // -------------------------------------------------------------------

        [Test]
        public void TwoProducersOnSeparateLanes_KeepTheirOwnOrder()
        {
            const int FirstRecords = 64;
            const int SecondRecords = 32;

            Assert.That(
                BurstCompiler.IsEnabled, Is.True,
                "Burst compilation is off, so this test would prove nothing");

            TraceLaneSettings[] lanes =
            {
                new TraceLaneSettings(OnlyA, 512, FirstRecords),
                new TraceLaneSettings(OnlyB, 512, SecondRecords),
            };

            TraceLaneSet set = new TraceLaneSet(
                new TraceLaneSetProfile(
                    MaxPayloadLength, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                    lanes));
            NativeArray<int> firstAccepted = new NativeArray<int>(1, Allocator.TempJob);
            NativeArray<int> secondAccepted = new NativeArray<int>(1, Allocator.TempJob);
            JobHandle both = default;
            try
            {
                TraceLaneSetWriteJob first = new TraceLaneSetWriteJob
                {
                    Writer = set.CreateWriter(0),
                    Kind = KindA,
                    Records = FirstRecords,
                    Accepted = firstAccepted,
                };
                TraceLaneSetWriteJob second = new TraceLaneSetWriteJob
                {
                    Writer = set.CreateWriter(1),
                    Kind = KindB,
                    Records = SecondRecords,
                    Accepted = secondAccepted,
                };

                // Both producers run at once, each on its own lane.
                both = JobHandle.CombineDependencies(first.Schedule(), second.Schedule());
                both.Complete();

                Assert.That(firstAccepted[0], Is.EqualTo(FirstRecords));
                Assert.That(secondAccepted[0], Is.EqualTo(SecondRecords));

                AssertLaneHolds(set, 0, KindA, FirstRecords);
                AssertLaneHolds(set, 1, KindB, SecondRecords);
            }
            finally
            {
                // The producers must have stopped before their lanes go.
                both.Complete();
                set.Dispose();
                firstAccepted.Dispose();
                secondAccepted.Dispose();
            }
        }

        /// <summary>
        /// Writes a run of records into one lane from inside Burst-compiled
        /// code, through the same value-type writer a production producer
        /// would be given for its ordinal.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        internal struct TraceLaneSetWriteJob : IJob
        {
            public TraceLaneWriter Writer;
            public TraceEventType Kind;
            public int Records;
            public NativeArray<int> Accepted;

            public void Execute()
            {
                byte* payload = stackalloc byte[4];
                int accepted = 0;

                for (int record = 0; record < Records; record++)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        payload[i] = (byte)(record + i);
                    }

                    if (Writer.TryWrite(Kind, payload, 4))
                    {
                        accepted++;
                    }
                }

                Accepted[0] = accepted;
            }
        }

        // -------------------------------------------------------------------
        // Lifetime
        // -------------------------------------------------------------------

        [Test]
        public void TheSetReleasesEveryLaneOnDispose_AndDisposingTwiceIsHarmless()
        {
            TraceLaneSetProfile profile = TwoLaneProfile();
            TraceLaneSet set = new TraceLaneSet(profile);
            try
            {
                Assert.That(set.AllocatedBytes, Is.EqualTo(profile.LaneStorageBytes));
                Assert.That(set.LaneCount, Is.EqualTo(2));
            }
            finally
            {
                set.Dispose();
            }

            Assert.That(set.AllocatedBytes, Is.EqualTo(0L));
            Assert.Throws<ObjectDisposedException>(() => set.CreateWriter(0));
            Assert.Throws<ObjectDisposedException>(
                () => set.TryPeek(0, out TraceLaneIndexEntry _, out byte* _));

            // Asking for it back twice takes nothing else from the allocator.
            set.Dispose();
            Assert.That(set.AllocatedBytes, Is.EqualTo(0L));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Two lanes that differ in every way the profile can express: which
        /// event they carry, how much payload they hold, and how many records.
        /// </summary>
        private static TraceLaneSetProfile TwoLaneProfile()
        {
            TraceLaneSettings[] lanes =
            {
                new TraceLaneSettings(OnlyA, 64, 3),
                new TraceLaneSettings(OnlyB, 16, 8),
            };

            return new TraceLaneSetProfile(
                MaxPayloadLength, NormalDrainMaxRecordCount, HistoryPageSize, HistoryPageCount,
                lanes);
        }

        private static TraceLaneSettings[] OneLane(int payloadCapacity, int indexCapacity)
        {
            return new[] { new TraceLaneSettings(OnlyA, payloadCapacity, indexCapacity) };
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

        private static void AssertNextRecord(
            TraceLaneSet set, int ordinal, TraceEventType kind, byte[] expected)
        {
            Assert.That(
                set.TryPeek(ordinal, out TraceLaneIndexEntry entry, out byte* payload), Is.True,
                "lane " + ordinal + " should have a record waiting");
            Assert.That(entry.RecordKind, Is.EqualTo(kind));
            Assert.That(entry.PayloadLength, Is.EqualTo(expected.Length));
            AssertBytes(payload, expected);
            set.Consume(ordinal, entry);
        }

        /// <summary>
        /// Reads one lane out in full and checks it came back in the order its
        /// own producer wrote it, whatever the other lane was doing.
        /// </summary>
        private static void AssertLaneHolds(
            TraceLaneSet set, int ordinal, TraceEventType kind, int records)
        {
            for (int record = 0; record < records; record++)
            {
                Assert.That(
                    set.TryPeek(ordinal, out TraceLaneIndexEntry entry, out byte* payload), Is.True,
                    "lane " + ordinal + " ran out at record " + record);
                Assert.That(entry.RecordKind, Is.EqualTo(kind));
                Assert.That(entry.PayloadLength, Is.EqualTo(4));
                Assert.That(
                    payload[0], Is.EqualTo((byte)record),
                    "lane " + ordinal + " record " + record + " was out of order");
                Assert.That(payload[3], Is.EqualTo((byte)(record + 3)));
                set.Consume(ordinal, entry);
            }

            Assert.That(
                set.TryPeek(ordinal, out TraceLaneIndexEntry _, out byte* _), Is.False,
                "lane " + ordinal + " held more records than its producer wrote");
        }

        private static void AssertBytes(byte* payload, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(
                    payload[i], Is.EqualTo(expected[i]), "payload byte " + i + " differs");
            }
        }
    }
}
