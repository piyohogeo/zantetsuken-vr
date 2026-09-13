using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Ends one Run's variable-length trace and puts it on disk, in that
    /// order, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It holds the finalizer it was given and nothing else. The producers
    /// having stopped - every job finished, every worker stopped, every writer
    /// done with - is the caller's promise, exactly as it was for the final
    /// drain; nothing here proves it again, and there is no registry, lease,
    /// receipt, writer count, timeout, or watchdog to say so. The lanes, the
    /// history, the drainer and the file are all the caller's too: none of
    /// them is taken over or released here.
    /// </para>
    /// <para>
    /// The two steps happen in one order and are not second-guessed. If
    /// finishing fails, nothing is saved. If saving fails, no result and no
    /// byte count are handed back, and the failure comes through as it is -
    /// the Run may well be sealed by then, and it stays sealed: nothing here
    /// unseals it, finishes it again, saves it elsewhere, or tries again.
    /// Calling this twice is refused by the seal the finalizer already has,
    /// not by a rule of its own, and neither the verdict nor the drop counts
    /// are worked out a second time here.
    /// </para>
    /// <para>
    /// Where the file goes is the caller's decision: no path, name, or
    /// extension is decided here.
    /// </para>
    /// </remarks>
    internal sealed class TracePagedRunTerminationCoordinator
    {
        private readonly TracePagedRunFinalizer _finalizer;

        internal TracePagedRunTerminationCoordinator(TracePagedRunFinalizer finalizer)
        {
            _finalizer = finalizer ?? throw new ArgumentNullException(nameof(finalizer));
        }

        /// <summary>
        /// Takes the last records, seals the Run, and saves what it came to,
        /// returning that result and how many bytes were written only once the
        /// file is there.
        /// </summary>
        internal TracePagedRunResult FinishAndSave(string destinationPath, out long savedByteCount)
        {
            TracePagedRunResult result = _finalizer.Finish();

            // Only what was just finished is saved, and only once. A failure
            // here leaves the caller with no result and no count.
            savedByteCount = TracePagedHistoryFileStore.SaveAtomic(destinationPath, result);
            return result;
        }
    }
}
