using System;
using System.Threading;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the variable-length SPSC trace lane: what one
    /// producer can put in it, what one consumer sees, and what happens when
    /// it is full. The payload boundary is a pointer, so these tests use the
    /// same pointers the product does rather than a friendlier stand-in.
    /// </summary>
    /// <remarks>
    /// Capacities here are the smallest that make each case happen at all -
    /// one wrap, one full index, one full payload - rather than a sweep of
    /// every size.
    /// </remarks>
    public unsafe class TraceLaneContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;
        private const TraceEventType KindB = TraceEventType.TaskStarted;
        private const TraceEventType KindOff = TraceEventType.TaskCompleted;

        private static TraceLaneEventMask TwoEvents =>
            TraceLaneEventMask.None.With(KindA).With(KindB);

        // -------------------------------------------------------------------
        // Ordinary traffic
        // -------------------------------------------------------------------

        [Test]
        public void EnabledRecords_AreReadBackInOrder_WithTheirKindAndPayload()
        {
            using (TraceLane lane = new TraceLane(64, 64, 4, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(Write(writer, KindA, 1, 2, 3), Is.True);
                Assert.That(Write(writer, KindB, 9), Is.True);

                AssertNextRecord(lane, KindA, new byte[] { 1, 2, 3 });
                AssertNextRecord(lane, KindB, new byte[] { 9 });

                Assert.That(lane.TryPeek(out TraceLaneIndexEntry _, out byte* _), Is.False);
                Assert.That(lane.DropCount, Is.EqualTo(0L));
            }
        }

        [Test]
        public void AnEmptyPayload_IsARecordLikeAnyOther()
        {
            using (TraceLane lane = new TraceLane(16, 16, 2, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(writer.TryWrite(KindA, null, 0), Is.True);

                Assert.That(lane.TryPeek(out TraceLaneIndexEntry entry, out byte* _), Is.True);
                Assert.That(entry.RecordKind, Is.EqualTo(KindA));
                Assert.That(entry.PayloadLength, Is.EqualTo(0));
                lane.Consume(entry);
                Assert.That(lane.DropCount, Is.EqualTo(0L));
            }
        }

        [Test]
        public void TheCallersBytes_AreCopiedBeforeTryWriteReturns()
        {
            using (TraceLane lane = new TraceLane(64, 64, 4, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                using (NativeArray<byte> caller = new NativeArray<byte>(4, Allocator.Temp))
                {
                    byte* callerBytes = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(caller);
                    callerBytes[0] = 10;
                    callerBytes[1] = 20;
                    callerBytes[2] = 30;
                    callerBytes[3] = 40;

                    Assert.That(writer.TryWrite(KindA, callerBytes, 4), Is.True);

                    // The caller's memory is the caller's again the moment
                    // TryWrite returns.
                    callerBytes[0] = 99;
                    callerBytes[1] = 99;
                    callerBytes[2] = 99;
                    callerBytes[3] = 99;
                }

                AssertNextRecord(lane, KindA, new byte[] { 10, 20, 30, 40 });
            }
        }

        [Test]
        public void ADisabledEvent_IsNotWrittenAndIsNotADrop()
        {
            using (TraceLane lane = new TraceLane(64, 64, 4, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(writer.IsEnabled(KindOff), Is.False);
                Assert.That(Write(writer, KindOff, 1, 2, 3), Is.False);

                Assert.That(lane.TryPeek(out TraceLaneIndexEntry _, out byte* _), Is.False);
                Assert.That(
                    lane.DropCount, Is.EqualTo(0L),
                    "an event the lane does not carry was never this lane's to lose");

                // And the lane still works for what it does carry.
                Assert.That(Write(writer, KindA, 7), Is.True);
                AssertNextRecord(lane, KindA, new byte[] { 7 });
            }
        }

        // -------------------------------------------------------------------
        // Refusals
        // -------------------------------------------------------------------

        [Test]
        public void WhenTheIndexIsFull_TheRecordIsRefusedAndCounted()
        {
            // Two index slots, payload room to spare: the index runs out first.
            using (TraceLane lane = new TraceLane(64, 64, 2, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(Write(writer, KindA, 1), Is.True);
                Assert.That(Write(writer, KindA, 2), Is.True);
                Assert.That(Write(writer, KindA, 3), Is.False);
                Assert.That(lane.DropCount, Is.EqualTo(1L));

                // The two that were taken are still exactly as they were.
                AssertNextRecord(lane, KindA, new byte[] { 1 });
                AssertNextRecord(lane, KindA, new byte[] { 2 });
            }
        }

        [Test]
        public void WhenThePayloadIsFull_TheRecordIsRefusedAndCounted()
        {
            // Index room to spare, eight payload bytes: the payload runs out.
            using (TraceLane lane = new TraceLane(8, 8, 8, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(Write(writer, KindA, 1, 2, 3, 4, 5), Is.True);
                Assert.That(Write(writer, KindB, 6, 7, 8), Is.True);
                Assert.That(Write(writer, KindA, 9), Is.False);
                Assert.That(lane.DropCount, Is.EqualTo(1L));

                AssertNextRecord(lane, KindA, new byte[] { 1, 2, 3, 4, 5 });
                AssertNextRecord(lane, KindB, new byte[] { 6, 7, 8 });
            }
        }

        [Test]
        public void APayloadLongerThanTheRing_IsRefusedAndCounted()
        {
            using (TraceLane lane = new TraceLane(8, 8, 4, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();
                Assert.That(writer.MaxPayloadLength, Is.EqualTo(8));

                Assert.That(
                    Write(writer, KindA, new byte[9]), Is.False,
                    "a record longer than the whole ring can never be contiguous");
                Assert.That(lane.DropCount, Is.EqualTo(1L));
                Assert.That(lane.TryPeek(out TraceLaneIndexEntry _, out byte* _), Is.False);

                // Exactly the ring's length is still a record.
                Assert.That(Write(writer, KindA, new byte[8]), Is.True);
                Assert.That(lane.DropCount, Is.EqualTo(1L));
            }
        }

        [Test]
        public void APayloadLongerThanTheLanesLongestRecord_IsRefusedThoughTheRingHasRoom()
        {
            // Sixteen bytes of ring, but no single record longer than four.
            // The ring's size says how many records fit, not how long one may
            // be, so a five-byte record is refused with twelve bytes free.
            using (TraceLane lane = new TraceLane(16, 4, 4, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(lane.MaxPayloadLength, Is.EqualTo(4));
                Assert.That(lane.PayloadCapacity, Is.EqualTo(16));
                Assert.That(writer.MaxPayloadLength, Is.EqualTo(4));

                Assert.That(Write(writer, KindA, 1, 2, 3, 4), Is.True);
                Assert.That(lane.DropCount, Is.EqualTo(0L));

                Assert.That(Write(writer, KindB, 5, 6, 7, 8, 9), Is.False);
                Assert.That(lane.DropCount, Is.EqualTo(1L));

                // The record already in the lane is untouched, and so are the
                // cursors: the next record still begins where the first one
                // ended rather than after a record that was never written.
                Assert.That(lane.TryPeek(out TraceLaneIndexEntry entry, out byte* payload), Is.True);
                Assert.That(entry.RecordKind, Is.EqualTo(KindA));
                Assert.That(entry.PayloadStart, Is.EqualTo(0L));
                AssertBytes(payload, new byte[] { 1, 2, 3, 4 });
                lane.Consume(entry);

                Assert.That(Write(writer, KindB, 10, 11, 12, 13), Is.True);
                Assert.That(lane.TryPeek(out TraceLaneIndexEntry next, out byte* nextPayload), Is.True);
                Assert.That(
                    next.PayloadStart, Is.EqualTo(4L),
                    "the refused record must not have moved the write position");
                AssertBytes(nextPayload, new byte[] { 10, 11, 12, 13 });
                lane.Consume(next);

                Assert.That(lane.DropCount, Is.EqualTo(1L));
            }
        }

        [Test]
        public void ARefusalLeavesTheRecordsAlreadyInTheLaneReadable()
        {
            using (TraceLane lane = new TraceLane(8, 8, 2, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(Write(writer, KindA, 1, 2, 3), Is.True);
                Assert.That(Write(writer, KindB, 4, 5), Is.True);

                // Both an index refusal and an oversize refusal, back to back.
                Assert.That(Write(writer, KindA, 6), Is.False);
                Assert.That(Write(writer, KindA, new byte[64]), Is.False);
                Assert.That(lane.DropCount, Is.EqualTo(2L));

                AssertNextRecord(lane, KindA, new byte[] { 1, 2, 3 });
                AssertNextRecord(lane, KindB, new byte[] { 4, 5 });
            }
        }

        [Test]
        public void TheDropCountSaturatesRatherThanWrapping()
        {
            using (TraceLane lane = new TraceLane(8, 8, 1, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                // Start it one short of the largest value it can hold.
                SetDropCount(lane, long.MaxValue - 1L);

                Assert.That(Write(writer, KindA, new byte[64]), Is.False);
                Assert.That(lane.DropCount, Is.EqualTo(long.MaxValue));

                Assert.That(Write(writer, KindA, new byte[64]), Is.False);
                Assert.That(
                    lane.DropCount, Is.EqualTo(long.MaxValue),
                    "the count stops at the largest value instead of turning over");
            }
        }

        // -------------------------------------------------------------------
        // Wrapping and reuse
        // -------------------------------------------------------------------

        [Test]
        public void ARecordThatWouldStraddleTheEnd_StartsAgainAtTheBeginning()
        {
            // Eight payload bytes. Five, consumed, then four: the four cannot
            // fit in the three that are left before the end, so the tail is
            // spent as padding and the record starts at the beginning.
            using (TraceLane lane = new TraceLane(8, 8, 4, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(Write(writer, KindA, 1, 2, 3, 4, 5), Is.True);
                AssertNextRecord(lane, KindA, new byte[] { 1, 2, 3, 4, 5 });

                Assert.That(Write(writer, KindB, 6, 7, 8, 9), Is.True);

                Assert.That(lane.TryPeek(out TraceLaneIndexEntry entry, out byte* payload), Is.True);
                Assert.That(entry.RecordKind, Is.EqualTo(KindB));
                Assert.That(entry.PayloadLength, Is.EqualTo(4));
                Assert.That(
                    entry.PayloadStart % lane.PayloadCapacity, Is.EqualTo(0L),
                    "the wrapped record must begin at the start of the ring");
                Assert.That(
                    entry.PayloadStart, Is.EqualTo(8L),
                    "the three bytes of tail were spent as padding");
                AssertBytes(payload, new byte[] { 6, 7, 8, 9 });
                lane.Consume(entry);
            }
        }

        [Test]
        public void ThePaddingAWrapNeeds_CountsAgainstTheRoomTheLaneHasLeft()
        {
            // Two bytes, then four, then the first two consumed: the write
            // position sits two bytes short of the end with four bytes still
            // unconsumed behind it, so four of the eight bytes are free. A
            // four-byte record cannot use them, because it must be contiguous:
            // it needs the two bytes of tail as padding as well, six in all.
            // Counting only the four would let it start at the beginning of
            // the ring and overwrite the record still waiting there.
            using (TraceLane lane = new TraceLane(8, 8, 4, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(Write(writer, KindA, 1, 2), Is.True);
                Assert.That(Write(writer, KindB, 3, 4, 5, 6), Is.True);
                AssertNextRecord(lane, KindA, new byte[] { 1, 2 });

                // Four bytes free, six needed once the padding is counted.
                Assert.That(Write(writer, KindA, 10, 11, 12, 13), Is.False);
                Assert.That(lane.DropCount, Is.EqualTo(1L));

                // The record that was still there is untouched.
                AssertNextRecord(lane, KindB, new byte[] { 3, 4, 5, 6 });

                // And once it is gone, the same write fits - after the two
                // bytes of tail padding, at the start of the ring.
                Assert.That(Write(writer, KindA, 10, 11, 12, 13), Is.True);
                Assert.That(lane.TryPeek(out TraceLaneIndexEntry entry, out byte* payload), Is.True);
                Assert.That(
                    entry.PayloadStart % lane.PayloadCapacity, Is.EqualTo(0L),
                    "the wrapped record must begin at the start of the ring");
                AssertBytes(payload, new byte[] { 10, 11, 12, 13 });
                lane.Consume(entry);
            }
        }

        [Test]
        public void ConsumingARecord_GivesItsRoomBackToTheProducer()
        {
            using (TraceLane lane = new TraceLane(8, 8, 2, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(Write(writer, KindA, 1, 2, 3, 4), Is.True);
                Assert.That(Write(writer, KindB, 5, 6, 7, 8), Is.True);

                // Full on both rings.
                Assert.That(Write(writer, KindA, 9), Is.False);
                Assert.That(lane.DropCount, Is.EqualTo(1L));

                AssertNextRecord(lane, KindA, new byte[] { 1, 2, 3, 4 });

                // One record's room back is enough for one more record.
                Assert.That(Write(writer, KindA, 9, 10, 11, 12), Is.True);
                Assert.That(lane.DropCount, Is.EqualTo(1L));

                AssertNextRecord(lane, KindB, new byte[] { 5, 6, 7, 8 });
                AssertNextRecord(lane, KindA, new byte[] { 9, 10, 11, 12 });
            }
        }

        [Test]
        public void APeekedPayload_IsNotOverwrittenUntilItIsConsumed()
        {
            using (TraceLane lane = new TraceLane(8, 8, 4, TwoEvents))
            {
                TraceLaneWriter writer = lane.CreateWriter();

                Assert.That(Write(writer, KindA, 1, 2, 3, 4), Is.True);
                Assert.That(lane.TryPeek(out TraceLaneIndexEntry entry, out byte* payload), Is.True);

                // The producer fills everything it is allowed to while the
                // first record is still being held.
                Assert.That(Write(writer, KindB, 5, 6, 7, 8), Is.True);
                Assert.That(Write(writer, KindB, 9), Is.False);

                AssertBytes(payload, new byte[] { 1, 2, 3, 4 });
                lane.Consume(entry);
            }
        }

        // -------------------------------------------------------------------
        // Two threads, and a Burst job
        // -------------------------------------------------------------------

        [Test]
        public void AProducerThreadAndAConsumerThread_NeverSeeAPartialRecord()
        {
            const int Records = 4096;
            const int DeadlineMilliseconds = 30000;

            // The producer writes into memory the lane owns, so the lane may
            // only be released once the producer has physically stopped - and
            // that has to hold when an assertion below fails as well, not only
            // when the test passes. So the lane is released by hand, after the
            // producer is told to stop and is seen to have stopped.
            TraceLane lane = new TraceLane(256, 256, 16, TwoEvents);
            TraceLaneWriter writer = lane.CreateWriter();
            int written = 0;
            long refused = 0;
            int stopRequested = 0;
            long drops = -1;
            Exception producerFault = null;

            Thread producer = new Thread(() =>
            {
                try
                {
                    byte* payload = stackalloc byte[16];
                    for (int record = 0; record < Records; record++)
                    {
                        int length = (record % 16) + 1;
                        for (int i = 0; i < length; i++)
                        {
                            payload[i] = (byte)(record + i);
                        }

                        while (!writer.TryWrite(KindA, payload, length))
                        {
                            // Nothing in the lane waits: a full lane is the
                            // producer's own business, and this test simply
                            // keeps offering the same record. Each refusal is
                            // a drop, so they are counted here too.
                            refused++;
                            if (Volatile.Read(ref stopRequested) != 0)
                            {
                                return;
                            }

                            Thread.Yield();
                        }

                        written++;
                    }
                }
                catch (Exception fault)
                {
                    producerFault = fault;
                }
            });

            producer.IsBackground = true;
            bool producerStopped = false;
            try
            {
                producer.Start();

                int read = 0;
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                while (read < Records)
                {
                    Assert.That(
                        clock.ElapsedMilliseconds, Is.LessThan(DeadlineMilliseconds),
                        "only " + read + " of " + Records + " records arrived before the deadline");

                    if (!lane.TryPeek(out TraceLaneIndexEntry entry, out byte* payload))
                    {
                        Thread.Yield();
                        continue;
                    }

                    int length = (read % 16) + 1;
                    Assert.That(entry.RecordKind, Is.EqualTo(KindA));
                    Assert.That(
                        entry.PayloadLength, Is.EqualTo(length),
                        "record " + read + " came out with another record's length");

                    for (int i = 0; i < length; i++)
                    {
                        Assert.That(
                            payload[i], Is.EqualTo((byte)(read + i)),
                            "record " + read + " byte " + i + " was not this record's");
                    }

                    lane.Consume(entry);
                    read++;
                }
            }
            finally
            {
                Volatile.Write(ref stopRequested, 1);
                producerStopped = producer.Join(DeadlineMilliseconds);
                if (producerStopped)
                {
                    drops = lane.DropCount;
                    lane.Dispose();
                }
            }

            // A producer still running would be writing into the lane's
            // memory, so the lane above was deliberately left allocated
            // rather than freed underneath it.
            Assert.That(
                producerStopped, Is.True,
                "the producer did not stop, so the lane was left allocated on purpose");
            Assert.That(producerFault, Is.Null, "the producer failed: " + producerFault);
            Assert.That(written, Is.EqualTo(Records));
            Assert.That(
                drops, Is.EqualTo(refused),
                "the lane counted a different number of refusals than the producer met");
        }

        [Test]
        public void TheWriterWorksFromABurstCompiledJob()
        {
            const int Records = 64;

            Assert.That(
                BurstCompiler.IsEnabled, Is.True,
                "Burst compilation is off, so this test would prove nothing");

            // One index slot more than the job fills, so the oversize
            // record below can only be refused for its length.
            using (TraceLane lane = new TraceLane(512, 4, Records + 1, TwoEvents))
            using (NativeArray<int> results = new NativeArray<int>(2, Allocator.TempJob))
            {
                TraceLaneWriteJob job = new TraceLaneWriteJob
                {
                    Writer = lane.CreateWriter(),
                    Records = Records,
                    OversizeLength = 5,
                    Results = results,
                };

                // Compiled synchronously, so this really runs through Burst
                // rather than falling back to the managed path.
                job.Schedule().Complete();

                Assert.That(
                    job.Writer.MaxPayloadLength, Is.EqualTo(4),
                    "the writer should carry the lane's own record limit, not its ring size");
                Assert.That(
                    results[ResultAccepted], Is.EqualTo(Records),
                    "every record the job offered should have been taken");
                Assert.That(
                    results[ResultOversizeRefused], Is.EqualTo(1),
                    "a record longer than the limit must be refused inside Burst too");

                for (int record = 0; record < Records; record++)
                {
                    Assert.That(lane.TryPeek(out TraceLaneIndexEntry entry, out byte* payload), Is.True);
                    Assert.That(entry.RecordKind, Is.EqualTo(KindA));
                    Assert.That(entry.PayloadLength, Is.EqualTo(4));
                    Assert.That(payload[0], Is.EqualTo((byte)record));
                    Assert.That(payload[3], Is.EqualTo((byte)(record + 3)));
                    lane.Consume(entry);
                }

                Assert.That(
                    lane.DropCount, Is.EqualTo(1L),
                    "only the oversize record should have been counted as a drop");
            }
        }

        private const int ResultAccepted = 0;
        private const int ResultOversizeRefused = 1;

        /// <summary>
        /// Writes into the lane from inside Burst-compiled code, through the
        /// same value-type writer production callers use.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        internal struct TraceLaneWriteJob : IJob
        {
            public TraceLaneWriter Writer;
            public int Records;
            public int OversizeLength;
            public NativeArray<int> Results;

            public void Execute()
            {
                byte* payload = stackalloc byte[16];
                int accepted = 0;

                for (int record = 0; record < Records; record++)
                {
                    if (!Writer.IsEnabled(KindA))
                    {
                        continue;
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        payload[i] = (byte)(record + i);
                    }

                    if (Writer.TryWrite(KindA, payload, 4))
                    {
                        accepted++;
                    }
                }

                // The same limit applies here as anywhere else: a record
                // longer than the lane's longest is refused, even though the
                // ring has room for it many times over.
                for (int i = 0; i < OversizeLength; i++)
                {
                    payload[i] = (byte)i;
                }

                int oversizeRefused = Writer.TryWrite(KindA, payload, OversizeLength) ? 0 : 1;

                Results[ResultAccepted] = accepted;
                Results[ResultOversizeRefused] = oversizeRefused;
            }
        }

        // -------------------------------------------------------------------
        // Lifetime
        // -------------------------------------------------------------------

        [Test]
        public void TheLaneHoldsItsRegionUntilItIsDisposed()
        {
            TraceLane lane = new TraceLane(64, 64, 4, TwoEvents);
            try
            {
                Assert.That(lane.AllocatedBytes, Is.GreaterThan(0L));
                Assert.That(Write(lane.CreateWriter(), KindA, 1), Is.True);
            }
            finally
            {
                lane.Dispose();
            }

            Assert.That(lane.AllocatedBytes, Is.EqualTo(0L));
            Assert.Throws<ObjectDisposedException>(() => lane.CreateWriter());
            Assert.Throws<ObjectDisposedException>(
                () => lane.TryPeek(out TraceLaneIndexEntry _, out byte* _));
            Assert.Throws<ObjectDisposedException>(() => { long _ = lane.DropCount; });

            // Disposing again asks the allocator for nothing.
            lane.Dispose();
            Assert.That(lane.AllocatedBytes, Is.EqualTo(0L));
        }

        [Test]
        public void ALaneRefusesCapacitiesItCannotHold()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLane(0, 1, 4, TwoEvents));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLane(64, 64, 0, TwoEvents));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLane(64, 0, 4, TwoEvents));

            // A record limit the ring could never hold.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLane(64, 65, 4, TwoEvents));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

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

        private static void AssertNextRecord(TraceLane lane, TraceEventType kind, byte[] expected)
        {
            Assert.That(lane.TryPeek(out TraceLaneIndexEntry entry, out byte* payload), Is.True);
            Assert.That(entry.RecordKind, Is.EqualTo(kind));
            Assert.That(entry.PayloadLength, Is.EqualTo(expected.Length));
            AssertBytes(payload, expected);
            lane.Consume(entry);
        }

        private static void AssertBytes(byte* payload, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(
                    payload[i], Is.EqualTo(expected[i]), "payload byte " + i + " differs");
            }
        }

        /// <summary>
        /// Puts the lane's drop count near its limit so the saturating step
        /// itself can be observed, rather than counting to it.
        /// </summary>
        private static void SetDropCount(TraceLane lane, long value)
        {
            System.Reflection.FieldInfo field = typeof(TraceLane).GetField(
                "_cursors",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "_cursors field not found");

            long* cursors = (long*)System.Reflection.Pointer.Unbox(field.GetValue(lane));
            Assert.That(cursors != null, Is.True, "the lane's cursor block was not readable");
            cursors[TraceLaneCursors.DropCount] = value;
        }
    }
}
