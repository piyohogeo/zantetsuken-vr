using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for reading a saved trace back: what a good file gives
    /// the destination, and what a file that does not add up is refused for.
    /// </summary>
    /// <remarks>
    /// The good files here are made by the writer, so the two halves are held
    /// to the same format. Broken files are made by taking a good one and
    /// changing a few bytes at their fixed offsets - there is no sweep of
    /// every way a file could be wrong, and nothing here is a fuzzer.
    /// </remarks>
    public unsafe class TracePagedHistoryFileReadContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;
        private const TraceEventType KindB = TraceEventType.TaskStarted;

        private const int MaxPayloadLength = 16;
        private const int NormalDrainMaxRecordCount = 64;

        // -------------------------------------------------------------------
        // A file that reads back
        // -------------------------------------------------------------------

        [Test]
        public void EveryRecordComesBackInOrderWithItsKindAndPayload()
        {
            byte[] saved;
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 5))
            {
                // Each of these takes 24 bytes of a 32-byte page, so the pages
                // change under them; the empty one shares a page with its
                // neighbour.
                run.Write(0, KindA, new byte[16]);
                run.Write(1, KindB, 7, 8);
                run.Write(0, KindA);
                run.Write(1, KindB, new byte[16]);
                saved = run.Save();
            }

            RecordingDestination destination = new RecordingDestination();
            TracePagedHistoryFileSummary summary = Read(saved, destination);

            Assert.That(summary.CommittedRecordCount, Is.EqualTo(4L));
            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Complete));
            Assert.That(summary.PageCount, Is.GreaterThan(1), "this file should span pages");
            Assert.That(destination.Count, Is.EqualTo(4));

            destination.AssertRecord(0, KindA, new byte[16]);
            destination.AssertRecord(1, KindB, new byte[] { 7, 8 });
            destination.AssertRecord(2, KindA, new byte[0]);
            destination.AssertRecord(3, KindB, new byte[16]);
        }

        [Test]
        public void TheDropCountsAndTheVerdictComeBackAsTheyWereSaved()
        {
            byte[] complete;
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 128, pageCount: 2))
            {
                run.Write(0, KindA, 1);
                complete = run.Save();
            }

            TracePagedHistoryFileSummary summary = Read(complete, new RecordingDestination());
            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Complete));
            Assert.That(summary.LaneDropCount, Is.EqualTo(0L));
            Assert.That(summary.HistoryDropCount, Is.EqualTo(0L));

            byte[] laneDropped;
            using (Run run = new Run(laneIndexCapacity: 1, pageSize: 128, pageCount: 2))
            {
                run.Write(0, KindA, 1);
                Assert.That(run.TryWrite(0, KindA, 2), Is.False, "the lane is full");
                laneDropped = run.Save();
            }

            summary = Read(laneDropped, new RecordingDestination());
            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));
            Assert.That(summary.LaneDropCount, Is.EqualTo(1L));
            Assert.That(summary.HistoryDropCount, Is.EqualTo(0L));

            byte[] historyDropped;
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 1))
            {
                for (int record = 0; record < 5; record++)
                {
                    run.Write(0, KindA, (byte)record);
                }

                historyDropped = run.Save();
            }

            RecordingDestination destination = new RecordingDestination();
            summary = Read(historyDropped, destination);
            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));
            Assert.That(summary.LaneDropCount, Is.EqualTo(0L));
            Assert.That(summary.HistoryDropCount, Is.EqualTo(2L));
            Assert.That(destination.Count, Is.EqualTo(3));
        }

        [Test]
        public void ThePayloadHandedOverIsOnlyGoodDuringTheCall()
        {
            byte[] saved;
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 128, pageCount: 2))
            {
                run.Write(0, KindA, 1, 2, 3);
                run.Write(0, KindA, 9, 9, 9);
                saved = run.Save();
            }

            // This destination keeps the pointer it was given instead of the
            // bytes, which is exactly what a destination may not do - and what
            // it kept is not what it saw.
            KeepingDestination keeping = new KeepingDestination();
            Read(saved, keeping);

            Assert.That(
                keeping.FirstPointer == keeping.SecondPointer, Is.True,
                "the same buffer is handed over each time, so nothing may be kept from it");
            Assert.That(
                keeping.CopiedFirst, Is.EqualTo(new byte[] { 1, 2, 3 }),
                "a destination that copies while it is called sees the right bytes");
        }

        [Test]
        public void AStreamThatCannotSeekAndReadsShortIsStillEnough()
        {
            byte[] saved;
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 4))
            {
                run.Write(0, KindA, 1, 2, 3);
                run.Write(1, KindB, new byte[16]);
                run.Write(0, KindA, 4);
                saved = run.Save();
            }

            RecordingDestination destination = new RecordingDestination();
            using (DribblingStream stream = new DribblingStream(saved, mostPerRead: 3))
            {
                TracePagedHistoryFileSummary summary =
                    TracePagedHistoryFileReader.Read(stream, MaxPayloadLength, destination);

                Assert.That(summary.CommittedRecordCount, Is.EqualTo(3L));
                Assert.That(stream.CanSeek, Is.False);
                Assert.That(stream.Closed, Is.False, "the stream is the caller's to close");
            }

            Assert.That(destination.Count, Is.EqualTo(3));
            destination.AssertRecord(0, KindA, new byte[] { 1, 2, 3 });
            destination.AssertRecord(1, KindB, new byte[16]);
            destination.AssertRecord(2, KindA, new byte[] { 4 });
        }

        // -------------------------------------------------------------------
        // Files that do not add up
        // -------------------------------------------------------------------

        [Test]
        public void AFileThatIsNotThisFormatOrVersionIsRefused()
        {
            byte[] saved = OneRecordFile();

            AssertRefused(Changed(saved, 0, (byte)'X'), "another mark");
            AssertRefused(
                ChangedInt32(saved, TracePagedHistoryFileFormat.VersionOffset, 2), "another version");
            AssertRefused(
                ChangedInt32(saved, TracePagedHistoryFileFormat.HeaderByteLengthOffset, 48),
                "another header length");
        }

        [Test]
        public void AFileThatStopsPartWayThroughIsRefused()
        {
            byte[] saved = OneRecordFile();

            AssertRefused(Truncated(saved, 16), "half a header");
            AssertRefused(
                Truncated(saved, TracePagedHistoryFileFormat.HeaderBytes + 4), "half a page entry");
            AssertRefused(Truncated(saved, saved.Length - 2), "half a record's payload");
        }

        [Test]
        public void AHeaderThatContradictsItselfIsRefused()
        {
            byte[] saved = OneRecordFile();

            AssertRefused(
                ChangedInt64(saved, TracePagedHistoryFileFormat.CommittedRecordCountOffset, -1L),
                "a count of less than nothing");
            AssertRefused(
                ChangedInt32(saved, TracePagedHistoryFileFormat.PageCountOffset, -1),
                "fewer than no pages");
            AssertRefused(
                ChangedInt32(saved, TracePagedHistoryFileFormat.IntegrityStateOffset, 7),
                "a verdict this format does not have");

            // Complete, but with something dropped.
            AssertRefused(
                ChangedInt64(saved, TracePagedHistoryFileFormat.LaneDropCountOffset, 3L),
                "a complete Run that dropped records");
        }

        [Test]
        public void AnIncompleteRunThatDroppedNothingIsStillRead()
        {
            // A Run can be incomplete for reasons that are not a drop, and
            // this format does not carry what those were. The reader takes the
            // verdict as it stands.
            byte[] saved = ChangedInt32(
                OneRecordFile(),
                TracePagedHistoryFileFormat.IntegrityStateOffset,
                (int)TraceIntegrityState.Incomplete);

            RecordingDestination destination = new RecordingDestination();
            TracePagedHistoryFileSummary summary = Read(saved, destination);

            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));
            Assert.That(summary.LaneDropCount, Is.EqualTo(0L));
            Assert.That(summary.HistoryDropCount, Is.EqualTo(0L));
            Assert.That(destination.Count, Is.EqualTo(1));
            destination.AssertRecord(0, KindA, new byte[] { 1, 2, 3 });
        }

        [Test]
        public void PagesOutOfOrderOrRepeatedAreRefused()
        {
            byte[] saved = TwoPageFile(out int secondPageEntryOffset);
            int firstEntry = TracePagedHistoryFileFormat.HeaderBytes;

            // The same page twice.
            AssertRefused(
                ChangedInt32(saved, secondPageEntryOffset + TracePagedHistoryFileFormat.PageOrdinalOffset, 0),
                "the same page twice");

            // And the pages the wrong way round.
            AssertRefused(
                ChangedInt32(saved, firstEntry + TracePagedHistoryFileFormat.PageOrdinalOffset, 5),
                "pages going backwards");
        }

        [Test]
        public void ARecordThatDoesNotFitItsPageOrTheCallersLimitIsRefused()
        {
            byte[] saved = OneRecordFile();
            int recordOffset =
                TracePagedHistoryFileFormat.HeaderBytes + TracePagedHistoryFileFormat.PageMetadataBytes;

            // A length that would run past the end of the page.
            AssertRefused(ChangedInt32(saved, recordOffset, 64), "a record past the end of its page");

            // A length too short to carry even the kind.
            AssertRefused(ChangedInt32(saved, recordOffset, 2), "a record without room for its kind");

            // And a payload longer than this caller will take.
            byte[] big;
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 128, pageCount: 2))
            {
                run.Write(0, KindA, new byte[16]);
                big = run.Save();
            }

            Assert.Throws<InvalidDataException>(
                () => Read(big, new RecordingDestination(), maxPayloadLength: 4),
                "a caller that will only take four bytes must not be handed sixteen");
        }

        [Test]
        public void CountsThatDisagreeWithWhatWasReadAreRefused()
        {
            byte[] saved = OneRecordFile();

            AssertRefused(
                ChangedInt64(saved, TracePagedHistoryFileFormat.CommittedRecordCountOffset, 2L),
                "more records than the pages hold");
            AssertRefused(
                ChangedInt64(saved, TracePagedHistoryFileFormat.CommittedByteTotalOffset, 999L),
                "more page bytes than the pages hold");

            // Something after the last page the file declared.
            byte[] withTail = new byte[saved.Length + 1];
            Array.Copy(saved, withTail, saved.Length);
            withTail[saved.Length] = 42;
            AssertRefused(withTail, "bytes after the last page");

            // That refusal comes after the records have already gone over, and
            // nothing takes them back: the caller keeps what it was given only
            // when a summary comes back, which here it did not.
            RecordingDestination destination = new RecordingDestination();
            Assert.Throws<InvalidDataException>(() => Read(withTail, destination));
            Assert.That(
                destination.Count, Is.EqualTo(1),
                "the record before the trouble had already been handed over");
            destination.AssertRecord(0, KindA, new byte[] { 1, 2, 3 });
        }

        // -------------------------------------------------------------------
        // What the reader does not do
        // -------------------------------------------------------------------

        [Test]
        public void TheReaderClosesNeitherTheStreamNorTheDestination()
        {
            byte[] saved = OneRecordFile();

            using (DribblingStream stream = new DribblingStream(saved, mostPerRead: 1024))
            {
                RecordingDestination destination = new RecordingDestination();
                TracePagedHistoryFileReader.Read(stream, MaxPayloadLength, destination);

                Assert.That(stream.Closed, Is.False);
                Assert.That(stream.CanRead, Is.True);
                Assert.That(destination.Disposed, Is.False, "the destination is the caller's too");
            }
        }

        [Test]
        public void ADestinationThatThrowsStopsTheReadWithItsOwnFailure()
        {
            byte[] saved;
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 128, pageCount: 2))
            {
                run.Write(0, KindA, 1);
                run.Write(0, KindA, 2);
                run.Write(0, KindA, 3);
                saved = run.Save();
            }

            InvalidOperationException failure = new InvalidOperationException("enough");
            ThrowingDestination destination = new ThrowingDestination(failure, throwOnRecord: 1);

            using (MemoryStream stream = new MemoryStream(saved, writable: false))
            {
                InvalidOperationException caught = Assert.Throws<InvalidOperationException>(
                    () => TracePagedHistoryFileReader.Read(stream, MaxPayloadLength, destination));

                Assert.That(caught, Is.SameAs(failure));
                Assert.That(
                    destination.Count, Is.EqualTo(1),
                    "nothing after the record it refused should have been handed over");
            }
        }

        [Test]
        public void AReaderNeedsAStreamAPositiveLimitAndADestination()
        {
            byte[] saved = OneRecordFile();

            Assert.Throws<ArgumentNullException>(
                () => TracePagedHistoryFileReader.Read(null, MaxPayloadLength, new RecordingDestination()));

            using (MemoryStream stream = new MemoryStream(saved, writable: false))
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => TracePagedHistoryFileReader.Read(stream, 0, new RecordingDestination()));
                Assert.Throws<ArgumentNullException>(
                    () => TracePagedHistoryFileReader.Read(stream, MaxPayloadLength, null));
            }
        }

        // -------------------------------------------------------------------
        // Helpers: making files
        // -------------------------------------------------------------------

        private static byte[] OneRecordFile()
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 2))
            {
                run.Write(0, KindA, 1, 2, 3);
                return run.Save();
            }
        }

        private static byte[] TwoPageFile(out int secondPageEntryOffset)
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 4))
            {
                // 24 bytes each in a 32-byte page, so each record has a page.
                run.Write(0, KindA, new byte[16]);
                run.Write(1, KindB, new byte[16]);

                byte[] saved = run.Save();
                int firstPageLength = ReadInt32(
                    saved,
                    TracePagedHistoryFileFormat.HeaderBytes
                        + TracePagedHistoryFileFormat.PageByteLengthOffset);

                secondPageEntryOffset = TracePagedHistoryFileFormat.HeaderBytes
                    + TracePagedHistoryFileFormat.PageMetadataBytes
                    + firstPageLength;
                return saved;
            }
        }

        /// <summary>
        /// Two lanes, a history and a drainer, finished once and saved.
        /// </summary>
        private sealed class Run : IDisposable
        {
            private readonly TraceLaneSet _lanes;
            private readonly TracePagedHistory _history;
            private readonly TracePagedRunFinalizer _finalizer;

            internal Run(int laneIndexCapacity, int pageSize, int pageCount)
            {
                TraceLaneSetProfile profile = new TraceLaneSetProfile(
                    MaxPayloadLength,
                    NormalDrainMaxRecordCount,
                    pageSize,
                    pageCount,
                    new[]
                    {
                        new TraceLaneSettings(
                            TraceLaneEventMask.None.With(KindA), 4096, laneIndexCapacity),
                        new TraceLaneSettings(
                            TraceLaneEventMask.None.With(KindB), 4096, laneIndexCapacity),
                    });

                _lanes = new TraceLaneSet(profile);
                _history = new TracePagedHistory(profile);
                _finalizer = new TracePagedRunFinalizer(new TraceLaneDrainer(_lanes), _history);
            }

            internal void Write(int ordinal, TraceEventType kind, params byte[] payload)
            {
                Assert.That(TryWrite(ordinal, kind, payload), Is.True);
            }

            internal bool TryWrite(int ordinal, TraceEventType kind, params byte[] payload)
            {
                TraceLaneWriter writer = _lanes.CreateWriter(ordinal);
                fixed (byte* pinned = payload)
                {
                    return writer.TryWrite(kind, pinned, payload.Length);
                }
            }

            internal byte[] Save()
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    TracePagedHistoryFileWriter.Write(stream, _finalizer.Finish());
                    return stream.ToArray();
                }
            }

            public void Dispose()
            {
                _history.Dispose();
                _lanes.Dispose();
            }
        }

        // -------------------------------------------------------------------
        // Helpers: reading and breaking files
        // -------------------------------------------------------------------

        private static TracePagedHistoryFileSummary Read(
            byte[] saved, ITraceRecordDestination destination, int maxPayloadLength = MaxPayloadLength)
        {
            using (MemoryStream stream = new MemoryStream(saved, writable: false))
            {
                return TracePagedHistoryFileReader.Read(stream, maxPayloadLength, destination);
            }
        }

        private static void AssertRefused(byte[] saved, string what)
        {
            Assert.Throws<InvalidDataException>(
                () => Read(saved, new RecordingDestination()),
                what + " should have been refused");
        }

        private static byte[] Changed(byte[] saved, int offset, byte value)
        {
            byte[] copy = (byte[])saved.Clone();
            copy[offset] = value;
            return copy;
        }

        private static byte[] ChangedInt32(byte[] saved, int offset, int value)
        {
            byte[] copy = (byte[])saved.Clone();
            TracePagedHistoryFileFormat.WriteInt32(copy, offset, value);
            return copy;
        }

        private static byte[] ChangedInt64(byte[] saved, int offset, long value)
        {
            byte[] copy = (byte[])saved.Clone();
            TracePagedHistoryFileFormat.WriteInt64(copy, offset, value);
            return copy;
        }

        private static byte[] Truncated(byte[] saved, int length)
        {
            byte[] copy = new byte[length];
            Array.Copy(saved, copy, length);
            return copy;
        }

        private static int ReadInt32(byte[] saved, int offset)
        {
            return saved[offset]
                | (saved[offset + 1] << 8)
                | (saved[offset + 2] << 16)
                | (saved[offset + 3] << 24);
        }

        // -------------------------------------------------------------------
        // Helpers: destinations and a stream that gives up little at a time
        // -------------------------------------------------------------------

        private class RecordingDestination : ITraceRecordDestination
        {
            private readonly List<TraceEventType> _kinds = new List<TraceEventType>();
            private readonly List<byte[]> _payloads = new List<byte[]>();

            internal int Count => _kinds.Count;

            internal bool Disposed => false;

            public virtual void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
            {
                byte[] copy = new byte[payloadLength];
                for (int i = 0; i < payloadLength; i++)
                {
                    copy[i] = payload[i];
                }

                _kinds.Add(recordKind);
                _payloads.Add(copy);
            }

            internal void AssertRecord(int record, TraceEventType kind, byte[] payload)
            {
                Assert.That(_kinds.Count, Is.GreaterThan(record), "record " + record + " never arrived");
                Assert.That(_kinds[record], Is.EqualTo(kind), "record " + record + " has another kind");
                Assert.That(
                    _payloads[record], Is.EqualTo(payload), "record " + record + " has another payload");
            }
        }

        /// <summary>
        /// Keeps the pointer it was handed, which a destination may not do, so
        /// the test can say that the buffer behind it is the reader's own and
        /// is used again.
        /// </summary>
        private sealed class KeepingDestination : ITraceRecordDestination
        {
            internal byte* FirstPointer;
            internal byte* SecondPointer;
            internal byte[] CopiedFirst;

            private int _count;

            public void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
            {
                if (_count == 0)
                {
                    FirstPointer = payload;
                    CopiedFirst = new byte[payloadLength];
                    for (int i = 0; i < payloadLength; i++)
                    {
                        CopiedFirst[i] = payload[i];
                    }
                }
                else if (_count == 1)
                {
                    SecondPointer = payload;
                }

                _count++;
            }
        }

        private sealed class ThrowingDestination : RecordingDestination
        {
            private readonly Exception _failure;
            private readonly int _throwOnRecord;

            private int _seen;

            internal ThrowingDestination(Exception failure, int throwOnRecord)
            {
                _failure = failure;
                _throwOnRecord = throwOnRecord;
            }

            public override void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
            {
                if (_seen == _throwOnRecord)
                {
                    throw _failure;
                }

                _seen++;
                base.Receive(recordKind, payload, payloadLength);
            }
        }

        /// <summary>
        /// A stream that cannot seek and never gives back more than a few
        /// bytes at a time, the way a pipe or a network stream would.
        /// </summary>
        private sealed class DribblingStream : Stream
        {
            private readonly byte[] _bytes;
            private readonly int _mostPerRead;

            private int _position;

            internal DribblingStream(byte[] bytes, int mostPerRead)
            {
                _bytes = bytes;
                _mostPerRead = mostPerRead;
            }

            internal bool Closed { get; private set; }

            public override bool CanRead => !Closed;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int left = _bytes.Length - _position;
                if (left <= 0)
                {
                    return 0;
                }

                int taken = count;
                if (taken > _mostPerRead)
                {
                    taken = _mostPerRead;
                }

                if (taken > left)
                {
                    taken = left;
                }

                Array.Copy(_bytes, _position, buffer, offset, taken);
                _position += taken;
                return taken;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override void Close()
            {
                Closed = true;
                base.Close();
            }
        }
    }
}
