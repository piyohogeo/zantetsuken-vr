using System;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

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
    /// What is held is only the pages, one committed byte count per page, and
    /// one committed record count for the history - no page objects, no index,
    /// no page state, no reference count, lease, generation, or live snapshot.
    /// The producer lanes are not this type's and are not released with it;
    /// stopping whatever writes to or reads from the history is not its job
    /// either. <see cref="Dispose"/> gives the region back once, and a second
    /// call does nothing.
    /// </para>
    /// <para>
    /// Nothing is written into a page here. The pointer into a page is not
    /// handed out, and neither is any array behind it: what a writer needs
    /// comes later.
    /// </para>
    /// </remarks>
    internal sealed unsafe class TracePagedHistory : IDisposable
    {
        /// <summary>
        /// The block in front of the per-page counts, holding the history's own
        /// committed record count. One cache line, so that counter does not
        /// share a line with the page counts.
        /// </summary>
        private const int MetadataBlockBytes = 64;

        private const int CommittedRecordCountOffset = 0;

        private readonly int _pageSize;
        private readonly int _pageCount;
        private readonly long _blockBytes;

        private byte* _block;
        private long* _committedRecordCount;
        private long* _committedByteCounts;

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
            _committedByteCounts = (long*)(_block + MetadataBlockBytes);
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
            _committedByteCounts = null;
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
