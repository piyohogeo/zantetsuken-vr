using System;
using System.Collections.Generic;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for reading a history back after the writing has
    /// stopped: what the committed bytes of each page actually contain, and
    /// what the drained records look like once they have been through the
    /// lanes and the drainer.
    /// </summary>
    /// <remarks>
    /// The walk over the bytes here is a few lines in this fixture, not a
    /// reader: it reads a length, a kind and a payload and steps on. Nothing
    /// in the product parses a record yet.
    /// </remarks>
    public unsafe class TracePagedHistoryViewContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;
        private const TraceEventType KindB = TraceEventType.TaskStarted;
        private const TraceEventType KindC = TraceEventType.TaskCompleted;

        private const int MaxPayloadLength = 16;
        private const int NormalDrainMaxRecordCount = 2;

        // -------------------------------------------------------------------
        // What is actually in the pages
        // -------------------------------------------------------------------

        [Test]
        public void EachRecordIsItsLengthThenItsKindThenItsPayload()
        {
            using (TracePagedHistory history = History(64, 2))
            {
                Write(history, KindA, 1, 2, 3, 4);
                Write(history, KindB, 9);
                history.Receive(KindC, null, 0);

                ReadRecords records = Read(history);

                Assert.That(records.Count, Is.EqualTo(3));
                records.AssertRecord(0, page: 0, offset: 0, kind: KindA, payload: new byte[] { 1, 2, 3, 4 });
                records.AssertRecord(
                    1, page: 0, offset: Framed(4), kind: KindB, payload: new byte[] { 9 });
                records.AssertRecord(
                    2, page: 0, offset: Framed(4) + Framed(1), kind: KindC, payload: new byte[0]);

                // The length in front of a record counts the kind and the
                // payload, and the next record starts that far past it.
                Assert.That(
                    records.RecordLengthOf(0),
                    Is.EqualTo(TraceRecordHeader.RecordKindSize + 4));
                Assert.That(
                    records.OffsetOf(1) - records.OffsetOf(0),
                    Is.EqualTo(TraceRecordHeader.LengthFieldSize + records.RecordLengthOf(0)));
                Assert.That(
                    records.RecordLengthOf(2),
                    Is.EqualTo(TraceRecordHeader.RecordKindSize),
                    "an empty record is its kind and nothing more");
            }
        }

        [Test]
        public void ChangingTheCallersBytesAfterwardsDoesNotChangeTheHistory()
        {
            using (TracePagedHistory history = History(64, 2))
            {
                byte[] payload = { 1, 2, 3, 4 };
                fixed (byte* pinned = payload)
                {
                    history.Receive(KindA, pinned, payload.Length);
                }

                // The caller is free to reuse its own memory the moment the
                // call comes back.
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = 99;
                }

                ReadRecords records = Read(history);
                Assert.That(records.Count, Is.EqualTo(1));
                records.AssertRecord(0, page: 0, offset: 0, kind: KindA, payload: new byte[] { 1, 2, 3, 4 });
            }
        }

        [Test]
        public void ARecordOnTheNextPageStartsAtItsBeginning_AndTheOldTailIsNotShown()
        {
            using (TracePagedHistory history = History(64, 3))
            {
                // 24 and 24 of 64, then a third that needs 24 with 16 left.
                Write(history, KindA, new byte[16]);
                Write(history, KindA, new byte[16]);
                Write(history, KindB, new byte[16]);

                CountingVisitor pages = new CountingVisitor();
                history.CreateView().VisitCommittedPages(pages);

                Assert.That(pages.Pages, Is.EqualTo(new[] { 0, 1 }));
                Assert.That(
                    pages.Lengths[0], Is.EqualTo(48),
                    "the 16 bytes of tail the third record could not use are not shown");
                Assert.That(pages.Lengths[1], Is.EqualTo(Framed(16)));

                ReadRecords records = Read(history);
                Assert.That(records.Count, Is.EqualTo(3));
                records.AssertRecord(2, page: 1, offset: 0, kind: KindB, payload: new byte[16]);
            }
        }

        [Test]
        public void APageWithNothingInItIsNotShownAtAll()
        {
            using (TracePagedHistory history = History(64, 4))
            {
                CountingVisitor empty = new CountingVisitor();
                Assert.That(
                    history.CreateView().VisitCommittedPages(empty), Is.EqualTo(0),
                    "a history with nothing in it shows nothing");
                Assert.That(empty.Pages, Is.Empty);

                Write(history, KindA, 1, 2);

                CountingVisitor pages = new CountingVisitor();
                Assert.That(history.CreateView().VisitCommittedPages(pages), Is.EqualTo(1));
                Assert.That(
                    pages.Pages, Is.EqualTo(new[] { 0 }),
                    "the three pages nothing has reached are not shown");
            }
        }

        [Test]
        public void ThePagesComeInPageOrder()
        {
            using (TracePagedHistory history = History(32, 4))
            {
                // Each record takes 24 of 32, so every record starts a page.
                Write(history, KindA, new byte[16]);
                Write(history, KindB, new byte[16]);
                Write(history, KindC, new byte[16]);

                CountingVisitor pages = new CountingVisitor();
                Assert.That(history.CreateView().VisitCommittedPages(pages), Is.EqualTo(3));
                Assert.That(pages.Pages, Is.EqualTo(new[] { 0, 1, 2 }));

                ReadRecords records = Read(history);
                records.AssertRecord(0, page: 0, offset: 0, kind: KindA, payload: new byte[16]);
                records.AssertRecord(1, page: 1, offset: 0, kind: KindB, payload: new byte[16]);
                records.AssertRecord(2, page: 2, offset: 0, kind: KindC, payload: new byte[16]);
            }
        }

        // -------------------------------------------------------------------
        // A visitor that gives up
        // -------------------------------------------------------------------

        [Test]
        public void AVisitorThatThrowsLeavesTheHistoryExactlyAsItWas()
        {
            using (TracePagedHistory history = History(32, 4))
            {
                Write(history, KindA, new byte[16]);
                Write(history, KindB, new byte[16]);

                long records = history.CommittedRecordCount;
                long firstPage = history.CommittedByteCountOf(0);
                long secondPage = history.CommittedByteCountOf(1);

                InvalidOperationException thrown = new InvalidOperationException("stop here");
                ThrowingVisitor visitor = new ThrowingVisitor(thrown, throwOnPage: 1);

                InvalidOperationException caught = Assert.Throws<InvalidOperationException>(
                    () => history.CreateView().VisitCommittedPages(visitor));
                Assert.That(caught, Is.SameAs(thrown), "the visitor's own failure comes back");

                Assert.That(history.CommittedRecordCount, Is.EqualTo(records));
                Assert.That(history.CommittedByteCountOf(0), Is.EqualTo(firstPage));
                Assert.That(history.CommittedByteCountOf(1), Is.EqualTo(secondPage));

                // And what the pages hold is still there to be read.
                ReadRecords after = Read(history);
                Assert.That(after.Count, Is.EqualTo(2));
                after.AssertRecord(0, page: 0, offset: 0, kind: KindA, payload: new byte[16]);
                after.AssertRecord(1, page: 1, offset: 0, kind: KindB, payload: new byte[16]);
            }
        }

        // -------------------------------------------------------------------
        // Lifetime
        // -------------------------------------------------------------------

        [Test]
        public void AReleasedHistoryCanBeNeitherViewedNorWalked()
        {
            TracePagedHistory history = History(64, 2);
            TracePagedHistoryView view;
            try
            {
                Write(history, KindA, 1, 2);
                view = history.CreateView();
                Assert.That(view.VisitCommittedPages(new CountingVisitor()), Is.EqualTo(1));
            }
            finally
            {
                history.Dispose();
            }

            Assert.Throws<ObjectDisposedException>(() => history.CreateView());
            Assert.Throws<ObjectDisposedException>(
                () => view.VisitCommittedPages(new CountingVisitor()));

            TracePagedHistoryView never = default;
            Assert.Throws<InvalidOperationException>(
                () => never.VisitCommittedPages(new CountingVisitor()));
        }

        // -------------------------------------------------------------------
        // Through the lanes and the drainer
        // -------------------------------------------------------------------

        [Test]
        public void RecordsDrainedFromTheLanesAreInTheHistoryInDrainOrder()
        {
            TraceLaneSetProfile profile = Profile(128, 4);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                TraceLaneWriter first = lanes.CreateWriter(0);
                TraceLaneWriter second = lanes.CreateWriter(1);

                Assert.That(LaneWrite(first, KindA, 1), Is.True);
                Assert.That(LaneWrite(first, KindA, 2), Is.True);
                Assert.That(LaneWrite(second, KindB, 11), Is.True);
                Assert.That(LaneWrite(second, KindB, 12), Is.True);

                TraceLaneDrainer drainer = new TraceLaneDrainer(lanes);

                // One ordinary drain, then the rest - the history takes them
                // exactly as the drainer hands them over.
                Assert.That(drainer.Drain(history), Is.EqualTo(NormalDrainMaxRecordCount));
                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(history);

                Assert.That(result.RecordCount, Is.EqualTo(2L));
                Assert.That(result.DropCount, Is.EqualTo(0L), "no lane had to drop anything");
                Assert.That(history.CommittedRecordCount, Is.EqualTo(4L));
                Assert.That(history.DropCount, Is.EqualTo(0L));

                // Round-robin order, which is what the drainer handed over.
                ReadRecords records = Read(history);
                Assert.That(records.Count, Is.EqualTo(4));
                records.AssertKindAndPayload(0, KindA, new byte[] { 1 });
                records.AssertKindAndPayload(1, KindB, new byte[] { 11 });
                records.AssertKindAndPayload(2, KindA, new byte[] { 2 });
                records.AssertKindAndPayload(3, KindB, new byte[] { 12 });
            }
        }

        [Test]
        public void AHistoryWithNoRoomLeftStillLetsTheLanesEmptyThemselves()
        {
            // One page of 32 bytes takes three records of one payload byte -
            // nine bytes each - and has no room for a fourth.
            TraceLaneSetProfile profile = Profile(32, 1);
            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                TraceLaneWriter writer = lanes.CreateWriter(0);
                for (int record = 0; record < 5; record++)
                {
                    Assert.That(LaneWrite(writer, KindA, (byte)record), Is.True);
                }

                TraceLaneDrainer drainer = new TraceLaneDrainer(lanes);
                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(history);

                // The lanes gave up everything they had, and the drain result
                // still means what it always meant: lane records and lane
                // drops.
                Assert.That(result.RecordCount, Is.EqualTo(5L));
                Assert.That(
                    result.DropCount, Is.EqualTo(0L),
                    "the lanes dropped nothing; the history is the one that ran out");
                Assert.That(
                    lanes.TryPeek(0, out TraceLaneIndexEntry _, out byte* _), Is.False,
                    "every record was consumed even though the history could not keep it");

                Assert.That(history.CommittedRecordCount, Is.EqualTo(3L));
                Assert.That(
                    history.DropCount, Is.EqualTo(2L),
                    "the records the history had no room for are its own drops");

                ReadRecords records = Read(history);
                Assert.That(records.Count, Is.EqualTo(3));
                records.AssertKindAndPayload(0, KindA, new byte[] { 0 });
                records.AssertKindAndPayload(1, KindA, new byte[] { 1 });
                records.AssertKindAndPayload(2, KindA, new byte[] { 2 });
            }
        }

        [Test]
        public void ALaneDropAndAHistoryDropAreCountedApart()
        {
            // One index slot per lane, so the lane itself refuses the second
            // record; the history has room for everything it is given.
            TraceLaneSetProfile profile = new TraceLaneSetProfile(
                MaxPayloadLength,
                NormalDrainMaxRecordCount,
                128,
                2,
                new[]
                {
                    new TraceLaneSettings(TraceLaneEventMask.None.With(KindA), 64, 1),
                });

            using (TraceLaneSet lanes = new TraceLaneSet(profile))
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                TraceLaneWriter writer = lanes.CreateWriter(0);
                Assert.That(LaneWrite(writer, KindA, 1), Is.True);
                Assert.That(LaneWrite(writer, KindA, 2), Is.False, "the lane is full");
                Assert.That(LaneWrite(writer, KindA, 3), Is.False);

                TraceLaneDrainer drainer = new TraceLaneDrainer(lanes);
                TraceFinalDrainResult result = drainer.DrainToEndAndSeal(history);

                Assert.That(result.RecordCount, Is.EqualTo(1L));
                Assert.That(result.DropCount, Is.EqualTo(2L), "both refusals were the lane's");
                Assert.That(
                    history.DropCount, Is.EqualTo(0L),
                    "the history was never asked for room it did not have");
                Assert.That(history.CommittedRecordCount, Is.EqualTo(1L));
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static int Framed(int payloadLength)
        {
            return TraceRecordHeader.LengthFieldSize
                + TraceRecordHeader.RecordKindSize
                + payloadLength;
        }

        private static TraceLaneSetProfile Profile(int pageSize, int pageCount)
        {
            return new TraceLaneSetProfile(
                MaxPayloadLength,
                NormalDrainMaxRecordCount,
                pageSize,
                pageCount,
                new[]
                {
                    new TraceLaneSettings(TraceLaneEventMask.None.With(KindA), 64, 8),
                    new TraceLaneSettings(TraceLaneEventMask.None.With(KindB), 64, 8),
                });
        }

        private static TracePagedHistory History(int pageSize, int pageCount)
        {
            return new TracePagedHistory(Profile(pageSize, pageCount));
        }

        private static void Write(TracePagedHistory history, TraceEventType kind, params byte[] payload)
        {
            fixed (byte* pinned = payload)
            {
                history.Receive(kind, pinned, payload.Length);
            }
        }

        private static bool LaneWrite(TraceLaneWriter writer, TraceEventType kind, params byte[] payload)
        {
            fixed (byte* pinned = payload)
            {
                return writer.TryWrite(kind, pinned, payload.Length);
            }
        }

        private static ReadRecords Read(TracePagedHistory history)
        {
            ReadRecords records = new ReadRecords();
            history.CreateView().VisitCommittedPages(records);
            return records;
        }

        /// <summary>
        /// Notes which pages were shown and how much of each, without looking
        /// at what is in them.
        /// </summary>
        private sealed class CountingVisitor : ITraceCommittedPageVisitor
        {
            internal readonly List<int> Pages = new List<int>();
            internal readonly List<int> Lengths = new List<int>();

            public void Visit(int pageOrdinal, byte* committedBytes, int committedByteCount)
            {
                Pages.Add(pageOrdinal);
                Lengths.Add(committedByteCount);
            }
        }

        private sealed class ThrowingVisitor : ITraceCommittedPageVisitor
        {
            private readonly Exception _failure;
            private readonly int _throwOnPage;

            internal ThrowingVisitor(Exception failure, int throwOnPage)
            {
                _failure = failure;
                _throwOnPage = throwOnPage;
            }

            public void Visit(int pageOrdinal, byte* committedBytes, int committedByteCount)
            {
                if (pageOrdinal == _throwOnPage)
                {
                    throw _failure;
                }
            }
        }

        /// <summary>
        /// Walks the committed bytes of each page the way a reader would:
        /// a length, a kind, a payload, then on to the next. This is the only
        /// thing here that knows the framing, and it keeps what it finds so
        /// the test can check it after the walk.
        /// </summary>
        private sealed class ReadRecords : ITraceCommittedPageVisitor
        {
            private readonly List<int> _pages = new List<int>();
            private readonly List<int> _offsets = new List<int>();
            private readonly List<int> _recordLengths = new List<int>();
            private readonly List<TraceEventType> _kinds = new List<TraceEventType>();
            private readonly List<byte[]> _payloads = new List<byte[]>();

            internal int Count => _pages.Count;

            public void Visit(int pageOrdinal, byte* committedBytes, int committedByteCount)
            {
                int offset = 0;
                while (offset < committedByteCount)
                {
                    int recordLength = *(int*)(committedBytes + offset);
                    Assert.That(
                        recordLength, Is.GreaterThanOrEqualTo(TraceRecordHeader.RecordKindSize),
                        "a record must at least carry its kind");
                    Assert.That(
                        offset + TraceRecordHeader.LengthFieldSize + recordLength,
                        Is.LessThanOrEqualTo(committedByteCount),
                        "a record must not run past what the page committed");

                    TraceEventType kind =
                        (TraceEventType)(*(int*)(committedBytes + offset + TraceRecordHeader.LengthFieldSize));
                    int payloadLength = recordLength - TraceRecordHeader.RecordKindSize;
                    byte[] payload = new byte[payloadLength];
                    byte* source = committedBytes + offset + TraceRecordHeader.Bytes;
                    for (int i = 0; i < payloadLength; i++)
                    {
                        payload[i] = source[i];
                    }

                    _pages.Add(pageOrdinal);
                    _offsets.Add(offset);
                    _recordLengths.Add(recordLength);
                    _kinds.Add(kind);
                    _payloads.Add(payload);

                    offset += TraceRecordHeader.LengthFieldSize + recordLength;
                }

                Assert.That(
                    offset, Is.EqualTo(committedByteCount),
                    "the records must end exactly where the page's committed bytes do");
            }

            internal int OffsetOf(int record) => _offsets[record];

            internal int RecordLengthOf(int record) => _recordLengths[record];

            internal void AssertRecord(
                int record, int page, int offset, TraceEventType kind, byte[] payload)
            {
                Assert.That(_pages.Count, Is.GreaterThan(record), "record " + record + " is not there");
                Assert.That(_pages[record], Is.EqualTo(page), "record " + record + " is on another page");
                Assert.That(
                    _offsets[record], Is.EqualTo(offset),
                    "record " + record + " starts somewhere else");
                AssertKindAndPayload(record, kind, payload);
            }

            internal void AssertKindAndPayload(int record, TraceEventType kind, byte[] payload)
            {
                Assert.That(_pages.Count, Is.GreaterThan(record), "record " + record + " is not there");
                Assert.That(_kinds[record], Is.EqualTo(kind), "record " + record + " has another kind");
                Assert.That(
                    _recordLengths[record],
                    Is.EqualTo(TraceRecordHeader.RecordKindSize + payload.Length),
                    "record " + record + " has another length");
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
