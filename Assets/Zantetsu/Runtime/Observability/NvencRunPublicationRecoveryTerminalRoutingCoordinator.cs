using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Routes one already-classified publication recovery to exactly one of the
    /// three terminalization branches, fixed at construction, and returns the
    /// common terminal value that branch produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What this type reads is the decision's current validity and its
    /// disposition: a Run that requires publication recovery goes to the
    /// CaptureComplete branch, an incomplete one to the orphan cleanup branch,
    /// and a collision or a deferred verification to the stopping branch. That
    /// choice is made once, in the constructor, so no later failure - a stage
    /// exception, a partial release, or an unusable receipt after a completed
    /// release - can send the same Run down a different branch.
    /// </para>
    /// <para>
    /// Every other correlation stays with the boundary that already owns it:
    /// the CaptureComplete branch's own constructor admission, the orphan
    /// cleanup operation factory, and the stopping release operation factory
    /// each check the lease against this decision's graph. The root layout, Run
    /// identity, lock path set, plan, and snapshot are not re-validated here.
    /// </para>
    /// <para>
    /// This type calls no filesystem API itself; the branch it selected may
    /// well commit a Capture Index or delete files, and that work, its
    /// once-only stages, its retry rules, and its completion latch all belong
    /// to that branch. Routing adds no route enum, status, result wrapper,
    /// receipt, proof, token, nonce, generation, started flag, or terminal
    /// latch of its own, keeps no copy of the terminal value, owns no thread,
    /// queue, task, wait, or deadline, never disposes the lease, and is not an
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryTerminalRoutingCoordinator
    {
        private readonly NvencRunPublicationRecoveryDecision _decision;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private readonly NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator
            _captureCompleteTerminal;

        private readonly NvencRunPublicationRecoveryIncompleteTerminalCoordinator
            _incompleteTerminal;

        private readonly NvencRunPublicationRecoveryStopTerminalCoordinator _stopTerminal;

        internal NvencRunPublicationRecoveryTerminalRoutingCoordinator(
            NvencRunPublicationRecoveryDecision decision,
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            NvencRunCaptureIndexRecoveryOrchestrationCoordinator captureIndexRecovery,
            NvencRunCaptureCompleteRecoveryOrchestrationCoordinator captureComplete,
            NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator captureCompleteCleanup,
            NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator
                captureCompleteReleaseExecution,
            NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator incompleteCleanup,
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
                incompleteReleaseExecution,
            NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator stopReleaseExecution)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            // Every branch this classifier can produce must be constructible,
            // so a missing dependency is refused even when this Run will not
            // take that branch.
            if (captureIndexRecovery == null)
            {
                throw new ArgumentNullException(nameof(captureIndexRecovery));
            }

            if (captureComplete == null)
            {
                throw new ArgumentNullException(nameof(captureComplete));
            }

            if (captureCompleteCleanup == null)
            {
                throw new ArgumentNullException(nameof(captureCompleteCleanup));
            }

            if (captureCompleteReleaseExecution == null)
            {
                throw new ArgumentNullException(nameof(captureCompleteReleaseExecution));
            }

            if (incompleteCleanup == null)
            {
                throw new ArgumentNullException(nameof(incompleteCleanup));
            }

            if (incompleteReleaseExecution == null)
            {
                throw new ArgumentNullException(nameof(incompleteReleaseExecution));
            }

            if (stopReleaseExecution == null)
            {
                throw new ArgumentNullException(nameof(stopReleaseExecution));
            }

            if (!decision.IsValid)
            {
                throw new ArgumentException(
                    "Publication recovery decision must be valid.", nameof(decision));
            }

            switch (decision.Disposition)
            {
                case NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired:
                    _captureCompleteTerminal =
                        new NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator(
                            decision,
                            ownershipLease,
                            captureIndexRecovery,
                            captureComplete,
                            captureCompleteCleanup,
                            captureCompleteReleaseExecution);
                    break;

                case NvencRunPublicationRecoveryDisposition.Incomplete:
                    _incompleteTerminal =
                        new NvencRunPublicationRecoveryIncompleteTerminalCoordinator(
                            decision, ownershipLease, incompleteCleanup,
                            incompleteReleaseExecution);
                    break;

                case NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision:
                case NvencRunPublicationRecoveryDisposition.Deferred:
                    // Both stopping shapes take the same branch; which one it
                    // was stays in the operation and receipt graph.
                    _stopTerminal = new NvencRunPublicationRecoveryStopTerminalCoordinator(
                        new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                            decision, ownershipLease, stopReleaseExecution));
                    break;

                default:
                    throw new ArgumentException(
                        "Publication recovery disposition has no terminal branch.",
                        nameof(decision));
            }

            _decision = decision;
            _ownershipLease = ownershipLease;
        }

        internal NvencRunPublicationRecoveryDecision Decision => _decision;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        /// <summary>
        /// The terminal value of the selected branch, read from that branch
        /// rather than copied here.
        /// </summary>
        internal NvencRunPublicationRecoveryTerminalResult TerminalResult
        {
            get
            {
                if (_captureCompleteTerminal != null)
                {
                    return _captureCompleteTerminal.TerminalResult;
                }

                if (_incompleteTerminal != null)
                {
                    return _incompleteTerminal.TerminalResult;
                }

                return _stopTerminal.TerminalResult;
            }
        }

        /// <summary>
        /// Whether the selected branch has reached its terminal value. The
        /// branch owns that latch; routing only asks it.
        /// </summary>
        internal bool IsComplete
        {
            get
            {
                if (_captureCompleteTerminal != null)
                {
                    return _captureCompleteTerminal.IsComplete;
                }

                if (_incompleteTerminal != null)
                {
                    return _incompleteTerminal.IsComplete;
                }

                return _stopTerminal.IsComplete;
            }
        }

        internal NvencRunPublicationRecoveryTerminalResult Execute()
        {
            // The branch was fixed at construction: the disposition is not read
            // again, no other branch is built, and a failure is never followed
            // by a fallback to one.
            if (_captureCompleteTerminal != null)
            {
                return _captureCompleteTerminal.Execute();
            }

            if (_incompleteTerminal != null)
            {
                return _incompleteTerminal.Execute();
            }

            return _stopTerminal.Execute();
        }
    }
}
