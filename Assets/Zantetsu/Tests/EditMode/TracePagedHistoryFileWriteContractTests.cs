using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for saving a finished Run's trace as version 1 bytes:
    /// what the header says, which pages are written, and what is left out.
    /// </summary>
    /// <remarks>
    /// There is no reader yet, so the saved bytes are checked at fixed offsets
    /// with a few little-endian helpers in this fixture. Nothing here is a
    /// general parser, and nothing in the product reads a saved file.
    /// </remarks>
    public unsafe class TracePagedHistoryFileWriteContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;
        private const TraceEventType KindB = TraceEventType.TaskStarted;

        private const int MaxPayloadLength = 16;
        private const int NormalDrainMaxRecordCount = 64;

        // -------------------------------------------------------------------
        // The header
        // -------------------------------------------------------------------

        [Test]
        public void AHistoryWithNothingInItIsStillAValidFile()
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 2))
            {
                TracePagedRunResult result = run.Finish();
                byte[] saved = Save(result, out long written);

                Assert.That(written, Is.EqualTo((long)TracePagedHistoryFileFormat.HeaderBytes));
                Assert.That(saved.Length, Is.EqualTo(TracePagedHistoryFileFormat.HeaderBytes));

                AssertMagicAndVersion(saved);
                Assert.That(PageCount(saved), Is.EqualTo(0));
                Assert.That(CommittedByteTotal(saved), Is.EqualTo(0L));
                Assert.That(CommittedRecordCount(saved), Is.EqualTo(0L));
                Assert.That(LaneDropCount(saved), Is.EqualTo(0L));
                Assert.That(HistoryDropCount(saved), Is.EqualTo(0L));
                Assert.That(Integrity(saved), Is.EqualTo((int)TraceIntegrityState.Complete));
            }
        }

        [Test]
        public void TheHeaderIsLittleEndianAndAlwaysTheSameLength()
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 2))
            {
                run.Write(0, KindA, 1, 2);
                byte[] saved = Save(run.Finish(), out long _);

                // The version is one, written least significant byte first.
                Assert.That(
                    Bytes(saved, TracePagedHistoryFileFormat.VersionOffset, 4),
                    Is.EqualTo(new byte[] { 1, 0, 0, 0 }));

                // The header says how long it is, and that is where the first
                // page's entry begins.
                Assert.That(
                    ReadInt32(saved, TracePagedHistoryFileFormat.HeaderByteLengthOffset),
                    Is.EqualTo(TracePagedHistoryFileFormat.HeaderBytes));
                Assert.That(TracePagedHistoryFileFormat.HeaderBytes, Is.EqualTo(56));

                // One page, as four bytes with the low one first.
                Assert.That(
                    Bytes(saved, TracePagedHistoryFileFormat.PageCountOffset, 4),
                    Is.EqualTo(new byte[] { 1, 0, 0, 0 }));

                // One record, as eight bytes with the low one first.
                Assert.That(
                    Bytes(saved, TracePagedHistoryFileFormat.CommittedRecordCountOffset, 8),
                    Is.EqualTo(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }));
            }
        }

        // -------------------------------------------------------------------
        // The pages
        // -------------------------------------------------------------------

        [Test]
        public void OnePagesOrdinalLengthAndBytesAreAllThere()
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 2))
            {
                run.Write(0, KindA, 1, 2, 3);
                run.Write(1, KindB, 4);

                byte[] saved = Save(run.Finish(), out long written);
                SavedPages pages = ReadPages(saved);

                Assert.That(pages.Count, Is.EqualTo(1));
                Assert.That(pages.Ordinal(0), Is.EqualTo(0));
                Assert.That(pages.Bytes(0).Length, Is.EqualTo(Framed(3) + Framed(1)));
                Assert.That(written, Is.EqualTo((long)saved.Length));

                // And the records in those bytes are the ones that went in, in
                // the order the drain handed them over.
                SavedRecords records = ReadRecords(pages);
                Assert.That(records.Count, Is.EqualTo(2));
                records.AssertRecord(0, KindA, new byte[] { 1, 2, 3 });
                records.AssertRecord(1, KindB, new byte[] { 4 });
            }
        }

        [Test]
        public void SeveralPagesComeOutInOrder_WithNoUnusedTailAndNoEmptyPage()
        {
            // Each record takes 24 bytes of a 32-byte page, so every record
            // starts a page and leaves 8 bytes of tail behind.
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 5))
            {
                run.Write(0, KindA, new byte[16]);
                run.Write(1, KindB, new byte[16]);
                run.Write(0, KindA, new byte[16]);

                byte[] saved = Save(run.Finish(), out long written);
                SavedPages pages = ReadPages(saved);

                Assert.That(pages.Count, Is.EqualTo(3), "the two pages nothing reached are not in the file");
                Assert.That(pages.Ordinal(0), Is.EqualTo(0));
                Assert.That(pages.Ordinal(1), Is.EqualTo(1));
                Assert.That(pages.Ordinal(2), Is.EqualTo(2));

                for (int page = 0; page < pages.Count; page++)
                {
                    Assert.That(
                        pages.Bytes(page).Length, Is.EqualTo(Framed(16)),
                        "page " + page + " should carry its one record and none of its tail");
                }

                // Nothing else is in the file: header, three page entries, and
                // three records' worth of bytes.
                Assert.That(
                    written,
                    Is.EqualTo(
                        (long)TracePagedHistoryFileFormat.HeaderBytes
                        + (3 * TracePagedHistoryFileFormat.PageMetadataBytes)
                        + (3 * Framed(16))));
                Assert.That(saved.Length, Is.EqualTo((int)written));

                SavedRecords records = ReadRecords(pages);
                records.AssertRecord(0, KindA, new byte[16]);
                records.AssertRecord(1, KindB, new byte[16]);
                records.AssertRecord(2, KindA, new byte[16]);
            }
        }

        [Test]
        public void TheHeadersByteTotalIsExactlyWhatThePagesCarry()
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 4))
            {
                run.Write(0, KindA, new byte[16]);
                run.Write(1, KindB, new byte[8]);
                run.Write(0, KindA, new byte[2]);

                byte[] saved = Save(run.Finish(), out long _);
                SavedPages pages = ReadPages(saved);

                long carried = 0;
                for (int page = 0; page < pages.Count; page++)
                {
                    carried += pages.Bytes(page).Length;
                }

                Assert.That(CommittedByteTotal(saved), Is.EqualTo(carried));
                Assert.That(PageCount(saved), Is.EqualTo(pages.Count));
            }
        }

        // -------------------------------------------------------------------
        // What was lost
        // -------------------------------------------------------------------

        [Test]
        public void ALaneDropIsSavedOnItsOwn()
        {
            using (Run run = new Run(laneIndexCapacity: 1, pageSize: 128, pageCount: 2))
            {
                run.Write(0, KindA, 1);
                Assert.That(run.TryWrite(0, KindA, 2), Is.False, "the lane is full");

                byte[] saved = Save(run.Finish(), out long _);

                Assert.That(LaneDropCount(saved), Is.EqualTo(1L));
                Assert.That(HistoryDropCount(saved), Is.EqualTo(0L));
                Assert.That(CommittedRecordCount(saved), Is.EqualTo(1L));
                Assert.That(Integrity(saved), Is.EqualTo((int)TraceIntegrityState.Incomplete));
            }
        }

        [Test]
        public void AHistoryDropIsSavedOnItsOwn()
        {
            // A 32-byte page takes three records of one payload byte and no
            // more, and there is only one page.
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 1))
            {
                for (int record = 0; record < 5; record++)
                {
                    run.Write(0, KindA, (byte)record);
                }

                byte[] saved = Save(run.Finish(), out long _);

                Assert.That(LaneDropCount(saved), Is.EqualTo(0L));
                Assert.That(HistoryDropCount(saved), Is.EqualTo(2L));
                Assert.That(CommittedRecordCount(saved), Is.EqualTo(3L));
                Assert.That(Integrity(saved), Is.EqualTo((int)TraceIntegrityState.Incomplete));

                SavedRecords records = ReadRecords(ReadPages(saved));
                Assert.That(records.Count, Is.EqualTo(3));
                records.AssertRecord(0, KindA, new byte[] { 0 });
                records.AssertRecord(2, KindA, new byte[] { 2 });
            }
        }

        // -------------------------------------------------------------------
        // How it writes
        // -------------------------------------------------------------------

        [Test]
        public void NoSingleWriteIsBiggerThanTheScratchBuffer()
        {
            const int Records = 400;

            // A page far bigger than the scratch buffer, filled right up.
            using (Run run = new Run(laneIndexCapacity: 512, pageSize: 16384, pageCount: 2))
            {
                for (int record = 0; record < Records; record++)
                {
                    run.Write(0, KindA, new byte[16]);
                }

                RecordingStream stream = new RecordingStream();
                long written = TracePagedHistoryFileWriter.Write(stream, run.Finish());

                Assert.That(
                    stream.Length, Is.GreaterThan((long)TracePagedHistoryFileWriter.ScratchByteLength),
                    "this page must be bigger than one bufferful, or the test proves nothing");
                Assert.That(written, Is.EqualTo(stream.Length));
                Assert.That(
                    stream.LargestWrite,
                    Is.LessThanOrEqualTo(TracePagedHistoryFileWriter.ScratchByteLength),
                    "a page must go out a bufferful at a time, however big it is");
            }
        }

        [Test]
        public void TheWriterLeavesTheStreamOpen()
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 2))
            {
                run.Write(0, KindA, 1, 2);

                RecordingStream stream = new RecordingStream();
                TracePagedHistoryFileWriter.Write(stream, run.Finish());

                Assert.That(stream.Closed, Is.False, "the stream is the caller's to close");
                Assert.That(stream.CanWrite, Is.True);
            }
        }

        [Test]
        public void AStreamFailureComesBackAsItIs()
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 4))
            {
                run.Write(0, KindA, new byte[16]);
                run.Write(1, KindB, new byte[16]);

                IOException failure = new IOException("no room on the device");
                FailingStream stream = new FailingStream(failure, failOnWrite: 2);

                TracePagedRunResult result = run.Finish();
                IOException caught = Assert.Throws<IOException>(
                    () => TracePagedHistoryFileWriter.Write(stream, result));

                Assert.That(caught, Is.SameAs(failure), "the stream's own failure comes back");
                Assert.That(
                    stream.Closed, Is.False,
                    "a failed write does not close the caller's stream either");
            }
        }

        [Test]
        public void WritingTheSameResultTwiceGivesTheSameBytes()
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 4))
            {
                run.Write(0, KindA, new byte[16]);
                run.Write(1, KindB, 7, 8);
                run.Write(0, KindA, 9);

                TracePagedRunResult result = run.Finish();
                byte[] first = Save(result, out long firstLength);
                byte[] second = Save(result, out long secondLength);

                Assert.That(secondLength, Is.EqualTo(firstLength));
                Assert.That(second, Is.EqualTo(first), "the same finished Run saves the same bytes");
            }
        }

        [Test]
        public void AWriterNeedsAStreamItCanWriteTo()
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 2))
            {
                TracePagedRunResult result = run.Finish();
                Assert.Throws<ArgumentNullException>(
                    () => TracePagedHistoryFileWriter.Write(null, result));

                using (MemoryStream stream = new MemoryStream(new byte[16], writable: false))
                {
                    Assert.Throws<ArgumentException>(
                        () => TracePagedHistoryFileWriter.Write(stream, result));
                }
            }
        }

        // -------------------------------------------------------------------
        // Helpers: one Run, built by hand
        // -------------------------------------------------------------------

        private static int Framed(int payloadLength)
        {
            return TraceRecordHeader.LengthFieldSize
                + TraceRecordHeader.RecordKindSize
                + payloadLength;
        }

        /// <summary>
        /// Two lanes, a history, and a drainer, put together the way a Run
        /// would and finished once.
        /// </summary>
        private sealed class Run : IDisposable
        {
            private readonly TraceLaneSet _lanes;
            private readonly TracePagedHistory _history;
            private readonly TraceLaneDrainer _drainer;

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
                            TraceLaneEventMask.None.With(KindA), 8192, laneIndexCapacity),
                        new TraceLaneSettings(
                            TraceLaneEventMask.None.With(KindB), 8192, laneIndexCapacity),
                    });

                _lanes = new TraceLaneSet(profile);
                _history = new TracePagedHistory(profile);
                _drainer = new TraceLaneDrainer(_lanes);
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

            internal TracePagedRunResult Finish()
            {
                return TracePagedRunResult.Finish(_drainer, _history);
            }

            public void Dispose()
            {
                _history.Dispose();
                _lanes.Dispose();
            }
        }

        private static byte[] Save(TracePagedRunResult result, out long written)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                written = TracePagedHistoryFileWriter.Write(stream, result);
                return stream.ToArray();
            }
        }

        // -------------------------------------------------------------------
        // Helpers: reading the saved bytes at fixed offsets
        // -------------------------------------------------------------------

        private static void AssertMagicAndVersion(byte[] saved)
        {
            // Written out here rather than read from the product, so a change
            // to what a file starts with cannot agree with itself.
            byte[] expectedMagic =
            {
                (byte)'Z', (byte)'T', (byte)'R', (byte)'C',
                (byte)'H', (byte)'I', (byte)'S', (byte)'T',
            };

            Assert.That(Bytes(saved, 0, 8), Is.EqualTo(expectedMagic));
            Assert.That(TracePagedHistoryFileFormat.MagicByteLength, Is.EqualTo(8));
            Assert.That(
                ReadInt32(saved, TracePagedHistoryFileFormat.VersionOffset),
                Is.EqualTo(TracePagedHistoryFileFormat.Version));
        }

        private static byte[] Bytes(byte[] saved, int offset, int length)
        {
            byte[] slice = new byte[length];
            Array.Copy(saved, offset, slice, 0, length);
            return slice;
        }

        private static int ReadInt32(byte[] saved, int offset)
        {
            return saved[offset]
                | (saved[offset + 1] << 8)
                | (saved[offset + 2] << 16)
                | (saved[offset + 3] << 24);
        }

        private static long ReadInt64(byte[] saved, int offset)
        {
            long low = (uint)ReadInt32(saved, offset);
            long high = (uint)ReadInt32(saved, offset + 4);
            return low | (high << 32);
        }

        private static int PageCount(byte[] saved) =>
            ReadInt32(saved, TracePagedHistoryFileFormat.PageCountOffset);

        private static int Integrity(byte[] saved) =>
            ReadInt32(saved, TracePagedHistoryFileFormat.IntegrityStateOffset);

        private static long CommittedRecordCount(byte[] saved) =>
            ReadInt64(saved, TracePagedHistoryFileFormat.CommittedRecordCountOffset);

        private static long LaneDropCount(byte[] saved) =>
            ReadInt64(saved, TracePagedHistoryFileFormat.LaneDropCountOffset);

        private static long HistoryDropCount(byte[] saved) =>
            ReadInt64(saved, TracePagedHistoryFileFormat.HistoryDropCountOffset);

        private static long CommittedByteTotal(byte[] saved) =>
            ReadInt64(saved, TracePagedHistoryFileFormat.CommittedByteTotalOffset);

        /// <summary>
        /// Walks the page entries at their fixed offsets. Small enough to be a
        /// test helper rather than a reader.
        /// </summary>
        private static SavedPages ReadPages(byte[] saved)
        {
            SavedPages pages = new SavedPages();
            int offset = ReadInt32(saved, TracePagedHistoryFileFormat.HeaderByteLengthOffset);
            int count = PageCount(saved);

            for (int page = 0; page < count; page++)
            {
                int ordinal = ReadInt32(saved, offset + TracePagedHistoryFileFormat.PageOrdinalOffset);
                int length = ReadInt32(saved, offset + TracePagedHistoryFileFormat.PageByteLengthOffset);
                offset += TracePagedHistoryFileFormat.PageMetadataBytes;

                pages.Add(ordinal, Bytes(saved, offset, length));
                offset += length;
            }

            Assert.That(offset, Is.EqualTo(saved.Length), "the file ends where the last page does");
            return pages;
        }

        private static SavedRecords ReadRecords(SavedPages pages)
        {
            SavedRecords records = new SavedRecords();
            for (int page = 0; page < pages.Count; page++)
            {
                byte[] bytes = pages.Bytes(page);
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int recordLength = ReadInt32(bytes, offset);
                    TraceEventType kind = (TraceEventType)ReadInt32(
                        bytes, offset + TraceRecordHeader.LengthFieldSize);
                    int payloadLength = recordLength - TraceRecordHeader.RecordKindSize;

                    records.Add(kind, Bytes(bytes, offset + TraceRecordHeader.Bytes, payloadLength));
                    offset += TraceRecordHeader.LengthFieldSize + recordLength;
                }

                Assert.That(
                    offset, Is.EqualTo(bytes.Length),
                    "a page's records must end exactly where its saved bytes do");
            }

            return records;
        }

        private sealed class SavedPages
        {
            private readonly List<int> _ordinals = new List<int>();
            private readonly List<byte[]> _bytes = new List<byte[]>();

            internal int Count => _ordinals.Count;

            internal void Add(int ordinal, byte[] bytes)
            {
                _ordinals.Add(ordinal);
                _bytes.Add(bytes);
            }

            internal int Ordinal(int page) => _ordinals[page];

            internal byte[] Bytes(int page) => _bytes[page];
        }

        private sealed class SavedRecords
        {
            private readonly List<TraceEventType> _kinds = new List<TraceEventType>();
            private readonly List<byte[]> _payloads = new List<byte[]>();

            internal int Count => _kinds.Count;

            internal void Add(TraceEventType kind, byte[] payload)
            {
                _kinds.Add(kind);
                _payloads.Add(payload);
            }

            internal void AssertRecord(int record, TraceEventType kind, byte[] payload)
            {
                Assert.That(_kinds.Count, Is.GreaterThan(record), "record " + record + " is not there");
                Assert.That(_kinds[record], Is.EqualTo(kind), "record " + record + " has another kind");
                Assert.That(
                    _payloads[record], Is.EqualTo(payload), "record " + record + " has another payload");
            }
        }

        // -------------------------------------------------------------------
        // Helpers: streams that watch what the writer does
        // -------------------------------------------------------------------

        private class RecordingStream : Stream
        {
            private readonly MemoryStream _inner = new MemoryStream();

            internal int LargestWrite { get; private set; }

            internal bool Closed { get; private set; }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => !Closed;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }

            internal byte[] ToArray() => _inner.ToArray();

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count > LargestWrite)
                {
                    LargestWrite = count;
                }

                _inner.Write(buffer, offset, count);
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Close()
            {
                Closed = true;
                base.Close();
            }
        }

        private sealed class FailingStream : RecordingStream
        {
            private readonly Exception _failure;
            private readonly int _failOnWrite;

            private int _writes;

            internal FailingStream(Exception failure, int failOnWrite)
            {
                _failure = failure;
                _failOnWrite = failOnWrite;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_writes == _failOnWrite)
                {
                    throw _failure;
                }

                _writes++;
                base.Write(buffer, offset, count);
            }
        }
    }
}
