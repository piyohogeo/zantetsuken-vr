using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The production <see cref="INvencSourceReadCompletedSource"/>: it asks the
    /// native session, once and without waiting, whether the GPU conversion
    /// bound to one submission's sync lease has finished reading its source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session is the caller's. This holds one reference to that exact
    /// owner and nothing else: no cache, latch, poll loop, thread, task, timer,
    /// native handle, or process state, and it never opens, closes, or replaces
    /// a session.
    /// </para>
    /// <para>
    /// A conversion that has not completed is reported as <c>false</c> with
    /// default evidence, which is the caller's cue to come back later. Anything
    /// the session raises - a callback that failed, a refused wait or reset, a
    /// generation that is not outstanding - is left to propagate exactly as it
    /// came: it is not caught, wrapped, retried, or turned into "not yet",
    /// because a completion that is never coming must not be waited for
    /// forever.
    /// </para>
    /// </remarks>
    internal sealed class NvencNativeSourceReadCompletedSource : INvencSourceReadCompletedSource
    {
        private readonly NvencNativeEncoderSessionOwner _session;

        internal NvencNativeSourceReadCompletedSource(NvencNativeEncoderSessionOwner session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            _session = session;
        }

        /// <summary>
        /// Asks about exactly the conversion this record's sync lease names,
        /// once, with no patience at all.
        /// </summary>
        /// <remarks>
        /// The lease's slot index and generation are used as they are: a sync
        /// lease's generation is already the positive, forward-only number the
        /// conversion was armed with, so there is no second counter and no
        /// translation table here.
        /// </remarks>
        public bool TryGetEvidence(
            in NvencSubmissionRecord record,
            out NvencSourceReadCompletedEvidence evidence)
        {
            evidence = default;

            // Timeout zero: this asks, it never waits. A caller that needs to
            // wait comes back.
            if (!_session.TryCollectConversionCommand(
                    record.SyncSlot.SlotIndex,
                    (ulong)record.SyncSlot.Generation,
                    0))
            {
                return false;
            }

            evidence = NvencSourceReadCompletedEvidence.Create(this, record);
            return true;
        }
    }
}
