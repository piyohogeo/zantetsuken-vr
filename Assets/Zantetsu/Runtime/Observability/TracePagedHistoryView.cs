using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Something that looks at the committed part of one history page.
    /// </summary>
    /// <remarks>
    /// The bytes are the history's own and are valid only while this call is
    /// running: a visitor reads or copies what it wants there and then, and
    /// never keeps the pointer or takes it over. Pages arrive in order, and a
    /// page is only ever handed over as far as it was committed.
    /// </remarks>
    internal unsafe interface ITraceCommittedPageVisitor
    {
        void Visit(int pageOrdinal, byte* committedBytes, int committedByteCount);
    }

    /// <summary>
    /// A way of reading back what a history holds, once nothing is writing to
    /// it any more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer having stopped, and the history still being alive for as
    /// long as this is used, are the caller's to keep. Nothing here proves
    /// either: there is no seal, latch, writer registry, lease, receipt,
    /// generation, or reference count, and nothing here copes with a write
    /// happening at the same time.
    /// </para>
    /// <para>
    /// What is handed over is each page's committed run of bytes and nothing
    /// else - no unused tail, no uncommitted room, no metadata. The pages are
    /// not joined together, nothing is copied into an array, no index of
    /// records is built, and no record is parsed, checked, or versioned here:
    /// what the bytes mean is the reader's business.
    /// </para>
    /// </remarks>
    internal readonly unsafe struct TracePagedHistoryView
    {
        private readonly TracePagedHistory _history;

        internal TracePagedHistoryView(TracePagedHistory history)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
        }

        /// <summary>
        /// Hands each page that has anything committed to the visitor, in page
        /// order, and returns how many pages that was.
        /// </summary>
        /// <remarks>
        /// A history with nothing in it calls the visitor not at all and
        /// returns nothing. A visitor that throws stops the walk there and its
        /// failure is passed on as it is, leaving the history exactly as it
        /// was.
        /// </remarks>
        internal int VisitCommittedPages(ITraceCommittedPageVisitor visitor)
        {
            if (_history == null)
            {
                throw new InvalidOperationException(
                    "This view was never given a history to read.");
            }

            return _history.VisitCommittedPages(visitor);
        }
    }
}
