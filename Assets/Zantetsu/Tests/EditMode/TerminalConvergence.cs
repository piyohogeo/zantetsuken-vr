using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Test-only convergence helper for the Output Worker's Run chunk terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Worker's <c>Settled</c> event is documented as a best-effort
    /// observation raised when the Worker is about to park, and production
    /// correctness never depends on subscribers. A settle observed after a
    /// terminal request is therefore not evidence that this request's terminal
    /// was advanced: the Worker reaches its raise only after it has already
    /// reset its wake signal and re-checked for work, so a raise that was
    /// already in flight when the request was accepted arrives afterwards
    /// carrying no information about it.
    /// </para>
    /// <para>
    /// This helper never treats a settle as the condition. It confirms the real
    /// condition - <c>TryCollectTerminal</c> - and uses the settle only as a
    /// wake hint between attempts, against a single monotonic deadline that is
    /// never re-granted. It returns at the first success and never collects
    /// twice, and it re-issues no terminal request, no teardown, and no work:
    /// the only thing repeated is the non-destructive collect and the Worker
    /// notification that asks it to re-evaluate.
    /// </para>
    /// </remarks>
    internal static class TerminalConvergence
    {
        internal delegate bool TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome);

        internal static NvencRunChunkTerminalOutcome Collect(
            TryCollectTerminal tryCollect,
            NvencOrderedOutputWorkerService worker,
            ManualResetEventSlim settled,
            int timeoutMs,
            string message)
        {
            NvencRunChunkTerminalOutcome outcome;

            // The condition may already hold, in which case nothing is waited
            // on and no notification is sent.
            if (tryCollect(out outcome))
            {
                return outcome;
            }

            Stopwatch watch = Stopwatch.StartNew();
            while (true)
            {
                // Drop any settle observed before this attempt, then ask the
                // Worker to re-evaluate. Notify only sets the wake signal; it
                // requests nothing.
                settled.Reset();
                worker.Notify();

                // Re-check before parking, so a terminal advanced between the
                // reset and the wait is not missed.
                if (tryCollect(out outcome))
                {
                    return outcome;
                }

                long remaining = timeoutMs - watch.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    break;
                }

                // The wake hint's own result is deliberately ignored: only the
                // re-check below decides.
                settled.Wait((int)remaining);

                if (tryCollect(out outcome))
                {
                    return outcome;
                }

                if (watch.ElapsedMilliseconds >= timeoutMs)
                {
                    break;
                }
            }

            Assert.Fail(
                message
                + " (the Run chunk terminal was still not collectable after "
                + timeoutMs.ToString()
                + " ms; a settle observation alone is not evidence that it was advanced)");
            return default;
        }
    }
}
