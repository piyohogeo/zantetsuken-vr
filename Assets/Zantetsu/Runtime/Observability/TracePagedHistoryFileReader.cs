using System;
using System.IO;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// What a saved trace says about the Run it came from, taken from its
    /// header once the whole file has been read and found consistent.
    /// </summary>
    /// <remarks>
    /// There are no records in here, and no pages: they went to the
    /// destination as the file was read. Nothing here holds the stream, the
    /// destination, a payload, or anything saying who owned what. The version
    /// is not kept either - only version 1 is read at all.
    /// </remarks>
    internal readonly struct TracePagedHistoryFileSummary
    {
        private readonly int _pageCount;
        private readonly TraceIntegrityState _integrity;
        private readonly long _committedRecordCount;
        private readonly long _laneDropCount;
        private readonly long _historyDropCount;
        private readonly long _committedByteTotal;

        internal TracePagedHistoryFileSummary(
            int pageCount,
            TraceIntegrityState integrity,
            long committedRecordCount,
            long laneDropCount,
            long historyDropCount,
            long committedByteTotal)
        {
            _pageCount = pageCount;
            _integrity = integrity;
            _committedRecordCount = committedRecordCount;
            _laneDropCount = laneDropCount;
            _historyDropCount = historyDropCount;
            _committedByteTotal = committedByteTotal;
        }

        /// <summary>How many pages the file carried.</summary>
        internal int PageCount => _pageCount;

        /// <summary>Whether the Run kept everything it was given.</summary>
        internal TraceIntegrityState Integrity => _integrity;

        /// <summary>Records the history held over the whole Run.</summary>
        internal long CommittedRecordCount => _committedRecordCount;

        /// <summary>Records the lanes could not take.</summary>
        internal long LaneDropCount => _laneDropCount;

        /// <summary>Records the history had no room for.</summary>
        internal long HistoryDropCount => _historyDropCount;

        /// <summary>Bytes of page data the file carried.</summary>
        internal long CommittedByteTotal => _committedByteTotal;
    }

    /// <summary>
    /// Reads a saved trace back once, from front to back, handing each record
    /// to a destination as it comes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole file goes past in one pass through two small buffers: one for
    /// the header, the page entries and the record headers, and one for a
    /// payload, no bigger than the longest record the caller says it will
    /// accept. Nothing here grows with how many pages, records, or bytes the
    /// file has - no page is materialised, no list of records is built, there
    /// is no index and no way to jump about. Nothing is cached, pooled,
    /// registered, or done on another thread.
    /// </para>
    /// <para>
    /// The stream is the caller's: it is not closed, seeked, rewound, or read
    /// twice, so a stream that cannot seek is enough, and a read that comes
    /// back short is simply read from again until the bytes asked for have
    /// arrived or the file ends. A payload is handed over in the reader's own
    /// buffer and is good only while <see cref="ITraceRecordDestination.Receive"/>
    /// is running, which is the contract that boundary already had.
    /// </para>
    /// <para>
    /// The header is checked before any of it is used, and each record is
    /// checked against its page and the caller's limit before it is handed
    /// over - so a record that would run past the end of its page or past
    /// what the caller will take never reaches the destination. Some things
    /// are only knowable at the end, though: whether the records and bytes
    /// read match what the header promised, and whether anything follows the
    /// last page. Those are checked after the records have gone over.
    /// </para>
    /// <para>
    /// Nothing is taken back when they fail. Records already handed to the
    /// destination stay where they went, and there is no undo, transaction, or
    /// second pass to prevent it; what a failure does guarantee is that no
    /// summary is returned. So a caller keeps what it was given only when a
    /// summary comes back, and treats anything it accumulated before a failure
    /// as a prefix of a file that turned out not to be one. A failure raised
    /// by the stream or by the destination is passed on exactly as it is, and
    /// is no different in this respect. Bad data is refused as bad data, once,
    /// with no catalogue of reasons.
    /// </para>
    /// </remarks>
    internal static unsafe class TracePagedHistoryFileReader
    {
        /// <summary>
        /// Reads one saved trace, handing every record to
        /// <paramref name="destination"/> in the order it was saved, and
        /// returns what the file's header said.
        /// </summary>
        internal static TracePagedHistoryFileSummary Read(
            Stream source, int maxPayloadLength, ITraceRecordDestination destination)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (!source.CanRead)
            {
                throw new ArgumentException("The source cannot be read from.", nameof(source));
            }

            if (maxPayloadLength <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxPayloadLength), maxPayloadLength, "Max payload length must be positive.");
            }

            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            byte[] fixedFields = new byte[TracePagedHistoryFileFormat.HeaderBytes];
            byte[] payload = new byte[maxPayloadLength];

            TracePagedHistoryFileSummary summary = ReadHeader(source, fixedFields);

            long bytesSeen = 0L;
            long recordsSeen = 0L;
            int previousOrdinal = -1;

            for (int page = 0; page < summary.PageCount; page++)
            {
                ReadExactly(source, fixedFields, 0, TracePagedHistoryFileFormat.PageMetadataBytes);

                int ordinal = ReadInt32(fixedFields, TracePagedHistoryFileFormat.PageOrdinalOffset);
                int pageByteLength =
                    ReadInt32(fixedFields, TracePagedHistoryFileFormat.PageByteLengthOffset);

                if (ordinal <= previousOrdinal)
                {
                    // Pages are saved in order and a page appears once. They
                    // need not run one after another, because a page nothing
                    // reached is not saved at all.
                    throw Invalid("The saved pages are out of order.");
                }

                if (pageByteLength <= 0)
                {
                    throw Invalid("A saved page carries nothing.");
                }

                previousOrdinal = ordinal;
                checked
                {
                    bytesSeen += pageByteLength;
                }

                recordsSeen = ReadPage(
                    source, fixedFields, payload, maxPayloadLength, pageByteLength,
                    destination, recordsSeen);
            }

            if (recordsSeen != summary.CommittedRecordCount)
            {
                throw Invalid("The file holds a different number of records than it says.");
            }

            if (bytesSeen != summary.CommittedByteTotal)
            {
                throw Invalid("The file's pages hold a different number of bytes than it says.");
            }

            if (source.Read(fixedFields, 0, 1) != 0)
            {
                throw Invalid("There is more in the file than the pages it declared.");
            }

            return summary;
        }

        private static TracePagedHistoryFileSummary ReadHeader(Stream source, byte[] header)
        {
            ReadExactly(source, header, 0, TracePagedHistoryFileFormat.HeaderBytes);

            if (header[0] != (byte)'Z'
                || header[1] != (byte)'T'
                || header[2] != (byte)'R'
                || header[3] != (byte)'C'
                || header[4] != (byte)'H'
                || header[5] != (byte)'I'
                || header[6] != (byte)'S'
                || header[7] != (byte)'T')
            {
                throw Invalid("This is not a saved trace.");
            }

            int version = ReadInt32(header, TracePagedHistoryFileFormat.VersionOffset);
            if (version != TracePagedHistoryFileFormat.Version)
            {
                throw Invalid("This saved trace is of another version.");
            }

            int headerByteLength = ReadInt32(header, TracePagedHistoryFileFormat.HeaderByteLengthOffset);
            if (headerByteLength != TracePagedHistoryFileFormat.HeaderBytes)
            {
                throw Invalid("The header is not the length this version has.");
            }

            int pageCount = ReadInt32(header, TracePagedHistoryFileFormat.PageCountOffset);
            int integrity = ReadInt32(header, TracePagedHistoryFileFormat.IntegrityStateOffset);
            long committedRecordCount =
                ReadInt64(header, TracePagedHistoryFileFormat.CommittedRecordCountOffset);
            long laneDropCount = ReadInt64(header, TracePagedHistoryFileFormat.LaneDropCountOffset);
            long historyDropCount = ReadInt64(header, TracePagedHistoryFileFormat.HistoryDropCountOffset);
            long committedByteTotal =
                ReadInt64(header, TracePagedHistoryFileFormat.CommittedByteTotalOffset);

            if (pageCount < 0
                || committedRecordCount < 0L
                || laneDropCount < 0L
                || historyDropCount < 0L
                || committedByteTotal < 0L)
            {
                throw Invalid("The header counts something as less than nothing.");
            }

            if (integrity != (int)TraceIntegrityState.Complete
                && integrity != (int)TraceIntegrityState.Incomplete)
            {
                throw Invalid("The header's integrity state is not one this format has.");
            }

            // A Run that says it kept everything cannot also say it dropped
            // something. The other way round is not a contradiction: a Run can
            // be incomplete for reasons that are not a drop and that this
            // format does not carry, so Incomplete with nothing dropped is
            // read as it stands.
            if (integrity == (int)TraceIntegrityState.Complete
                && (laneDropCount != 0L || historyDropCount != 0L))
            {
                throw Invalid("The header says the Run was complete but also that records were lost.");
            }

            return new TracePagedHistoryFileSummary(
                pageCount,
                (TraceIntegrityState)integrity,
                committedRecordCount,
                laneDropCount,
                historyDropCount,
                committedByteTotal);
        }

        /// <summary>
        /// Reads one page's records and hands each of them over, returning how
        /// many records have been seen in the file so far.
        /// </summary>
        private static long ReadPage(
            Stream source,
            byte[] recordHeader,
            byte[] payload,
            int maxPayloadLength,
            int pageByteLength,
            ITraceRecordDestination destination,
            long recordsSeen)
        {
            int offset = 0;
            while (offset < pageByteLength)
            {
                if (pageByteLength - offset < TraceRecordHeader.Bytes)
                {
                    throw Invalid("A record's header runs past the end of its page.");
                }

                ReadExactly(source, recordHeader, 0, TraceRecordHeader.Bytes);
                int recordLength = ReadInt32(recordHeader, 0);
                int kind = ReadInt32(recordHeader, TraceRecordHeader.LengthFieldSize);

                if (recordLength < TraceRecordHeader.RecordKindSize)
                {
                    throw Invalid("A record is too short to carry its kind.");
                }

                int payloadLength = recordLength - TraceRecordHeader.RecordKindSize;
                if (payloadLength > maxPayloadLength)
                {
                    throw Invalid("A record is longer than the caller said it would accept.");
                }

                int framedLength = TraceRecordHeader.LengthFieldSize + recordLength;
                if (framedLength > pageByteLength - offset)
                {
                    throw Invalid("A record runs past the end of its page.");
                }

                if (payloadLength > 0)
                {
                    ReadExactly(source, payload, 0, payloadLength);
                }

                // The payload is this reader's own buffer, and it is only the
                // destination's for as long as this call runs.
                fixed (byte* bytes = payload)
                {
                    destination.Receive(
                        (TraceEventType)kind, payloadLength > 0 ? bytes : null, payloadLength);
                }

                offset += framedLength;
                checked
                {
                    recordsSeen++;
                }
            }

            return recordsSeen;
        }

        /// <summary>
        /// Reads exactly as many bytes as asked for, going back to the stream
        /// for as long as it keeps giving some but not all of them.
        /// </summary>
        private static void ReadExactly(Stream source, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int taken = source.Read(buffer, offset + read, count - read);
                if (taken <= 0)
                {
                    throw Invalid("The file ends part way through.");
                }

                read += taken;
            }
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
        }

        private static long ReadInt64(byte[] buffer, int offset)
        {
            long low = (uint)ReadInt32(buffer, offset);
            long high = (uint)ReadInt32(buffer, offset + 4);
            return low | (high << 32);
        }

        private static InvalidDataException Invalid(string what)
        {
            return new InvalidDataException(what);
        }
    }
}
