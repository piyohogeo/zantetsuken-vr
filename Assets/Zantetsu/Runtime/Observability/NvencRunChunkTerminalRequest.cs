using System;
using System.Threading;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed one-slot Run chunk terminal request boundary. It binds the exact
    /// <see cref="NvencRunChunkContext"/>, accepts exactly one Finalize or
    /// Abandon request, advances it exactly once on the Output Worker, and
    /// publishes the terminal outcome for exactly-once collection by the Main
    /// Thread. The caller thread only ever stores the exact context and kind;
    /// it never touches the context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request exclusion, the terminal outcome publication, and the
    /// collected transition are serialized on a short fixed gate. The
    /// finalizer I/O runs outside the gate. The result is held first and the
    /// <c>Completed</c> state is published with release semantics; the
    /// collector observes <c>Completed</c> with acquire semantics and only
    /// then reads the result.
    /// </para>
    /// <para>
    /// A transient pre-side-effect <c>false</c> from
    /// <see cref="NvencRunChunkContext.TryFinalize(out NvencChunkFinalizationResult)"/>
    /// keeps the request for the next notification; no result or abandon is
    /// fabricated. An accepted Abandon request that does not abandon, a null or
    /// uncorrelated finalized result, and any finalizer or context exception
    /// are invariant failures that propagate unchanged for the existing worker
    /// fatal path; no terminal outcome is published for them.
    /// </para>
    /// <para>
    /// This type holds no array, queue, dictionary, token, proof, nonce,
    /// history, thread, timer, or sleep, and it is not an
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunChunkTerminalRequest
    {
        private readonly object _gate = new object();

        private int _state;
        private NvencRunChunkContext _context;
        private bool _finalizeKind;
        private NvencChunkFinalizationResult _result;

        internal NvencRunChunkTerminalRequest()
        {
            _state = (int)NvencRunChunkTerminalRequestState.None;
        }

        internal NvencRunChunkTerminalRequestState State =>
            (NvencRunChunkTerminalRequestState)Volatile.Read(ref _state);

        /// <summary>
        /// Accepts an exclusive Finalize request into the empty slot. The
        /// caller stores only the exact context; it never contacts the
        /// context. Returns false while a request is already held or the
        /// terminal is Completed or Collected.
        /// </summary>
        internal bool TryAcceptFinalize(NvencRunChunkContext context)
        {
            return TryAccept(context, finalize: true);
        }

        /// <summary>
        /// Accepts an exclusive Abandon request into the empty slot. The
        /// caller stores only the exact context; it never contacts the
        /// context. Returns false while a request is already held or the
        /// terminal is Completed or Collected.
        /// </summary>
        internal bool TryAcceptAbandon(NvencRunChunkContext context)
        {
            return TryAccept(context, finalize: false);
        }

        private bool TryAccept(NvencRunChunkContext context, bool finalize)
        {
            if (context == null)
            {
                return false;
            }

            lock (_gate)
            {
                if ((NvencRunChunkTerminalRequestState)Volatile.Read(ref _state) != NvencRunChunkTerminalRequestState.None)
                {
                    return false;
                }

                _context = context;
                _finalizeKind = finalize;
                Volatile.Write(
                    ref _state,
                    finalize
                        ? (int)NvencRunChunkTerminalRequestState.FinalizeRequested
                        : (int)NvencRunChunkTerminalRequestState.AbandonRequested);
                return true;
            }
        }

        /// <summary>
        /// Advances the accepted request exactly once on the Output Worker.
        /// Returns false when there is no accepted request, when the terminal
        /// is already Completed or Collected, or when a Finalize request
        /// returned a transient pre-side-effect false (the request is kept for
        /// the next notification). A null or uncorrelated finalized result, or
        /// an accepted Abandon request that does not abandon, throws an
        /// invariant failure for the worker fatal path.
        /// </summary>
        internal bool TryAdvance()
        {
            NvencRunChunkTerminalRequestState state =
                (NvencRunChunkTerminalRequestState)Volatile.Read(ref _state);

            if (state == NvencRunChunkTerminalRequestState.FinalizeRequested)
            {
                NvencRunChunkContext context = _context;
                if (context == null)
                {
                    throw new InvalidOperationException(
                        "Run chunk terminal request holds no Run chunk context.");
                }

                // Finalizer I/O and the context call run outside the gate.
                if (!context.TryFinalize(out NvencChunkFinalizationResult result))
                {
                    // Transient pre-side-effect false: keep the request and
                    // retry on the next notification.
                    return false;
                }

                if (result == null || !result.IsValid || !ReferenceEquals(result.Sink, context.Sink))
                {
                    throw new InvalidOperationException(
                        "Finalized Run chunk terminal result does not correlate to the requested context.");
                }

                PublishCompleted(result);
                return true;
            }

            if (state == NvencRunChunkTerminalRequestState.AbandonRequested)
            {
                NvencRunChunkContext context = _context;
                if (context == null)
                {
                    throw new InvalidOperationException(
                        "Run chunk terminal request holds no Run chunk context.");
                }

                // The abandon call runs outside the gate.
                if (!context.TryAbandon())
                {
                    throw new InvalidOperationException(
                        "Accepted Run chunk abandon request did not abandon the context.");
                }

                PublishCompleted(null);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Collects the published terminal outcome exactly once. Returns false
        /// while the terminal is unfinished, already collected, or poisoned
        /// with no published outcome; it never guesses a result.
        /// </summary>
        internal bool TryCollect(out NvencRunChunkTerminalOutcome outcome)
        {
            outcome = default;

            lock (_gate)
            {
                if ((NvencRunChunkTerminalRequestState)Volatile.Read(ref _state) != NvencRunChunkTerminalRequestState.Completed)
                {
                    return false;
                }

                outcome = _finalizeKind
                    ? NvencRunChunkTerminalOutcome.Finalized(_result)
                    : NvencRunChunkTerminalOutcome.Abandoned();

                Volatile.Write(ref _state, (int)NvencRunChunkTerminalRequestState.Collected);
                return true;
            }
        }

        private void PublishCompleted(NvencChunkFinalizationResult result)
        {
            lock (_gate)
            {
                // Hold the result first, then publish Completed with release
                // semantics.
                _result = result;
                Volatile.Write(ref _state, (int)NvencRunChunkTerminalRequestState.Completed);
            }
        }
    }
}
