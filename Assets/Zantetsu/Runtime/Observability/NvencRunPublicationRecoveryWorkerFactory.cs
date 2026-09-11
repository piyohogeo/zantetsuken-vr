using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Composes one Run's whole Phase 0.11 NVENC recovery from the production
    /// concretes and hands back its unstarted worker, so an application does
    /// not wire a dozen execution and orchestration coordinators by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Create"/> builds and returns; it starts nothing. Every call
    /// produces a fresh worker over a fresh coordinator graph for that one open
    /// outcome and lease, and the factory keeps none of it: no worker list, no
    /// retained outcome, lease, operation, receipt, or terminal value, no
    /// success latch, and no retry counter. It owns no thread, signal, or
    /// filesystem handle either - what it holds is the four collaborators it
    /// was configured with, which every Run it composes shares.
    /// </para>
    /// <para>
    /// The checks made before anything is built are that the open outcome is
    /// currently valid and that its own lock identity evidence was issued for
    /// the exact lease it was handed - the same two the entry coordinator makes
    /// when it runs - and that this process is still Running. The root layout,
    /// Run identity, and disposition are left to the boundaries that already
    /// own them, and no new admission type is introduced. Composition itself
    /// touches no file: building an inspector, committer, cleaner, or releaser
    /// opens nothing.
    /// </para>
    /// <para>
    /// Only <see cref="NvencCaptureProcessStatus.Running"/> admits a recovery.
    /// Draining and PoisonedUntilProcessRestart are refused, because a recovery
    /// begun then would read and change a Run's tree while this process is
    /// already tearing down or has lost the right to act on it at all - a
    /// rename whose result is unknown is left to the next process instead. The
    /// refusal changes nothing: no file, no opener or buffer pool, no lease, no
    /// open outcome, and not the process state, which is never moved back to
    /// Running, reset, unpoisoned, or given a recovery-specific value here.
    /// The state is not injected into the worker either, and no correlation
    /// receipt is added to the recovery graph.
    /// </para>
    /// <para>
    /// The ordinary constructor creates the production no-follow opener and one
    /// <see cref="CaptureIndexCommitFileSystem"/>, which serves as both the
    /// commit and the cleanup filesystem, and takes the verification buffer
    /// pool the process shares. As wiring, the same opener instance is supplied
    /// to both inspectors and the same cleanup filesystem instance to both
    /// cleaners; nothing beyond that sharing is claimed here - the inspectors
    /// and the commit and cleanup surfaces are different interfaces making
    /// their own open attempts, and neither a shared snapshot nor a continuing
    /// file identity follows from passing one reference. No new bundle, factory
    /// interface, filesystem interface, receipt, status, or proof is added
    /// here, and the worker's own start control - including anything about
    /// process state - belongs to the composition root above.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryWorkerFactory
    {
        private readonly NvencCaptureProcessState _processState;
        private readonly ICaptureArtifactNoFollowOpener _opener;
        private readonly CaptureArtifactVerificationBufferPool _verificationBufferPool;
        private readonly ICaptureIndexCommitFileSystem _commitFileSystem;
        private readonly ICaptureCompleteCleanupFileSystem _cleanupFileSystem;

        internal NvencRunPublicationRecoveryWorkerFactory(
            NvencCaptureProcessState processState,
            CaptureArtifactVerificationBufferPool verificationBufferPool)
            : this(
                processState,
                CaptureArtifactNoFollowOpen.Create(),
                verificationBufferPool,
                CaptureIndexCommitFileSystem.Create())
        {
        }

        /// <summary>
        /// The production wiring: one commit filesystem instance is also the
        /// cleanup filesystem, since the two capability surfaces are the same
        /// backend.
        /// </summary>
        private NvencRunPublicationRecoveryWorkerFactory(
            NvencCaptureProcessState processState,
            ICaptureArtifactNoFollowOpener opener,
            CaptureArtifactVerificationBufferPool verificationBufferPool,
            CaptureIndexCommitFileSystem fileSystem)
            : this(processState, opener, verificationBufferPool, fileSystem, fileSystem)
        {
        }

        internal NvencRunPublicationRecoveryWorkerFactory(
            NvencCaptureProcessState processState,
            ICaptureArtifactNoFollowOpener opener,
            CaptureArtifactVerificationBufferPool verificationBufferPool,
            ICaptureIndexCommitFileSystem commitFileSystem,
            ICaptureCompleteCleanupFileSystem cleanupFileSystem)
        {
            _processState = processState ?? throw new ArgumentNullException(nameof(processState));
            _opener = opener ?? throw new ArgumentNullException(nameof(opener));
            _verificationBufferPool = verificationBufferPool
                ?? throw new ArgumentNullException(nameof(verificationBufferPool));
            _commitFileSystem = commitFileSystem
                ?? throw new ArgumentNullException(nameof(commitFileSystem));
            _cleanupFileSystem = cleanupFileSystem
                ?? throw new ArgumentNullException(nameof(cleanupFileSystem));
        }

        /// <summary>
        /// Composes one unstarted recovery worker for that exact open outcome
        /// and lease. Nothing is inspected, committed, cleaned up, released, or
        /// started here.
        /// </summary>
        internal NvencRunPublicationRecoveryWorkerService Create(
            CaptureRunInitializationOpenOutcome openOutcome,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (openOutcome == null)
            {
                throw new ArgumentNullException(nameof(openOutcome));
            }

            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            if (!openOutcome.IsValid)
            {
                throw new ArgumentException(
                    "Initialization open outcome must be valid.", nameof(openOutcome));
            }

            // Settled before anything is built: the lock identity evidence this
            // outcome carries answers whether that exact lease is this Run's
            // current lock holder, and nothing of it is re-derived here.
            if (openOutcome.LockIdentityEvidence?.IsIssuedFor(ownershipLease) != true)
            {
                throw new ArgumentException(
                    "The ownership lease must be the one this Run's lock identity evidence was issued for.",
                    nameof(ownershipLease));
            }

            // D-143: a recovery belongs to a process that is still Running.
            // Once this one is Draining or Poisoned it must not read or change
            // a Run's tree at all; what a rename left behind is the next
            // process's to observe.
            NvencCaptureProcessStatus processStatus = _processState.State;
            if (processStatus != NvencCaptureProcessStatus.Running)
            {
                throw new InvalidOperationException(
                    "A recovery worker is composed only while the process is Running; this process is "
                    + processStatus + ".");
            }

            CaptureRunRootLayout rootLayout = openOutcome.RootLayout;

            NvencRunPublicationRecoveryEntryCoordinator entry =
                new NvencRunPublicationRecoveryEntryCoordinator(
                    new NvencRunPublicationRecoveryOrchestrationCoordinator(
                        new NvencRunPublicationRecoveryInspectionExecutionCoordinator(
                            new NvencRunPublicationRecoveryInspector(
                                _opener, _verificationBufferPool))),
                    new NvencRunCaptureIndexRecoveryOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(
                            new NvencRunCaptureIndexRecoveryInspector(_opener))),
                    new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                                new NvencRunCaptureIndexRecoveryCommitter(
                                    rootLayout, _commitFileSystem))),
                        new NvencRunCaptureCompleteRecoveryExecutionCoordinator(
                            new NvencRunCaptureCompleteRecoveryCompleter())),
                    new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                        new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(
                            new NvencRunCaptureCompleteRecoveryCleaner(
                                rootLayout, _cleanupFileSystem))),
                    new NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator(
                        new NvencRunCaptureCompleteRecoveryOwnershipReleaser()),
                    new NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
                        new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(
                            new NvencRunPublicationRecoveryIncompleteCleaner(
                                rootLayout, _cleanupFileSystem))),
                    new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                        new NvencRunPublicationRecoveryIncompleteOwnershipReleaser()),
                    new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
                        new NvencRunPublicationRecoveryStopOwnershipReleaser()));

            // The worker is returned unstarted, and nothing of this graph is
            // retained here.
            return new NvencRunPublicationRecoveryWorkerService(
                new NvencRunPublicationRecoveryCoordinator(entry, openOutcome, ownershipLease));
        }
    }
}
