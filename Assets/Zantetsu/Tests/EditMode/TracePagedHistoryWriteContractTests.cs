using System;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for putting records into the history pages: how much
    /// each record commits, when a record moves to the next page, and what
    /// happens when there is no page left.
    /// </summary>
    /// <remarks>
    /// What is actually in the pages is not read back here - nothing hands out
    /// a pointer into a page yet, and reading committed bytes belongs to the
    /// view that comes after the writing stops. So these cases check the
    /// framing arithmetic, how much was committed, which page it went to, and
    /// what was dropped; they do not confirm the payload bytes themselves.
    /// </remarks>
    public unsafe class TracePagedHistoryWriteContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;
        private const TraceEventType KindB = TraceEventType.TaskStarted;

        private const int MaxPayloadLength = 16;
        private const int NormalDrainMaxRecordCount = 4;

        /// <summary>The bytes a record of this payload length takes in a page.</summary>
        private static int Framed(int payloadLength)
        {
            return TraceRecordHeader.LengthFieldSize
                + TraceRecordHeader.RecordKindSize
                + payloadLength;
        }

        // -------------------------------------------------------------------
        // What one record commits
        // -------------------------------------------------------------------

        [Test]
        public void AnEmptyPayloadIsARecordOfItsHeaderAlone()
        {
            using (TracePagedHistory history = History(64, 2))
            {
                history.Receive(KindA, null, 0);

                Assert.That(
                    history.CommittedByteCountOf(0), Is.EqualTo((long)TraceRecordHeader.Bytes),
                    "an empty record still commits its length and its kind");
                Assert.That(history.CommittedRecordCount, Is.EqualTo(1L));
                Assert.That(history.DropCount, Is.EqualTo(0L));
            }
        }

        [Test]
        public void AnOrdinaryRecordMovesThePagesBytesAndTheHistorysRecords()
        {
            using (TracePagedHistory history = History(64, 2))
            {
                Write(history, KindA, 1, 2, 3, 4);

                Assert.That(history.CommittedByteCountOf(0), Is.EqualTo((long)Framed(4)));
                Assert.That(history.CommittedRecordCount, Is.EqualTo(1L));
                Assert.That(history.CommittedByteCountOf(1), Is.EqualTo(0L));
            }
        }

        [Test]
        public void RecordsGoOneAfterAnotherIntoTheSamePage()
        {
            using (TracePagedHistory history = History(64, 2))
            {
                Write(history, KindA, 1, 2, 3, 4);
                Write(history, KindB, 5, 6);
                history.Receive(KindA, null, 0);

                Assert.That(
                    history.CommittedByteCountOf(0),
                    Is.EqualTo((long)(Framed(4) + Framed(2) + Framed(0))));
                Assert.That(history.CommittedRecordCount, Is.EqualTo(3L));
                Assert.That(
                    history.CommittedByteCountOf(1), Is.EqualTo(0L),
                    "nothing should have reached the second page yet");
            }
        }

        // -------------------------------------------------------------------
        // Moving to the next page
        // -------------------------------------------------------------------

        [Test]
        public void ARecordThatWillNotFitStartsTheNextPage_AndTheOldTailIsNotCommitted()
        {
            // Two records of 24 bytes fill 48 of 64; a third needs 24 and only
            // 16 are left, so it starts the next page and those 16 bytes stay
            // uncommitted for good.
            using (TracePagedHistory history = History(64, 2))
            {
                Write(history, KindA, new byte[16]);
                Write(history, KindA, new byte[16]);
                Assert.That(history.CommittedByteCountOf(0), Is.EqualTo(48L));

                Write(history, KindB, new byte[16]);

                Assert.That(
                    history.CommittedByteCountOf(0), Is.EqualTo(48L),
                    "the tail the record could not use must not be committed");
                Assert.That(
                    history.CommittedByteCountOf(1), Is.EqualTo((long)Framed(16)),
                    "the record must start at the beginning of the next page");
                Assert.That(history.CommittedRecordCount, Is.EqualTo(3L));
                Assert.That(history.DropCount, Is.EqualTo(0L));
            }
        }

        [Test]
        public void ARecordThatEndsExactlyAtThePageEndFits()
        {
            // 24 + 24 + 16 is exactly 64.
            using (TracePagedHistory history = History(64, 2))
            {
                Write(history, KindA, new byte[16]);
                Write(history, KindA, new byte[16]);
                Write(history, KindB, new byte[8]);

                Assert.That(history.CommittedByteCountOf(0), Is.EqualTo(64L));
                Assert.That(history.CommittedRecordCount, Is.EqualTo(3L));
                Assert.That(
                    history.CommittedByteCountOf(1), Is.EqualTo(0L),
                    "a record that ends on the page boundary must not have moved on");
                Assert.That(history.DropCount, Is.EqualTo(0L));
            }
        }

        // -------------------------------------------------------------------
        // When there is no page left
        // -------------------------------------------------------------------

        [Test]
        public void OnTheLastPage_ARecordThatWillNotFitIsDroppedAndChangesNothing()
        {
            using (TracePagedHistory history = History(64, 1))
            {
                Write(history, KindA, new byte[16]);
                Write(history, KindA, new byte[16]);
                Assert.That(history.CommittedByteCountOf(0), Is.EqualTo(48L));

                Write(history, KindB, new byte[16]);

                Assert.That(
                    history.CommittedByteCountOf(0), Is.EqualTo(48L),
                    "a dropped record commits nothing");
                Assert.That(
                    history.CommittedRecordCount, Is.EqualTo(2L),
                    "a dropped record is not one of the history's records");
                Assert.That(history.DropCount, Is.EqualTo(1L));
            }
        }

        [Test]
        public void AfterThatDrop_AShorterRecordThatStillFitsIsTaken()
        {
            using (TracePagedHistory history = History(64, 1))
            {
                Write(history, KindA, new byte[16]);
                Write(history, KindA, new byte[16]);
                Write(history, KindB, new byte[16]);
                Assert.That(history.DropCount, Is.EqualTo(1L));

                // Sixteen bytes are still free, which is exactly one record of
                // eight payload bytes.
                Write(history, KindB, new byte[8]);

                Assert.That(history.CommittedByteCountOf(0), Is.EqualTo(64L));
                Assert.That(history.CommittedRecordCount, Is.EqualTo(3L));
                Assert.That(
                    history.DropCount, Is.EqualTo(1L),
                    "taking a record afterwards is not another drop");
            }
        }

        [Test]
        public void EveryRecordWithNoRoomIsCountedOnce_AndNoneOfThemIsCommitted()
        {
            using (TracePagedHistory history = History(64, 1))
            {
                Write(history, KindA, new byte[16]);
                Write(history, KindA, new byte[16]);

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    Write(history, KindB, new byte[16]);
                }

                Assert.That(history.DropCount, Is.EqualTo(5L));
                Assert.That(history.CommittedRecordCount, Is.EqualTo(2L));
                Assert.That(history.CommittedByteCountOf(0), Is.EqualTo(48L));
            }
        }

        // -------------------------------------------------------------------
        // What is a mistake rather than a full history
        // -------------------------------------------------------------------

        [Test]
        public void ALengthTheHistoryCannotFrameIsRefusedBeforeAnythingIsWritten()
        {
            using (TracePagedHistory history = History(64, 2))
            {
                Write(history, KindA, new byte[4]);
                long committed = history.CommittedByteCountOf(0);

                byte[] payload = new byte[MaxPayloadLength + 1];
                fixed (byte* pinned = payload)
                {
                    byte* bytes = pinned;
                    Assert.Throws<ArgumentOutOfRangeException>(
                        () => history.Receive(KindA, bytes, -1));
                    Assert.Throws<ArgumentOutOfRangeException>(
                        () => history.Receive(KindA, bytes, MaxPayloadLength + 1));
                }

                Assert.Throws<ArgumentNullException>(() => history.Receive(KindA, null, 4));

                Assert.That(
                    history.CommittedByteCountOf(0), Is.EqualTo(committed),
                    "a refused record must not have been written");
                Assert.That(history.CommittedRecordCount, Is.EqualTo(1L));
                Assert.That(
                    history.DropCount, Is.EqualTo(0L),
                    "a mistake in the calling code is not a full history");
            }
        }

        // -------------------------------------------------------------------
        // Lifetime
        // -------------------------------------------------------------------

        [Test]
        public void AReleasedHistoryTakesNothingMore_AndReleasingItTwiceIsHarmless()
        {
            TracePagedHistory history = History(64, 2);
            try
            {
                Write(history, KindA, 1, 2);
                Assert.That(history.AllocatedBytes, Is.GreaterThan(0L));
            }
            finally
            {
                history.Dispose();
            }

            Assert.That(history.AllocatedBytes, Is.EqualTo(0L));
            Assert.Throws<ObjectDisposedException>(() => history.Receive(KindA, null, 0));
            Assert.Throws<ObjectDisposedException>(() => { long _ = history.DropCount; });
            Assert.Throws<ObjectDisposedException>(
                () => { long _ = history.CommittedRecordCount; });

            history.Dispose();
            Assert.That(history.AllocatedBytes, Is.EqualTo(0L));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static TracePagedHistory History(int pageSize, int pageCount)
        {
            return new TracePagedHistory(new TraceLaneSetProfile(
                MaxPayloadLength,
                NormalDrainMaxRecordCount,
                pageSize,
                pageCount,
                new[]
                {
                    new TraceLaneSettings(TraceLaneEventMask.None.With(KindA), 64, 4),
                }));
        }

        private static void Write(TracePagedHistory history, TraceEventType kind, params byte[] payload)
        {
            fixed (byte* pinned = payload)
            {
                history.Receive(kind, pinned, payload.Length);
            }
        }
    }
}
