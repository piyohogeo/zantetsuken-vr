using System;
using System.Collections.Generic;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for ending one Run's variable-length trace: what the
    /// final drain took, what the history came to, and whether the Run counts
    /// as complete.
    /// </summary>
    /// <remarks>
    /// Nothing here is connected to the existing trace paths or to a capture
    /// run: the lanes, the drainer and the history are built by hand, and the
    /// result is read as a value. The producers having stopped is the same
    /// promise the final drain already relied on.
    /// </remarks>
    public unsafe class TracePagedRunFinalizerContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;
        private const TraceEventType KindB = TraceEventType.TaskStarted;

        private const int MaxPayloadLength = 16;
        private const int NormalDrainMaxRecordCount = 2;

        // -------------------------------------------------------------------
        // Whether the Run kept everything
        // -------------------------------------------------------------------

        [Test]
        public void WithNothingDroppedTheRunIsComplete()
        {
            TraceLaneSetProfile profile = Profile(laneIndexCapacity: 8, pageSize: 128, pageCount: 4);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                Write(lanes.CreateWriter(0), KindA, 1);
                Write(lanes.CreateWriter(1), KindB, 2);

                TracePagedRunResult result = Finalizer(lanes, history).Finish();

                Assert.That(result.Integrity, Is.EqualTo(TraceIntegrityState.Complete));
                Assert.That(result.FinalDrain.RecordCount, Is.EqualTo(2L));
                Assert.That(result.FinalDrain.DropCount, Is.EqualTo(0L));
                Assert.That(result.HistoryCommittedRecordCount, Is.EqualTo(2L));
                Assert.That(result.HistoryDropCount, Is.EqualTo(0L));
            }
        }

        [Test]
        public void ALaneDropOnItsOwnMakesTheRunIncomplete()
        {
            // One index slot per lane, so the lane refuses the second record
            // while the history has room for everything it is given.
            TraceLaneSetProfile profile = Profile(laneIndexCapacity: 1, pageSize: 128, pageCount: 4);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                TraceLaneWriter writer = lanes.CreateWriter(0);
                Assert.That(TryWrite(writer, KindA, 1), Is.True);
                Assert.That(TryWrite(writer, KindA, 2), Is.False, "the lane is full");

                TracePagedRunResult result = Finalizer(lanes, history).Finish();

                Assert.That(result.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));
                Assert.That(result.FinalDrain.DropCount, Is.EqualTo(1L));
                Assert.That(
                    result.HistoryDropCount, Is.EqualTo(0L),
                    "the history was never short of room");
                Assert.That(result.HistoryCommittedRecordCount, Is.EqualTo(1L));
            }
        }

        [Test]
        public void AHistoryDropOnItsOwnMakesTheRunIncomplete()
        {
            // One page of 32 bytes holds three records of one payload byte -
            // nine bytes each - and no more; the lanes drop nothing.
            TraceLaneSetProfile profile = Profile(laneIndexCapacity: 8, pageSize: 32, pageCount: 1);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                TraceLaneWriter writer = lanes.CreateWriter(0);
                for (int record = 0; record < 5; record++)
                {
                    Assert.That(TryWrite(writer, KindA, (byte)record), Is.True);
                }

                TracePagedRunResult result = Finalizer(lanes, history).Finish();

                Assert.That(result.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));
                Assert.That(
                    result.FinalDrain.DropCount, Is.EqualTo(0L),
                    "no lane had to refuse a producer");
                Assert.That(result.FinalDrain.RecordCount, Is.EqualTo(5L));
                Assert.That(result.HistoryCommittedRecordCount, Is.EqualTo(3L));
                Assert.That(result.HistoryDropCount, Is.EqualTo(2L));
            }
        }

        // -------------------------------------------------------------------
        // What each count covers
        // -------------------------------------------------------------------

        [Test]
        public void TheHistoryCountsTheWholeRunWhileTheFinalDrainCountsOnlyWhatWasLeft()
        {
            TraceLaneSetProfile profile = Profile(laneIndexCapacity: 8, pageSize: 128, pageCount: 4);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                TraceLaneWriter first = lanes.CreateWriter(0);
                TraceLaneWriter second = lanes.CreateWriter(1);
                for (int record = 0; record < 3; record++)
                {
                    Assert.That(TryWrite(first, KindA, (byte)record), Is.True);
                    Assert.That(TryWrite(second, KindB, (byte)(100 + record)), Is.True);
                }

                TraceLaneDrainer drainer = new TraceLaneDrainer(lanes);

                // Two records go to the history during the Run.
                Assert.That(drainer.Drain(history), Is.EqualTo(NormalDrainMaxRecordCount));

                TracePagedRunResult result =
                    new TracePagedRunFinalizer(drainer, history).Finish();

                Assert.That(
                    result.FinalDrain.RecordCount, Is.EqualTo(4L),
                    "the final drain counts what was left, not the whole Run");
                Assert.That(
                    result.HistoryCommittedRecordCount, Is.EqualTo(6L),
                    "the history counts the ordinary drain as well");
                Assert.That(result.Integrity, Is.EqualTo(TraceIntegrityState.Complete));
            }
        }

        [Test]
        public void TheResultsViewReadsBackWhatTheRunRecorded()
        {
            TraceLaneSetProfile profile = Profile(laneIndexCapacity: 8, pageSize: 128, pageCount: 4);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                TraceLaneWriter first = lanes.CreateWriter(0);
                TraceLaneWriter second = lanes.CreateWriter(1);
                Assert.That(TryWrite(first, KindA, 1, 2), Is.True);
                Assert.That(TryWrite(second, KindB, 3), Is.True);
                Assert.That(TryWrite(first, KindA, 4, 5), Is.True);

                TracePagedRunResult result = Finalizer(lanes, history).Finish();

                ReadRecords records = new ReadRecords();
                result.View.VisitCommittedPages(records);

                Assert.That(
                    records.Count, Is.EqualTo((int)result.HistoryCommittedRecordCount),
                    "the view shows exactly the records the history counted");
                records.AssertRecord(0, KindA, new byte[] { 1, 2 });
                records.AssertRecord(1, KindB, new byte[] { 3 });
                records.AssertRecord(2, KindA, new byte[] { 4, 5 });
            }
        }

        [Test]
        public void WhenTheHistoryRanOutTheLanesAreStillEmptyAndTheCountsAddUp()
        {
            TraceLaneSetProfile profile = Profile(laneIndexCapacity: 8, pageSize: 32, pageCount: 1);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                TraceLaneWriter writer = lanes.CreateWriter(0);
                for (int record = 0; record < 5; record++)
                {
                    Assert.That(TryWrite(writer, KindA, (byte)record), Is.True);
                }

                TracePagedRunResult result = Finalizer(lanes, history).Finish();

                Assert.That(
                    lanes.TryPeek(0, out TraceLaneIndexEntry _, out byte* _), Is.False,
                    "the lanes gave up every record even though the history could not keep them");
                Assert.That(
                    result.HistoryCommittedRecordCount + result.HistoryDropCount,
                    Is.EqualTo(result.FinalDrain.RecordCount),
                    "every record handed over was either kept or counted as dropped");

                ReadRecords records = new ReadRecords();
                result.View.VisitCommittedPages(records);
                Assert.That(records.Count, Is.EqualTo(3));
                records.AssertRecord(0, KindA, new byte[] { 0 });
                records.AssertRecord(2, KindA, new byte[] { 2 });
            }
        }

        // -------------------------------------------------------------------
        // Finishing once
        // -------------------------------------------------------------------

        [Test]
        public void FinishingASecondTimeIsRefusedByTheDrainersOwnSeal()
        {
            TraceLaneSetProfile profile = Profile(laneIndexCapacity: 8, pageSize: 128, pageCount: 4);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                Write(lanes.CreateWriter(0), KindA, 1);

                TracePagedRunFinalizer finalizer = Finalizer(lanes, history);
                TracePagedRunResult result = finalizer.Finish();
                Assert.That(result.HistoryCommittedRecordCount, Is.EqualTo(1L));

                Assert.Throws<InvalidOperationException>(() => finalizer.Finish());
                Assert.That(
                    history.CommittedRecordCount, Is.EqualTo(1L),
                    "a refused second finish records nothing more");
            }
        }

        [Test]
        public void AHistoryThatCannotTakeRecordsLeavesNoResultBehind()
        {
            TraceLaneSetProfile profile = Profile(laneIndexCapacity: 8, pageSize: 128, pageCount: 4);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            {
                TracePagedHistory history = new TracePagedHistory(profile);
                Write(lanes.CreateWriter(0), KindA, 1);

                // Releasing the history before finishing is a mistake in the
                // calling code, and the failure it raises is what comes back.
                history.Dispose();

                TracePagedRunFinalizer finalizer = Finalizer(lanes, history);
                Assert.Throws<ObjectDisposedException>(() => finalizer.Finish());

                // Nothing was handed over, so the record is still the lane's
                // and the drainer was never sealed.
                Assert.That(
                    lanes.TryPeek(0, out TraceLaneIndexEntry _, out byte* _), Is.True,
                    "a record that was not taken stays in its lane");
            }
        }

        [Test]
        public void AFinalizerNeedsBothADrainerAndAHistory()
        {
            TraceLaneSetProfile profile = Profile(laneIndexCapacity: 8, pageSize: 128, pageCount: 4);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                Assert.Throws<ArgumentNullException>(
                    () => new TracePagedRunFinalizer(null, history));
                Assert.Throws<ArgumentNullException>(
                    () => new TracePagedRunFinalizer(new TraceLaneDrainer(lanes), null));
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static TraceLaneSetProfile Profile(
            int laneIndexCapacity, int pageSize, int pageCount)
        {
            return new TraceLaneSetProfile(
                MaxPayloadLength,
                NormalDrainMaxRecordCount,
                pageSize,
                pageCount,
                new[]
                {
                    new TraceLaneSettings(
                        TraceLaneEventMask.None.With(KindA), 64, laneIndexCapacity),
                    new TraceLaneSettings(
                        TraceLaneEventMask.None.With(KindB), 64, laneIndexCapacity),
                });
        }

        private static TracePagedRunFinalizer Finalizer(
            TraceLaneSet lanes, TracePagedHistory history)
        {
            return new TracePagedRunFinalizer(new TraceLaneDrainer(lanes), history);
        }

        private static void Write(TraceLaneWriter writer, TraceEventType kind, params byte[] payload)
        {
            Assert.That(TryWrite(writer, kind, payload), Is.True);
        }

        private static bool TryWrite(TraceLaneWriter writer, TraceEventType kind, params byte[] payload)
        {
            fixed (byte* pinned = payload)
            {
                return writer.TryWrite(kind, pinned, payload.Length);
            }
        }

        /// <summary>
        /// Walks the committed bytes of each page the way a reader would - a
        /// length, a kind, a payload, then the next - and keeps what it finds
        /// so the test can check it afterwards.
        /// </summary>
        private sealed class ReadRecords : ITraceCommittedPageVisitor
        {
            private readonly List<TraceEventType> _kinds = new List<TraceEventType>();
            private readonly List<byte[]> _payloads = new List<byte[]>();

            internal int Count => _kinds.Count;

            public void Visit(int pageOrdinal, byte* committedBytes, int committedByteCount)
            {
                int offset = 0;
                while (offset < committedByteCount)
                {
                    int recordLength = *(int*)(committedBytes + offset);
                    TraceEventType kind = (TraceEventType)(*(int*)(
                        committedBytes + offset + TraceRecordHeader.LengthFieldSize));
                    int payloadLength = recordLength - TraceRecordHeader.RecordKindSize;

                    byte[] payload = new byte[payloadLength];
                    byte* source = committedBytes + offset + TraceRecordHeader.Bytes;
                    for (int i = 0; i < payloadLength; i++)
                    {
                        payload[i] = source[i];
                    }

                    _kinds.Add(kind);
                    _payloads.Add(payload);
                    offset += TraceRecordHeader.LengthFieldSize + recordLength;
                }

                Assert.That(
                    offset, Is.EqualTo(committedByteCount),
                    "the records must end exactly where the page's committed bytes do");
            }

            internal void AssertRecord(int record, TraceEventType kind, byte[] payload)
            {
                Assert.That(_kinds.Count, Is.GreaterThan(record), "record " + record + " is not there");
                Assert.That(_kinds[record], Is.EqualTo(kind), "record " + record + " has another kind");
                Assert.That(
                    _payloads[record].Length, Is.EqualTo(payload.Length),
                    "record " + record + " has another payload length");

                for (int i = 0; i < payload.Length; i++)
                {
                    Assert.That(
                        _payloads[record][i], Is.EqualTo(payload[i]),
                        "record " + record + " byte " + i + " differs");
                }
            }
        }
    }
}
