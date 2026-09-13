using System;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The fixed set of history pages one Run keeps its drained trace records
    /// in, with how much of each page is committed and how many records the
    /// history holds altogether.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything is taken once, after the profile has been validated and the
    /// whole Run's storage counted, and it is all or none: a region that
    /// cannot be taken leaves nothing behind. The pages are never grown,
    /// shrunk, added to, or moved while the Run is going.
    /// </para>
    /// <para>
    /// Records are put in one after another through <see cref="Receive"/>,
    /// each framed with its length and kind, and a record is never split
    /// across pages: one that will not fit what is left of a page starts the
    /// next page instead, leaving the tail of the old one unused and
    /// uncommitted. A record that will not fit and has no page left to go to
    /// is dropped without waiting, and the counts and the position in the
    /// current page are left exactly as they were - so a shorter record after
    /// it can still be taken.
    /// </para>
    /// <para>
    /// Only one thread may put records in, one at a time; that is the caller's
    /// to keep, and nothing here checks it, locks, or waits. A record becomes
    /// visible by being counted: the page's committed byte count is written
    /// after the record is in the page, and the history's record count after
    /// that, both as ordinary stores. Reading those counts while records are
    /// still going in is not something this type supports - reading is for
    /// after the writing has stopped.
    /// </para>
    /// <para>
    /// What is held is only the pages, one committed byte count per page, one
    /// committed record count and one drop count for the history - no page
    /// objects, no index, no page state, no reference count, lease,
    /// generation, or live snapshot.
    /// The producer lanes are not this type's and are not released with it;
    /// stopping whatever writes to or reads from the history is not its job
    /// either. <see cref="Dispose"/> gives the region back once, and a second
    /// call does nothing.
    /// </para>
    /// <para>
    /// The page region sits behind the counts in the same block, and neither a
    /// pointer into it nor any array behind it is handed out as a property.
    /// What is in the pages is read back through <see cref="CreateView"/>,
    /// which walks the committed part of each page once the writing has
    /// stopped and hands it over only for as long as the visitor is running.
    /// </para>
    /// </remarks>
    internal sealed unsafe class TracePagedHistory : ITraceRecordDestination, IDisposable
    {
        /// <summary>
        /// The block in front of the per-page counts, holding the history's own
        /// committed record count. One cache line, so that counter does not
        /// share a line with the page counts.
        /// </summary>
        private const int MetadataBlockBytes = 64;

        private const int CommittedRecordCountOffset = 0;

        private const int DropCountOffset = CommittedRecordCountOffset + sizeof(long);

        private readonly int _pageSize;
        private readonly int _pageCount;
        private readonly int _maxPayloadLength;
        private readonly long _blockBytes;

        private byte* _block;
        private long* _committedRecordCount;
        private long* _dropCount;
        private long* _committedByteCounts;
        private byte* _pages;

        /// <summary>
        /// The page records are going into. Only the one writer touches it,
        /// and it only ever moves forward.
        /// </summary>
        private int _currentPage;

        /// <summary>
        /// Takes the pages and the counts the profile describes, in one piece.
        /// </summary>
        internal TracePagedHistory(TraceLaneSetProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            _pageSize = profile.HistoryPageSize;
            _pageCount = profile.HistoryPageCount;
            _maxPayloadLength = profile.MaxPayloadLength;
            _blockBytes = profile.HistoryStorageBytes;

            _block = (byte*)UnsafeUtility.Malloc(_blockBytes, MetadataBlockBytes, Allocator.Persistent);
            if (_block == null)
            {
                // Nothing was taken, so there is nothing to give back.
                throw new OutOfMemoryException("The trace history's pages could not be allocated.");
            }

            // Every count starts at nothing, and so does every page.
            UnsafeUtility.MemClear(_block, _blockBytes);
            _committedRecordCount = (long*)(_block + CommittedRecordCountOffset);
            _dropCount = (long*)(_block + DropCountOffset);
            _committedByteCounts = (long*)(_block + MetadataBlockBytes);
            _pages = _block + MetadataBlockBytes + PageCountBytesFor(_pageCount);
        }

        /// <summary>
        /// How many unmanaged bytes a history of this shape takes: the counts
        /// in front, then one committed byte count per page, then the pages.
        /// The profile adds this up before anything exists, and this is what
        /// it adds.
        /// </summary>
        /// <exception cref="OverflowException">
        /// The shape describes more bytes than can be counted.
        /// </exception>
        internal static long StorageBytesFor(int pageSize, int pageCount)
        {
            checked
            {
                return MetadataBlockBytes + PageCountBytesFor(pageCount) + ((long)pageSize * pageCount);
            }
        }

        private static long PageCountBytesFor(int pageCount)
        {
            checked
            {
                return (long)sizeof(long) * pageCount;
            }
        }

        internal int PageSize => _pageSize;

        internal int PageCount => _pageCount;

        /// <summary>
        /// Bytes this history holds, which is nothing once it has been
        /// released. Managed objects are not part of it.
        /// </summary>
        internal long AllocatedBytes => _block == null ? 0L : _blockBytes;

        /// <summary>Records the history holds altogether.</summary>
        internal long CommittedRecordCount
        {
            get
            {
                RequireLive();
                return Interlocked.Read(ref *_committedRecordCount);
            }
        }

        /// <summary>
        /// Records the history had no room left for. Saturating, never
        /// wrapping, and not broken down by page, event, or reason.
        /// </summary>
        internal long DropCount
        {
            get
            {
                RequireLive();
                return Interlocked.Read(ref *_dropCount);
            }
        }

        /// <summary>How much of one page has been committed.</summary>
        internal long CommittedByteCountOf(int page)
        {
            RequireLive();
            if ((uint)page >= (uint)_pageCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(page), page, "There is no page with this number.");
            }

            return Interlocked.Read(ref _committedByteCounts[page]);
        }

        /// <summary>
        /// Puts one record into the history: its length, its kind, and then
        /// its payload, all in one piece inside a single page.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For the one writer, on its own thread, one record at a time. A
        /// record that will not fit what is left of the current page goes at
        /// the start of the next one; a record with no page left to go to is
        /// dropped, counted once, and changes nothing else.
        /// </para>
        /// <para>
        /// An empty payload is a record like any other. A negative length, a
        /// length longer than the Run allows, or no payload where there should
        /// be one is a mistake in the calling code, not a full history: it is
        /// refused before anything is written and is not counted as a drop.
        /// </para>
        /// </remarks>
        public void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
        {
            RequireLive();

            if (payloadLength < 0 || payloadLength > _maxPayloadLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payloadLength),
                    payloadLength,
                    "A record must be between zero bytes and the longest the Run allows.");
            }

            if (payloadLength > 0 && payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            int recordLength = TraceRecordHeader.RecordKindSize + payloadLength;
            int framedLength = TraceRecordHeader.LengthFieldSize + recordLength;

            long offset = _committedByteCounts[_currentPage];
            if (offset + framedLength > _pageSize)
            {
                if (_currentPage + 1 >= _pageCount)
                {
                    // Nowhere left to put it. The page keeps the room it still
                    // has, so a shorter record after this one can still go in.
                    CountDrop();
                    return;
                }

                // The tail of this page stays as it is - unused, and not
                // counted as committed.
                _currentPage++;
                offset = _committedByteCounts[_currentPage];
            }

            byte* cursor = _pages + ((long)_currentPage * _pageSize) + offset;
            *(TraceRecordHeader*)cursor = new TraceRecordHeader
            {
                RecordLength = recordLength,
                RecordKind = recordKind,
            };

            if (payloadLength > 0)
            {
                UnsafeUtility.MemCpy(cursor + TraceRecordHeader.Bytes, payload, payloadLength);
            }

            // The record is in the page before either count says so: the
            // page's bytes first, then the history's records.
            _committedByteCounts[_currentPage] = offset + framedLength;
            *_committedRecordCount = *_committedRecordCount + 1L;
        }

        /// <summary>
        /// Adds one to the drop count, stopping at the largest value rather
        /// than turning over.
        /// </summary>
        private void CountDrop()
        {
            long current = *_dropCount;
            if (current == long.MaxValue)
            {
                return;
            }

            *_dropCount = current + 1L;
        }

        /// <summary>
        /// A way of reading back what the pages hold, for after the writing
        /// has stopped.
        /// </summary>
        /// <remarks>
        /// The writer having stopped, and this history staying alive while the
        /// view is used, are the caller's to keep - nothing here proves either.
        /// Making one copies nothing and takes nothing.
        /// </remarks>
        internal TracePagedHistoryView CreateView()
        {
            RequireLive();
            return new TracePagedHistoryView(this);
        }

        /// <summary>
        /// Hands the committed run of each page that has one to the visitor,
        /// in page order, and returns how many pages that was.
        /// </summary>
        /// <remarks>
        /// Only the bytes between the start of a page and its committed byte
        /// count go over - never the tail a record could not use, the room
        /// nothing has been written to, or the counts in front of the pages.
        /// The pointer is good for the length of the call and no longer, and a
        /// visitor that throws stops the walk with the history untouched.
        /// </remarks>
        internal int VisitCommittedPages(ITraceCommittedPageVisitor visitor)
        {
            RequireLive();

            if (visitor == null)
            {
                throw new ArgumentNullException(nameof(visitor));
            }

            int visited = 0;
            for (int page = 0; page < _pageCount; page++)
            {
                long committed = _committedByteCounts[page];
                if (committed <= 0L)
                {
                    continue;
                }

                visitor.Visit(page, _pages + ((long)page * _pageSize), (int)committed);
                visited++;
            }

            return visited;
        }

        /// <summary>
        /// Gives the pages back. Only valid once whatever writes to or reads
        /// from them has stopped; this type has no way to stop them and does
        /// not try. Calling it twice does nothing the second time.
        /// </summary>
        public void Dispose()
        {
            if (_block == null)
            {
                return;
            }

            UnsafeUtility.Free(_block, Allocator.Persistent);
            _block = null;
            _committedRecordCount = null;
            _dropCount = null;
            _committedByteCounts = null;
            _pages = null;
        }

        private void RequireLive()
        {
            if (_block == null)
            {
                throw new ObjectDisposedException(nameof(TracePagedHistory));
            }
        }
    }
}
