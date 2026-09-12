using System;
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
    /// Measures the existing trace writer and the variable-length lane on the
    /// same records, and walks one lane set through its whole life from taking
    /// a writer to releasing the lanes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are measurements, not a gate. Nothing here asserts a time, a
    /// ratio, or that either backend is the faster one - the assertions are
    /// about counts, bytes, drops, drains and sealing. The numbers are printed
    /// for a person to read and are not written to any file, schema, or
    /// baseline.
    /// </para>
    /// <para>
    /// The two backends do not hold records the same way, and where that shows
    /// - a queue that grows against a ring that is fixed - it is reported as a
    /// difference in what they are, not as one of them being better.
    /// </para>
    /// </remarks>
    public unsafe class TraceLaneComparisonTests
    {
        private const TraceEventType Kind = TraceEventType.TaskScheduled;
        private const long TestRunId = 0x0C12E;

        private const int Records = 4096;
        private const int Samples = 5;
        private const int WarmUpSamples = 1;

        // -------------------------------------------------------------------
        // Workload 1: everything fits
        // -------------------------------------------------------------------

        [Test]
        public void WithinCapacity_BothBackendsTakeEveryRecord()
        {
            TraceEvent[] events = BuildEvents(Records);
            int recordBytes = UnsafeUtility.SizeOf<TraceEvent>();

            Sample existing = default;
            Sample lane = default;
            long[] existingWrite = new long[Samples];
            long[] existingDrain = new long[Samples];
            long[] laneWrite = new long[Samples];
            long[] laneDrain = new long[Samples];

            for (int sample = 0; sample < WarmUpSamples + Samples; sample++)
            {
                bool measured = sample >= WarmUpSamples;
                int slot = sample - WarmUpSamples;

                Sample existingSample = MeasureExistingWriter(events, recordBytes);
                Sample laneSample = MeasureLaneWriter(events, recordBytes, Records, Records * recordBytes);

                if (!measured)
                {
                    continue;
                }

                existingWrite[slot] = existingSample.WriteTicks;
                existingDrain[slot] = existingSample.DrainTicks;
                laneWrite[slot] = laneSample.WriteTicks;
                laneDrain[slot] = laneSample.DrainTicks;
                existing = existingSample;
                lane = laneSample;
            }

            // What the measurement is about: every attempt was taken, and
            // every record taken came back out again.
            Assert.That(existing.Accepted, Is.EqualTo(Records));
            Assert.That(existing.Drained, Is.EqualTo(Records));
            Assert.That(lane.Accepted, Is.EqualTo(Records));
            Assert.That(
                lane.Drained, Is.EqualTo(lane.Accepted),
                "every record the lane accepted must come back out of it");
            Assert.That(lane.Drops, Is.EqualTo(0L));
            Assert.That(
                lane.CopiedBytes, Is.EqualTo((long)Records * recordBytes),
                "the lane copies exactly the payload bytes it accepted");
            Assert.That(lane.Sealed, Is.True, "the final drain and seal must complete");

            Report(
                "Workload 1 - within capacity, managed calls",
                recordBytes,
                existing,
                lane,
                Median(existingWrite),
                Median(existingDrain),
                Median(laneWrite),
                Median(laneDrain));

            MeasureBurstWriters(events, recordBytes);
        }

        // -------------------------------------------------------------------
        // Workload 2: the lane runs out of room
        // -------------------------------------------------------------------

        [Test]
        public void OverCapacity_TheLaneRefusesWithoutWaitingAndCountsTheDrops()
        {
            const int Attempts = 1024;
            const int LaneRoom = 256;

            TraceEvent[] events = BuildEvents(Attempts);
            int recordBytes = UnsafeUtility.SizeOf<TraceEvent>();

            // Room for a quarter of the attempts, and nothing draining while
            // they are written.
            Sample lane = MeasureLaneWriter(events, recordBytes, LaneRoom, LaneRoom * recordBytes);
            Sample existing = MeasureExistingWriter(events, recordBytes);

            Assert.That(lane.Accepted, Is.EqualTo(LaneRoom));
            Assert.That(
                lane.Drops, Is.EqualTo((long)(Attempts - LaneRoom)),
                "every refused attempt is counted once");
            Assert.That(lane.Drained, Is.EqualTo(lane.Accepted));
            Assert.That(
                lane.CopiedBytes, Is.EqualTo((long)LaneRoom * recordBytes),
                "nothing is copied for a record that was refused");
            Assert.That(lane.Sealed, Is.True);

            // The queue behind the existing writer grows instead of refusing,
            // so it takes them all.
            Assert.That(existing.Accepted, Is.EqualTo(Attempts));
            Assert.That(existing.Drained, Is.EqualTo(Attempts));

            TestContext.Out.WriteLine(
                "Workload 2 - over the lane's capacity, managed calls (measurement, not a gate)");
            TestContext.Out.WriteLine("  attempts                  : " + Attempts);
            TestContext.Out.WriteLine(
                "  lane accepted / dropped   : " + lane.Accepted + " / " + lane.Drops);
            TestContext.Out.WriteLine(
                "  lane drained / seal       : " + lane.Drained + " / " + lane.Sealed);
            TestContext.Out.WriteLine("  existing accepted         : " + existing.Accepted);
            TestContext.Out.WriteLine(
                "  capacity model            : the lane is a fixed ring of " + LaneRoom +
                " records and refuses the rest without waiting; the existing writer enqueues into" +
                " a queue that grows, so it refuses nothing here. The two counts are not a" +
                " comparison of speed or quality - they are different capacity models.");
            TestContext.Out.WriteLine(
                "  lane write / drain ticks  : " + lane.WriteTicks + " / " + lane.DrainTicks +
                " (" + System.Diagnostics.Stopwatch.Frequency + " ticks per second)");
            TestContext.Out.WriteLine(
                "  existing write / drain    : " + existing.WriteTicks + " / " + existing.DrainTicks);
        }

        // -------------------------------------------------------------------
        // The whole life of one lane set
        // -------------------------------------------------------------------

        [Test]
        public void OneLaneSetGoesAllTheWayFromWriterToRelease()
        {
            const int JobRecords = 128;

            Assert.That(
                BurstCompiler.IsEnabled, Is.True,
                "Burst compilation is off, so this would not be the producer path");

            TraceEvent[] events = BuildEvents(JobRecords);
            int recordBytes = UnsafeUtility.SizeOf<TraceEvent>();

            TraceLaneSetProfile profile = new TraceLaneSetProfile(
                recordBytes,
                16,
                recordBytes + TraceRecordHeader.Bytes,
                2,
                new[]
                {
                    new TraceLaneSettings(
                        TraceLaneEventMask.None.With(Kind), JobRecords * recordBytes, JobRecords),
                });

            TraceLaneSet set = new TraceLaneSet(profile);
            NativeArray<TraceEvent> source = new NativeArray<TraceEvent>(JobRecords, Allocator.TempJob);
            NativeArray<int> accepted = new NativeArray<int>(1, Allocator.TempJob);
            JobHandle handle = default;
            try
            {
                for (int record = 0; record < JobRecords; record++)
                {
                    source[record] = events[record];
                }

                // 1. take the writer for this lane's ordinal.
                LaneWriteJob job = new LaneWriteJob
                {
                    Writer = set.CreateWriter(0),
                    Source = source,
                    RecordBytes = recordBytes,
                    Accepted = accepted,
                };

                // 2. the producer writes, and 3. the producer finishes. From
                // here on 4. nothing uses that writer again.
                handle = job.Schedule();
                handle.Complete();
                Assert.That(accepted[0], Is.EqualTo(JobRecords));

                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                CountingDestination destination = new CountingDestination(recordBytes);

                // 5. an ordinary drain, bounded by the profile.
                int ordinary = drainer.Drain(destination);
                Assert.That(ordinary, Is.EqualTo(profile.NormalDrainMaxRecordCount));

                // 6. and 7. the rest of the records, and what was dropped.
                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(destination);
                Assert.That(result.RecordCount, Is.EqualTo((long)(JobRecords - ordinary)));
                Assert.That(result.DropCount, Is.EqualTo(0L));
                Assert.That(destination.Count, Is.EqualTo(JobRecords));
                Assert.That(
                    destination.CopiedBytes, Is.EqualTo((long)JobRecords * recordBytes));
                Assert.Throws<InvalidOperationException>(() => drainer.Drain(destination));

                Assert.That(
                    set.AllocatedBytes, Is.EqualTo(profile.LaneStorageBytes),
                    "sealing releases no lane storage");

                TestContext.Out.WriteLine("Phase 0.12 checkpoint");
                TestContext.Out.WriteLine(
                    "  records written in a Burst job : " + accepted[0]);
                TestContext.Out.WriteLine(
                    "  ordinary drain / final drain   : " + ordinary + " / " + result.RecordCount);
                TestContext.Out.WriteLine("  aggregated drops               : " + result.DropCount);
                TestContext.Out.WriteLine(
                    "  fixed unmanaged lane storage   : " + profile.TotalStorageBytes + " bytes");
            }
            finally
            {
                // The producer has stopped before anything is released.
                handle.Complete();
                // 8. the lanes go back.
                set.Dispose();
                source.Dispose();
                accepted.Dispose();
            }

            Assert.That(set.AllocatedBytes, Is.EqualTo(0L));
        }

        // -------------------------------------------------------------------
        // Measuring
        // -------------------------------------------------------------------

        private static Sample MeasureExistingWriter(TraceEvent[] events, int recordBytes)
        {
            Sample sample = default;

            using (TraceLogger logger = new TraceLogger(events.Length, TestRunId))
            {
                SealableTraceWriter writer = logger.CaptureRunWriter;
                int accepted = 0;

                long allocatedBefore = AllocatedBytes();
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                for (int record = 0; record < events.Length; record++)
                {
                    if (writer.TryEnqueue(events[record]))
                    {
                        accepted++;
                    }
                }

                clock.Stop();
                sample.WriteTicks = clock.ElapsedTicks;

                clock.Restart();
                int drained = logger.Drain();
                clock.Stop();
                sample.DrainTicks = clock.ElapsedTicks;
                sample.Allocated = AllocatedBytes() - allocatedBefore;

                sample.Accepted = accepted;
                sample.Drained = drained;

                // Every accepted event is one whole TraceEvent value copied
                // into the queue.
                sample.CopiedBytes = (long)accepted * recordBytes;
            }

            return sample;
        }

        private static Sample MeasureLaneWriter(
            TraceEvent[] events, int recordBytes, int indexCapacity, int payloadCapacity)
        {
            Sample sample = default;

            TraceLaneSetProfile profile = new TraceLaneSetProfile(
                recordBytes,
                events.Length,
                recordBytes + TraceRecordHeader.Bytes,
                2,
                new[]
                {
                    new TraceLaneSettings(
                        TraceLaneEventMask.None.With(Kind), payloadCapacity, indexCapacity),
                });

            using (TraceLaneSet set = new TraceLaneSet(profile))
            {
                TraceLaneWriter writer = set.CreateWriter(0);
                TraceLaneDrainer drainer = new TraceLaneDrainer(set);
                CountingDestination destination = new CountingDestination(recordBytes);
                int accepted = 0;

                long allocatedBefore = AllocatedBytes();
                fixed (TraceEvent* source = events)
                {
                    byte* bytes = (byte*)source;
                    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                    for (int record = 0; record < events.Length; record++)
                    {
                        if (writer.TryWrite(Kind, bytes + (record * recordBytes), recordBytes))
                        {
                            accepted++;
                        }
                    }

                    clock.Stop();
                    sample.WriteTicks = clock.ElapsedTicks;
                }

                System.Diagnostics.Stopwatch drainClock = System.Diagnostics.Stopwatch.StartNew();
                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(destination);
                drainClock.Stop();
                sample.DrainTicks = drainClock.ElapsedTicks;
                sample.Allocated = AllocatedBytes() - allocatedBefore;

                sample.Accepted = accepted;
                sample.Drained = (int)result.RecordCount;
                sample.Drops = result.DropCount;
                sample.CopiedBytes = destination.CopiedBytes;
                sample.Sealed = true;
                sample.UnmanagedBytes = set.AllocatedBytes;
            }

            return sample;
        }

        /// <summary>
        /// The same two writers again, this time driven from Burst-compiled
        /// jobs. These numbers are kept apart from the managed ones rather
        /// than averaged together with them.
        /// </summary>
        private static void MeasureBurstWriters(TraceEvent[] events, int recordBytes)
        {
            if (!BurstCompiler.IsEnabled)
            {
                TestContext.Out.WriteLine(
                    "Burst is disabled in this editor, so the Burst rows were not measured.");
                return;
            }

            long[] laneTicks = new long[Samples];
            long[] existingTicks = new long[Samples];
            int laneAccepted = 0;
            int existingAccepted = 0;

            NativeArray<TraceEvent> source =
                new NativeArray<TraceEvent>(events.Length, Allocator.TempJob);
            NativeArray<int> accepted = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                for (int record = 0; record < events.Length; record++)
                {
                    source[record] = events[record];
                }

                for (int sample = 0; sample < WarmUpSamples + Samples; sample++)
                {
                    int slot = sample - WarmUpSamples;

                    TraceLaneSetProfile profile = new TraceLaneSetProfile(
                        recordBytes,
                        events.Length,
                        recordBytes + TraceRecordHeader.Bytes,
                        2,
                        new[]
                        {
                            new TraceLaneSettings(
                                TraceLaneEventMask.None.With(Kind),
                                events.Length * recordBytes,
                                events.Length),
                        });

                    using (TraceLaneSet set = new TraceLaneSet(profile))
                    {
                        LaneWriteJob job = new LaneWriteJob
                        {
                            Writer = set.CreateWriter(0),
                            Source = source,
                            RecordBytes = recordBytes,
                            Accepted = accepted,
                        };

                        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                        job.Schedule().Complete();
                        clock.Stop();

                        laneAccepted = accepted[0];
                        if (slot >= 0)
                        {
                            laneTicks[slot] = clock.ElapsedTicks;
                        }
                    }

                    using (TraceLogger logger = new TraceLogger(events.Length, TestRunId))
                    {
                        ExistingWriteJob job = new ExistingWriteJob
                        {
                            Writer = logger.CaptureRunWriter,
                            Source = source,
                            Accepted = accepted,
                        };

                        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                        job.Schedule().Complete();
                        clock.Stop();

                        existingAccepted = accepted[0];
                        if (slot >= 0)
                        {
                            existingTicks[slot] = clock.ElapsedTicks;
                        }

                        logger.Drain();
                    }
                }
            }
            finally
            {
                source.Dispose();
                accepted.Dispose();
            }

            TestContext.Out.WriteLine(
                "Workload 1 - within capacity, Burst jobs (measurement, not a gate)");
            TestContext.Out.WriteLine(
                "  records                      : " + events.Length);
            TestContext.Out.WriteLine(
                "  lane accepted / existing     : " + laneAccepted + " / " + existingAccepted);
            TestContext.Out.WriteLine(
                "  lane schedule+write (median) : " + Median(laneTicks) + " ticks");
            TestContext.Out.WriteLine(
                "  existing schedule+write      : " + Median(existingTicks) + " ticks");
            TestContext.Out.WriteLine(
                "  note                         : each figure includes scheduling and waiting" +
                " for one job, and is kept apart from the managed call figures above.");
        }

        private static void Report(
            string title,
            int recordBytes,
            Sample existing,
            Sample lane,
            long existingWriteTicks,
            long existingDrainTicks,
            long laneWriteTicks,
            long laneDrainTicks)
        {
            TestContext.Out.WriteLine(title + " (measurement, not a gate)");
            TestContext.Out.WriteLine(
                "  records / samples / warm-up : " + Records + " / " + Samples + " / " + WarmUpSamples);
            TestContext.Out.WriteLine(
                "  stopwatch frequency         : " + System.Diagnostics.Stopwatch.Frequency + " ticks per second" +
                (System.Diagnostics.Stopwatch.IsHighResolution ? " (high resolution)" : " (low resolution)"));
            TestContext.Out.WriteLine("  record size                 : " + recordBytes + " bytes");

            WriteBackend("existing writer", existing, existingWriteTicks, existingDrainTicks);
            WriteBackend("lane writer", lane, laneWriteTicks, laneDrainTicks);

            TestContext.Out.WriteLine(
                "  copied bytes definition     : lane = the accepted payload lengths added up;" +
                " existing = one whole TraceEvent value per accepted event.");
            TestContext.Out.WriteLine(
                "  managed allocation          : measured on this thread over write and drain" +
                " together, after warm-up" + (AllocationMeasurable ? "." : ", unavailable here."));
            TestContext.Out.WriteLine(
                "  not measured                : native allocation beyond the lane's own fixed" +
                " region, and GC events.");
        }

        private static void WriteBackend(string name, Sample sample, long writeTicks, long drainTicks)
        {
            double perRecord = sample.Accepted == 0
                ? 0d
                : (double)writeTicks / sample.Accepted;

            TestContext.Out.WriteLine("  " + name + ":");
            TestContext.Out.WriteLine(
                "    write (median)            : " + writeTicks + " ticks, " +
                perRecord.ToString("0.000") + " ticks per accepted record");
            TestContext.Out.WriteLine("    drain (median)            : " + drainTicks + " ticks");
            TestContext.Out.WriteLine(
                "    accepted / drained / drop : " + sample.Accepted + " / " + sample.Drained +
                " / " + sample.Drops);
            TestContext.Out.WriteLine("    copied bytes              : " + sample.CopiedBytes);
            TestContext.Out.WriteLine(
                "    managed allocation        : " +
                (AllocationMeasurable ? sample.Allocated + " bytes" : "unavailable"));

            if (sample.UnmanagedBytes > 0)
            {
                TestContext.Out.WriteLine(
                    "    fixed unmanaged storage   : " + sample.UnmanagedBytes + " bytes");
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static TraceEvent[] BuildEvents(int count)
        {
            TraceEvent[] events = new TraceEvent[count];
            for (int record = 0; record < count; record++)
            {
                events[record] = new TraceEvent
                {
                    Timestamp = record,
                    FrameId = record / 60,
                    FixedStepId = record,
                    ThreadId = 1,
                    TaskId = record,
                    TestRunId = TestRunId,
                    EventType = Kind,
                    TaskType = TraceTaskType.None,
                    FromState = 0,
                    ToState = 1,
                    Reason = TraceReason.None,
                    Value0 = record,
                    Value1 = -record,
                };
            }

            return events;
        }

        private static long Median(long[] values)
        {
            long[] sorted = (long[])values.Clone();
            Array.Sort(sorted);
            return sorted[sorted.Length / 2];
        }

        private static bool AllocationMeasurable = true;

        /// <summary>
        /// Bytes this thread has allocated so far, when the runtime can say.
        /// Nothing is built here to work around a runtime that cannot: the
        /// figure is simply reported as unavailable.
        /// </summary>
        private static long AllocatedBytes()
        {
            if (!AllocationMeasurable)
            {
                return 0L;
            }

            try
            {
                return GC.GetAllocatedBytesForCurrentThread();
            }
            catch (Exception)
            {
                AllocationMeasurable = false;
                return 0L;
            }
        }

        private struct Sample
        {
            internal long WriteTicks;
            internal long DrainTicks;
            internal int Accepted;
            internal int Drained;
            internal long Drops;
            internal long CopiedBytes;
            internal long Allocated;
            internal long UnmanagedBytes;
            internal bool Sealed;
        }

        /// <summary>
        /// Copies each record out of the lane while it is handed over, which is
        /// what a destination is for, and counts what it copied.
        /// </summary>
        private sealed class CountingDestination : ITraceRecordDestination
        {
            private readonly byte[] _buffer;

            private int _count;
            private long _copiedBytes;

            internal CountingDestination(int recordBytes)
            {
                _buffer = new byte[recordBytes];
            }

            internal int Count => _count;

            internal long CopiedBytes => _copiedBytes;

            public void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
            {
                fixed (byte* destination = _buffer)
                {
                    UnsafeUtility.MemCpy(destination, payload, payloadLength);
                }

                _count++;
                _copiedBytes += payloadLength;
            }
        }

        /// <summary>Writes the same records into the lane from Burst.</summary>
        [BurstCompile(CompileSynchronously = true)]
        private struct LaneWriteJob : IJob
        {
            public TraceLaneWriter Writer;

            [ReadOnly]
            public NativeArray<TraceEvent> Source;

            public int RecordBytes;
            public NativeArray<int> Accepted;

            public void Execute()
            {
                byte* bytes = (byte*)Source.GetUnsafeReadOnlyPtr();
                int accepted = 0;

                for (int record = 0; record < Source.Length; record++)
                {
                    if (Writer.TryWrite(
                        TraceEventType.TaskScheduled, bytes + (record * RecordBytes), RecordBytes))
                    {
                        accepted++;
                    }
                }

                Accepted[0] = accepted;
            }
        }

        /// <summary>Writes the same records through the existing writer from Burst.</summary>
        [BurstCompile(CompileSynchronously = true)]
        private struct ExistingWriteJob : IJob
        {
            public SealableTraceWriter Writer;

            [ReadOnly]
            public NativeArray<TraceEvent> Source;

            public NativeArray<int> Accepted;

            public void Execute()
            {
                int accepted = 0;
                for (int record = 0; record < Source.Length; record++)
                {
                    if (Writer.TryEnqueue(Source[record]))
                    {
                        accepted++;
                    }
                }

                Accepted[0] = accepted;
            }
        }
    }
}
