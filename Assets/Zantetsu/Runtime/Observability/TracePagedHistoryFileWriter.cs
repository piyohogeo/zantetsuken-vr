using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Writes what a finished Run's trace came to into a stream, in the
    /// version 1 format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The history is walked twice: once to count the pages that have anything
    /// in them and add up their committed bytes, which is what the header
    /// needs, and once to write those pages out in order. The history not
    /// changing between the two walks is the same promise reading it already
    /// relied on - nothing is snapshotted, copied aside, or locked here.
    /// </para>
    /// <para>
    /// A page's bytes go out through one small fixed buffer, a piece at a time,
    /// so a bigger page costs no more memory than a small one. Nothing here
    /// builds an array of a page, of the records, or of the file, and no record
    /// is parsed on the way out - the bytes the history holds are the bytes
    /// that are written.
    /// </para>
    /// <para>
    /// The stream belongs to the caller: it is not created, flushed to disk,
    /// closed, or deleted here, whether the write finishes or fails. A failure
    /// from the stream comes back as it is, with nothing rolled back and
    /// nothing retried, and whatever reached the stream before it is not a
    /// finished file - only a write that returns has produced one.
    /// </para>
    /// </remarks>
    internal static unsafe class TracePagedHistoryFileWriter
    {
        /// <summary>
        /// How much of a page is copied at a time. A small fixed size: it does
        /// not grow with the page size, the profile, or how many records there
        /// are.
        /// </summary>
        internal const int ScratchByteLength = 4096;

        /// <summary>
        /// Writes the whole of one Run's saved trace and returns how many bytes
        /// that was.
        /// </summary>
        internal static long Write(Stream destination, in TracePagedRunResult result)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (!destination.CanWrite)
            {
                throw new ArgumentException("The destination cannot be written to.", nameof(destination));
            }

            PageMeasure measure = new PageMeasure();
            result.View.VisitCommittedPages(measure);

            byte[] scratch = new byte[ScratchByteLength];
            WriteHeader(destination, scratch, result, measure.PageCount, measure.CommittedByteTotal);

            result.View.VisitCommittedPages(new PageWriter(destination, scratch));

            checked
            {
                return TracePagedHistoryFileFormat.HeaderBytes
                    + ((long)measure.PageCount * TracePagedHistoryFileFormat.PageMetadataBytes)
                    + measure.CommittedByteTotal;
            }
        }

        private static void WriteHeader(
            Stream destination,
            byte[] scratch,
            in TracePagedRunResult result,
            int pageCount,
            long committedByteTotal)
        {
            Array.Clear(scratch, 0, TracePagedHistoryFileFormat.HeaderBytes);
            TracePagedHistoryFileFormat.WriteMagic(scratch, 0);

            TracePagedHistoryFileFormat.WriteInt32(
                scratch, TracePagedHistoryFileFormat.VersionOffset, TracePagedHistoryFileFormat.Version);
            TracePagedHistoryFileFormat.WriteInt32(
                scratch,
                TracePagedHistoryFileFormat.HeaderByteLengthOffset,
                TracePagedHistoryFileFormat.HeaderBytes);
            TracePagedHistoryFileFormat.WriteInt32(
                scratch, TracePagedHistoryFileFormat.PageCountOffset, pageCount);
            TracePagedHistoryFileFormat.WriteInt32(
                scratch, TracePagedHistoryFileFormat.IntegrityStateOffset, (int)result.Integrity);

            // What the history holds over the whole Run, and what was lost on
            // each side of it. What the final drain itself took is left out:
            // it depends on when the ordinary drains happened to run, and says
            // nothing about the file.
            TracePagedHistoryFileFormat.WriteInt64(
                scratch,
                TracePagedHistoryFileFormat.CommittedRecordCountOffset,
                result.HistoryCommittedRecordCount);
            TracePagedHistoryFileFormat.WriteInt64(
                scratch,
                TracePagedHistoryFileFormat.LaneDropCountOffset,
                result.FinalDrain.DropCount);
            TracePagedHistoryFileFormat.WriteInt64(
                scratch, TracePagedHistoryFileFormat.HistoryDropCountOffset, result.HistoryDropCount);
            TracePagedHistoryFileFormat.WriteInt64(
                scratch, TracePagedHistoryFileFormat.CommittedByteTotalOffset, committedByteTotal);

            destination.Write(scratch, 0, TracePagedHistoryFileFormat.HeaderBytes);
        }

        /// <summary>
        /// Counts the pages that have anything in them and adds up how much,
        /// which is what the header has to say before any of it is written.
        /// </summary>
        private sealed class PageMeasure : ITraceCommittedPageVisitor
        {
            internal int PageCount;
            internal long CommittedByteTotal;

            public void Visit(int pageOrdinal, byte* committedBytes, int committedByteCount)
            {
                checked
                {
                    PageCount++;
                    CommittedByteTotal += committedByteCount;
                }
            }
        }

        /// <summary>
        /// Writes each page out: which page it was, how much of it was
        /// committed, and then those bytes, a scratch buffer at a time.
        /// </summary>
        private sealed class PageWriter : ITraceCommittedPageVisitor
        {
            private readonly Stream _destination;
            private readonly byte[] _scratch;

            internal PageWriter(Stream destination, byte[] scratch)
            {
                _destination = destination;
                _scratch = scratch;
            }

            public void Visit(int pageOrdinal, byte* committedBytes, int committedByteCount)
            {
                TracePagedHistoryFileFormat.WriteInt32(
                    _scratch, TracePagedHistoryFileFormat.PageOrdinalOffset, pageOrdinal);
                TracePagedHistoryFileFormat.WriteInt32(
                    _scratch, TracePagedHistoryFileFormat.PageByteLengthOffset, committedByteCount);
                _destination.Write(_scratch, 0, TracePagedHistoryFileFormat.PageMetadataBytes);

                int written = 0;
                while (written < committedByteCount)
                {
                    int chunk = committedByteCount - written;
                    if (chunk > _scratch.Length)
                    {
                        chunk = _scratch.Length;
                    }

                    Marshal.Copy((IntPtr)(committedBytes + written), _scratch, 0, chunk);
                    _destination.Write(_scratch, 0, chunk);
                    written += chunk;
                }
            }
        }
    }
}
