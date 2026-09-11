using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Brings one stopped publication recovery to the common terminal value: it
    /// drives the retained stopping release to completion and carries the
    /// resulting receipt as a
    /// <see cref="NvencRunPublicationRecoveryTerminalResult"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The held terminal result is the completion latch, and its own validity
    /// is the only thing consulted for that: no separate flag, status, retry
    /// counter, or copy of the receipt, operation, decision, or lease. Once it
    /// is valid, that exact value comes back and the release coordinator is not
    /// touched again.
    /// </para>
    /// <para>
    /// Until then every call goes through the same retained release
    /// coordinator, and the terminal result is assigned only after the
    /// terminal factory has returned. The two ways that can fail are not the
    /// same. A partial release - the disposal threw with the lease still
    /// releasable - leaves this coordinator unfinished and the next call
    /// becomes another release attempt on that same retained operation. A call
    /// that fully released the ownership lease but produced an unusable
    /// receipt also leaves it unfinished, but there is nothing left to retry:
    /// the exception propagates, nothing is retained, and every later call
    /// stops at the retention coordinator's own admission without reaching the
    /// releaser again, because neither the lease's state nor the decision is
    /// read as success.
    /// </para>
    /// <para>
    /// The collision and deferred shapes are never branched on here; which one
    /// this was is already carried by the operation and receipt graph the
    /// release coordinator holds. What this boundary handles is exactly two
    /// things: the validity of its own terminal result, and the result of that
    /// existing release coordinator.
    /// </para>
    /// <para>
    /// It performs no inspection, classification, or reclassification, no
    /// filesystem operation, no cleanup, Capture Index, or CaptureComplete
    /// work, changes no process state, Poison, Registry, disposition, or
    /// Service, owns no thread, queue, task, wait, or deadline, never disposes
    /// the lease itself, and adds no receipt, status, result wrapper, proof,
    /// token, nonce, or generation. It is not an <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryStopTerminalCoordinator
    {
        private readonly NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator
            _releaseCoordinator;

        private NvencRunPublicationRecoveryTerminalResult _terminalResult;

        internal NvencRunPublicationRecoveryStopTerminalCoordinator(
            NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator releaseCoordinator)
        {
            _releaseCoordinator = releaseCoordinator
                ?? throw new ArgumentNullException(nameof(releaseCoordinator));
        }

        internal NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator ReleaseCoordinator =>
            _releaseCoordinator;

        internal NvencRunPublicationRecoveryTerminalResult TerminalResult => _terminalResult;

        /// <summary>
        /// Whether this Run has reached its terminal value, derived from that
        /// value's own validity rather than from a separate flag.
        /// </summary>
        internal bool IsComplete => _terminalResult.IsValid;

        internal NvencRunPublicationRecoveryTerminalResult Execute()
        {
            if (_terminalResult.IsValid)
            {
                // Already terminal: the same value, without touching the
                // release coordinator, the releaser, or the lease again.
                return _terminalResult;
            }

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                _releaseCoordinator.Release();

            NvencRunPublicationRecoveryTerminalResult terminal =
                NvencRunPublicationRecoveryTerminalResult.Stopped(receipt);

            // Assigned only after a returned factory. A partial release
            // leaves the retry to the next call through that same release
            // coordinator; an unusable receipt after a completed release
            // leaves this unfinished with nothing left to retry.
            _terminalResult = terminal;
            return _terminalResult;
        }
    }
}
