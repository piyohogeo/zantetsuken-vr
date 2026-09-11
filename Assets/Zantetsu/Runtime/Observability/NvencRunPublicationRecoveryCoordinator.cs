using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// One recovered Run's whole synchronous recovery, held as a re-enterable
    /// unit of work: the entry classifies it once, the routing coordinator that
    /// classification produced is kept, and every later call goes back through
    /// that same routing until the common terminal value exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two stages are latched differently on purpose. The entry is entered
    /// at most once - the flag is set before the call, so an inspection that
    /// threw is never repeated and a later call stops with an
    /// <see cref="InvalidOperationException"/> instead of re-reading a tree a
    /// branch may already have started changing. The routing coordinator, once
    /// retained, is the only thing later calls use, so a stage exception or a
    /// partial release resumes inside the branch that was already chosen rather
    /// than returning to the entry.
    /// </para>
    /// <para>
    /// Completion is the held terminal result's own validity, assigned only
    /// after that value has been checked, so a partial release leaves this
    /// coordinator unfinished with the next call retrying the same release
    /// operation, and a fully released lease whose receipt turned out unusable
    /// leaves it unfinished with nothing to retry. Neither infers a success
    /// from the lease's state. Once the terminal value is held, a further call
    /// returns it without re-entering the entry or the branch.
    /// </para>
    /// <para>
    /// What this layer checks of that value is only that it is valid and that
    /// it carries this Run's exact open outcome and lease; which disposition
    /// was taken, and everything the branch did, is the branch's own to vouch
    /// for. Exceptions from the entry and the routing coordinator propagate by
    /// the same reference, and a terminal value that fails those checks is an
    /// <see cref="InvalidOperationException"/> rather than a result.
    /// </para>
    /// <para>
    /// The outcome and lease correlation belongs to the entry and is not
    /// restated here. This type calls no filesystem API itself, though the
    /// branch it reaches may commit a Capture Index or delete files; it adds no
    /// interface, result wrapper, receipt, status, proof, token, nonce,
    /// generation, or retry counter, owns no thread, queue, task, wait,
    /// deadline, or process state, never disposes the lease, and is not an
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryCoordinator
    {
        private readonly NvencRunPublicationRecoveryEntryCoordinator _entry;
        private readonly CaptureRunInitializationOpenOutcome _openOutcome;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private bool _entryStarted;

        private NvencRunPublicationRecoveryTerminalRoutingCoordinator _routingCoordinator;

        private NvencRunPublicationRecoveryTerminalResult _terminalResult;

        internal NvencRunPublicationRecoveryCoordinator(
            NvencRunPublicationRecoveryEntryCoordinator entry,
            CaptureRunInitializationOpenOutcome openOutcome,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            // The outcome's validity and its correlation to this exact lease
            // are the entry's own admission, at the moment it runs.
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));
            _openOutcome = openOutcome ?? throw new ArgumentNullException(nameof(openOutcome));
            _ownershipLease = ownershipLease
                ?? throw new ArgumentNullException(nameof(ownershipLease));
        }

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _openOutcome;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal NvencRunPublicationRecoveryTerminalRoutingCoordinator RoutingCoordinator =>
            _routingCoordinator;

        internal NvencRunPublicationRecoveryTerminalResult TerminalResult => _terminalResult;

        /// <summary>
        /// Whether this Run has been classified and its branch chosen. It says
        /// nothing about which branch, or about how far that branch has got.
        /// </summary>
        internal bool IsRoutingPrepared => _routingCoordinator != null;

        /// <summary>
        /// Whether this Run has reached its terminal value, derived from that
        /// value's own validity rather than from a separate flag.
        /// </summary>
        internal bool IsComplete => _terminalResult.IsValid;

        internal NvencRunPublicationRecoveryTerminalResult Execute()
        {
            if (_terminalResult.IsValid)
            {
                // Already terminal: the same value, without re-entering the
                // entry or the branch.
                return _terminalResult;
            }

            if (_routingCoordinator == null)
            {
                if (_entryStarted)
                {
                    // The inspection was entered and produced no routing, so a
                    // branch may already have begun changing this Run; reading
                    // the tree again here is exactly what must not happen.
                    throw new InvalidOperationException(
                        "The recovery entry was already started and produced no routing; it is not run again here.");
                }

                // Set before the call, so an exception cannot be followed by a
                // second inspection.
                _entryStarted = true;

                NvencRunPublicationRecoveryTerminalRoutingCoordinator routingCoordinator =
                    _entry.Begin(_openOutcome, _ownershipLease);

                if (routingCoordinator == null)
                {
                    throw new InvalidOperationException(
                        "The recovery entry returned no terminal routing coordinator.");
                }

                _routingCoordinator = routingCoordinator;
            }

            NvencRunPublicationRecoveryTerminalResult terminal = _routingCoordinator.Execute();

            if (!terminal.IsValid
                || !ReferenceEquals(terminal.OpenOutcome, _openOutcome)
                || !ReferenceEquals(terminal.OwnershipLease, _ownershipLease))
            {
                throw new InvalidOperationException(
                    "The terminal result must be valid and carry this Run's exact open outcome and ownership lease.");
            }

            // Assigned only after those checks, so an unfinished release leaves
            // the retry to the next call through this same routing.
            _terminalResult = terminal;
            return _terminalResult;
        }
    }
}
