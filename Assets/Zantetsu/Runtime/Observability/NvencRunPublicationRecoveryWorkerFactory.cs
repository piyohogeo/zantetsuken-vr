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
    /// The only checks made before anything is built are that the open outcome
    /// is currently valid and that its own lock identity evidence was issued
    /// for the exact lease it was handed. Those are the same two the entry
    /// coordinator makes when it runs; the root layout, Run identity, and
    /// disposition are left to the boundaries that already own them, and no new
    /// admission type is introduced. Composition itself touches no file:
    /// building an inspector, committer, cleaner, or releaser opens nothing.
    /// </para>
    /// <para>
    /// The ordinary constructor creates the production no-follow opener and one
    /// <see cref="CaptureIndexCommitFileSystem"/>, which serves as both the
    /// commit and the cleanup filesystem, and takes the verification buffer
    /// pool the process shares. The same opener goes to both inspectors and the
    /// same cleanup filesystem to both cleaners, so a Run never observes itself
    /// through two different backends. No new bundle, factory interface,
    /// filesystem interface, receipt, status, or proof is added here, and the
    /// worker's own start control - including anything about process state -
    /// belongs to the composition root above.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryWorkerFactory
    {
        private readonly ICaptureArtifactNoFollowOpener _opener;
        private readonly CaptureArtifactVerificationBufferPool _verificationBufferPool;
        private readonly ICaptureIndexCommitFileSystem _commitFileSystem;
        private readonly ICaptureCompleteCleanupFileSystem _cleanupFileSystem;

        internal NvencRunPublicationRecoveryWorkerFactory(
            CaptureArtifactVerificationBufferPool verificationBufferPool)
            : this(
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
            ICaptureArtifactNoFollowOpener opener,
            CaptureArtifactVerificationBufferPool verificationBufferPool,
            CaptureIndexCommitFileSystem fileSystem)
            : this(opener, verificationBufferPool, fileSystem, fileSystem)
        {
        }

        internal NvencRunPublicationRecoveryWorkerFactory(
            ICaptureArtifactNoFollowOpener opener,
            CaptureArtifactVerificationBufferPool verificationBufferPool,
            ICaptureIndexCommitFileSystem commitFileSystem,
            ICaptureCompleteCleanupFileSystem cleanupFileSystem)
        {
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
