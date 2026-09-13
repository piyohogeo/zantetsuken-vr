using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Managed end-to-end test of the Phase 0.11 NVENC incomplete/orphan
    /// cleanup happy path over a real filesystem: from the publication recovery
    /// inspection that finds a Run with no finished plan, through discarding
    /// both of its Run roots, to the release of the Run's Session Ownership
    /// Lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every stage from the publication recovery onwards is the production
    /// concrete over the production no-follow backend and a real temporary
    /// tree. The lock handles behind the Session Ownership Lease are the same
    /// small test seam the contract fixtures use, so what the release step
    /// shows is the production releaser driving that lease and its handles to
    /// a completed release - not the release of a real OS lock, which is not
    /// what this path is about. No private field is rewritten and no NVENC,
    /// GPU, Worker, Publication Service, Capture Index, CaptureComplete, or
    /// process state is involved.
    /// </para>
    /// <para>
    /// One representative healthy path is covered here: a representative
    /// incomplete tree, with no finished plan, cleaned and released. Why that
    /// plan is absent - an explicit abort, a failed pre-commit, or a crash - is
    /// neither observed nor asserted anywhere here; the classification rests on
    /// the file set alone. The failed cleanup, partial release, collision,
    /// deferred, and recoverable shapes belong to the contract fixtures that
    /// own them.
    /// </para>
    /// </remarks>
    public class NvencCapturePublicationPhase011IncompleteRecoveryEndToEndTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string CaptureIndexName = "capture.index";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string PublicationPlanName = "publication.plan";

        private const string PrecommitTemporaryName =
            "publication.plan.nvenc-precommit.tmp";

        private const string ChunksDirectoryName = "chunks";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

        private const string PartialChunkName = "chunk-0.nvenc-idr-chunk-v1.h264.partial";

        private const string FinalizedChunkName = "chunk-0.nvenc-idr-chunk-v1.h264";

        private const int VerificationBufferLength = 64 * 1024;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                try
                {
                    owner.Dispose();
                }
                catch (AggregateException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            _owners.Clear();

            foreach (string sandbox in _sandboxes)
            {
                try
                {
                    if (Directory.Exists(sandbox))
                    {
                        Directory.Delete(sandbox, true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            _sandboxes.Clear();
        }

        [Test]
        public void IncompleteRun_IsDiscardedWholeAndThenReleasesItsLease()
        {
            RequireNoFollowCapabilities();

            Sandbox sandbox = MakeSandbox();

            // ---- 1. Publication recovery: no finished plan on disk ----
            NvencRunPublicationRecoveryDecision publicationRecovery =
                new NvencRunPublicationRecoveryOrchestrationCoordinator(
                        new NvencRunPublicationRecoveryInspectionExecutionCoordinator(
                            new NvencRunPublicationRecoveryInspector(
                                CaptureArtifactNoFollowOpen.Create(),
                                new CaptureArtifactVerificationBufferPool(
                                    VerificationBufferLength))))
                    .Execute(sandbox.OpenOutcome);

            Assert.That(publicationRecovery.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));
            Assert.That(publicationRecovery.IsValid, Is.True);

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = publicationRecovery.Snapshot;
            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Absent));
            Assert.That(snapshot.PublicationPlan, Is.Null);
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);
            Assert.That(publicationRecovery.AuthoritativePlan, Is.Null);

            // Nothing authorized the chunk: there is no verification at all,
            // let alone a successful one.
            Assert.That(snapshot.HasChunkVerificationResult, Is.False);

            // The inspection observed only; every seeded entry is still there.
            sandbox.AssertSeededTreeIntact();

            // ---- 2. Discard both Run roots ----
            NvencRunPublicationRecoveryIncompleteCleaner cleaner =
                new NvencRunPublicationRecoveryIncompleteCleaner(sandbox.Layout);

            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult cleanup =
                new NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
                        new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(
                            cleaner))
                    .Execute(publicationRecovery, sandbox.Owner);

            Assert.That(cleanup.IsCleaned, Is.True);
            Assert.That(cleanup.Receipt, Is.Not.Null);
            Assert.That(cleanup.Receipt.IsIssuedFor(cleaner, cleanup.CleanupOperation), Is.True);
            Assert.That(cleanup.IsIssuedFor(cleaner, cleanup.CleanupOperation), Is.True);

            // One authority graph, from the classification down.
            Assert.That(ReferenceEquals(cleanup.Decision, publicationRecovery), Is.True);
            Assert.That(ReferenceEquals(cleanup.Snapshot, snapshot), Is.True);
            Assert.That(ReferenceEquals(cleanup.OpenOutcome, sandbox.OpenOutcome), Is.True);
            Assert.That(
                ReferenceEquals(cleanup.LockIdentityEvidence, sandbox.LockIdentityEvidence),
                Is.True);
            Assert.That(ReferenceEquals(cleanup.OwnershipLease, sandbox.Owner), Is.True);
            Assert.That(ReferenceEquals(cleanup.RootLayout, sandbox.Layout), Is.True);
            Assert.That(cleanup.TestRunId, Is.EqualTo(sandbox.Layout.TestRunId));
            Assert.That(cleanup.RunInitializationId, Is.EqualTo(InitId));

            sandbox.AssertBothRunRootsDiscarded();

            // The lock is still fully held: releasing it is the next step's
            // decision, not the cleanup's.
            Assert.That(sandbox.Owner.IsCreated, Is.True);
            Assert.That(sandbox.Owner.CanRelease, Is.True);
            Assert.That(sandbox.Owner.IsReleaseComplete, Is.False);
            Assert.That(sandbox.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(sandbox.SecondHandle.DisposeCallCount, Is.EqualTo(0));

            // ---- 3. Release the Run's Session Ownership Lease ----
            NvencRunPublicationRecoveryIncompleteOwnershipReleaser releaser =
                new NvencRunPublicationRecoveryIncompleteOwnershipReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator releaseCoordinator =
                new NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
                    new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                        releaser),
                    cleanup,
                    sandbox.Owner);

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation releaseOperation =
                releaseCoordinator.Operation;

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt releaseReceipt =
                releaseCoordinator.Release();

            Assert.That(releaseReceipt.IsValid, Is.True);
            Assert.That(releaseReceipt.IsIssuedFor(releaser, releaseOperation), Is.True);
            Assert.That(ReferenceEquals(releaseReceipt.Operation, releaseOperation), Is.True);
            Assert.That(releaseCoordinator.IsReleased, Is.True);

            // Still the same graph, now carrying the cleanup's terminal status.
            Assert.That(releaseReceipt.CleanupStatus,
                Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned));
            Assert.That(
                ReferenceEquals(releaseReceipt.CleanupOperation, cleanup.CleanupOperation),
                Is.True);
            Assert.That(
                ReferenceEquals(releaseReceipt.CleanupResult.Cleaner, cleaner), Is.True);
            Assert.That(ReferenceEquals(releaseReceipt.Decision, publicationRecovery), Is.True);
            Assert.That(ReferenceEquals(releaseReceipt.Snapshot, snapshot), Is.True);
            Assert.That(ReferenceEquals(releaseReceipt.OpenOutcome, sandbox.OpenOutcome), Is.True);
            Assert.That(
                ReferenceEquals(releaseReceipt.LockIdentityEvidence, sandbox.LockIdentityEvidence),
                Is.True);
            Assert.That(ReferenceEquals(releaseReceipt.OwnershipLease, sandbox.Owner), Is.True);
            Assert.That(ReferenceEquals(releaseReceipt.RootLayout, sandbox.Layout), Is.True);
            Assert.That(releaseReceipt.TestRunId, Is.EqualTo(sandbox.Layout.TestRunId));
            Assert.That(releaseReceipt.RunInitializationId, Is.EqualTo(InitId));

            // The lease and its handles, which is what this seam holds.
            Assert.That(sandbox.Owner.IsReleaseComplete, Is.True);
            Assert.That(sandbox.Owner.CanRelease, Is.False);
            Assert.That(sandbox.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(sandbox.SecondHandle.DisposeCallCount, Is.EqualTo(1));

            // First-attempt admission is over on both sides; the historical
            // binding the receipt rests on is not.
            Assert.That(cleanup.IsValid, Is.False);
            Assert.That(releaseOperation.IsValid, Is.False);
            Assert.That(releaseOperation.CanRelease, Is.False);
            Assert.That(releaseOperation.IsBindingIntact, Is.True);

            // ---- 4. A second release changes nothing ----
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt again =
                releaseCoordinator.Release();

            Assert.That(ReferenceEquals(again, releaseReceipt), Is.True);
            Assert.That(sandbox.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(sandbox.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            sandbox.AssertBothRunRootsDiscarded();
        }

        // ---- Fixture helpers ----

        /// <summary>
        /// Refuses the platform only on an explicit capability answer, before
        /// any side effect. An I/O failure is never read as a missing
        /// capability.
        /// </summary>
        private static void RequireNoFollowCapabilities()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("Phase 0.11 recovery requires Windows no-follow file handles.");
            }

            if (!CaptureArtifactNoFollowOpen.Create().IsSupported)
            {
                Assert.Ignore("No-follow artifact open is not available on this platform.");
            }

            CaptureIndexCommitFileSystem fileSystem = CaptureIndexCommitFileSystem.Create();
            if (!fileSystem.IsSupported || !fileSystem.IsDirectoryFlushSupported)
            {
                Assert.Ignore(
                    "No-follow open and directory flush are not available on this platform.");
            }
        }

        /// <summary>
        /// A real staging and final tree in a representative incomplete
        /// shape: no finished plan, the NVENC precommit temporary and both
        /// fixed staging chunk entries still there, and nothing committed on
        /// the final side. What left it this way is not part of the fixture's
        /// claim.
        /// </summary>
        private Sandbox MakeSandbox()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "zantetsuken-phase011-incomplete-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(root, "staging"), Path.Combine(root, "final"), 1);

            CaptureRunInitializationDocumentSet documents =
                CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);

            string chunksDirectory = Path.Combine(layout.StagingRunRoot, ChunksDirectoryName);
            Directory.CreateDirectory(chunksDirectory);
            Directory.CreateDirectory(layout.FinalRunRoot);

            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, RunInitializationMarkerName),
                documents.GetStagingInitializationBytes());
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, RunReadyMarkerName),
                documents.GetStagingReadyBytes());

            // Uncommitted NVENC artifacts at their fixed paths. Their content
            // is never read by this path, so any small bytes will do.
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, PrecommitTemporaryName),
                new byte[] { 0x7b, 0x22, 0x3f, 0x01 });
            File.WriteAllBytes(
                Path.Combine(chunksDirectory, PartialChunkName),
                new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88 });
            File.WriteAllBytes(
                Path.Combine(chunksDirectory, FinalizedChunkName),
                new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42 });

            File.WriteAllBytes(
                Path.Combine(layout.FinalRunRoot, RunInitializationMarkerName),
                documents.GetFinalInitializationBytes());
            File.WriteAllBytes(
                Path.Combine(layout.FinalRunRoot, RunReadyMarkerName),
                documents.GetFinalReadyBytes());

            // The state this recovery is about, as a file set: no finished
            // plan, and a final side holding nothing but its markers.
            Assert.That(
                File.Exists(Path.Combine(layout.StagingRunRoot, PublicationPlanName)), Is.False);
            Assert.That(
                File.Exists(Path.Combine(layout.FinalRunRoot, CaptureIndexName)), Is.False);
            Assert.That(
                File.Exists(Path.Combine(layout.FinalRunRoot, CaptureIndexTemporaryName)),
                Is.False);
            Assert.That(
                Directory.Exists(Path.Combine(layout.FinalRunRoot, ChunksDirectoryName)),
                Is.False);
            Assert.That(Directory.GetFileSystemEntries(layout.FinalRunRoot).Length, Is.EqualTo(2),
                "the final root holds its two markers and nothing else.");

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CaptureRunLockIdentityEvidence identity,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            Assert.That(openOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(openOutcome.Session, Is.Null);

            // Both trusted bases keep their runs directory, which the cleanup
            // must leave behind.
            Assert.That(Directory.Exists(Path.GetDirectoryName(layout.StagingRunRoot)), Is.True);
            Assert.That(Directory.Exists(Path.GetDirectoryName(layout.FinalRunRoot)), Is.True);

            return new Sandbox(
                layout, openOutcome, identity, owner, firstHandle, secondHandle);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lease, through the
        /// ordinary constructors and the same lock-handle seam the contract
        /// fixtures use. Both roots are described as they actually are on disk:
        /// canonical markers, with the staging chunks directory as the staging
        /// root's non-marker entry and nothing but markers in the final root.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CaptureRunLockIdentityEvidence identity,
            out CountingHandle firstHandle,
            out CountingHandle secondHandle)
        {
            CaptureRunMarkerBinding binding = new CaptureRunMarkerBinding(
                layout.TestRunId, InitId, layout.StagingRunRootSha256, layout.FinalRunRootSha256);

            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    new FakeRecoveryInspector(
                        MakeRootObservation(
                            CaptureRunRootRole.Staging,
                            binding.StagingInitialization,
                            binding.StagingReady,
                            hasNonMarkerEntry: true),
                        MakeRootObservation(
                            CaptureRunRootRole.Final,
                            binding.FinalInitialization,
                            binding.FinalReady,
                            hasNonMarkerEntry: false)),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            firstHandle = new CountingHandle(pathSet.FirstLockPath);
            secondHandle = new CountingHandle(pathSet.SecondLockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, firstHandle, secondHandle);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);

            identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(
                new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4));

            Assert.That(result.Status,
                Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired),
                "the fixture must reach a publication-recovery outcome.");

            return new CaptureRunInitializationOpenOutcome(result, null, identity);
        }

        private static CaptureRunInitializationRootObservation MakeRootObservation(
            CaptureRunRootRole role,
            CaptureRunInitializationMarker init,
            CaptureRunReadyMarker ready,
            bool hasNonMarkerEntry)
        {
            return new CaptureRunInitializationRootObservation(
                role, true, false, CaptureRunMarkerObservationStatus.Canonical, init,
                false, CaptureRunMarkerObservationStatus.Canonical, ready,
                hasNonMarkerEntry, false, false);
        }

        /// <summary>One Run's real tree, its open outcome, and its lock handles.</summary>
        private sealed class Sandbox
        {
            internal Sandbox(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunLockIdentityEvidence lockIdentityEvidence,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                LockIdentityEvidence = lockIdentityEvidence;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunLockIdentityEvidence LockIdentityEvidence { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal string ChunksPath => Path.Combine(Layout.StagingRunRoot, ChunksDirectoryName);

            internal string PrecommitTemporaryPath =>
                Path.Combine(Layout.StagingRunRoot, PrecommitTemporaryName);

            /// <summary>Everything the sandbox was seeded with is still there.</summary>
            internal void AssertSeededTreeIntact()
            {
                Assert.That(File.Exists(PrecommitTemporaryPath), Is.True);
                Assert.That(File.Exists(Path.Combine(ChunksPath, PartialChunkName)), Is.True);
                Assert.That(File.Exists(Path.Combine(ChunksPath, FinalizedChunkName)), Is.True);
                Assert.That(
                    File.Exists(Path.Combine(Layout.StagingRunRoot, RunInitializationMarkerName)),
                    Is.True);
                Assert.That(
                    File.Exists(Path.Combine(Layout.StagingRunRoot, RunReadyMarkerName)), Is.True);
                Assert.That(
                    File.Exists(Path.Combine(Layout.FinalRunRoot, RunInitializationMarkerName)),
                    Is.True);
                Assert.That(
                    File.Exists(Path.Combine(Layout.FinalRunRoot, RunReadyMarkerName)), Is.True);
                Assert.That(
                    File.Exists(Path.Combine(Layout.StagingRunRoot, PublicationPlanName)),
                    Is.False);
            }

            /// <summary>
            /// Both Run roots are gone with everything they held, and the
            /// trusted bases' runs directories remain.
            /// </summary>
            internal void AssertBothRunRootsDiscarded()
            {
                Assert.That(Directory.Exists(Layout.StagingRunRoot), Is.False);
                Assert.That(Directory.Exists(Layout.FinalRunRoot), Is.False);
                Assert.That(Directory.Exists(ChunksPath), Is.False);
                Assert.That(File.Exists(PrecommitTemporaryPath), Is.False);
                Assert.That(File.Exists(Path.Combine(ChunksPath, PartialChunkName)), Is.False);
                Assert.That(File.Exists(Path.Combine(ChunksPath, FinalizedChunkName)), Is.False);
                Assert.That(
                    File.Exists(Path.Combine(Layout.StagingRunRoot, RunReadyMarkerName)),
                    Is.False);
                Assert.That(
                    File.Exists(Path.Combine(Layout.StagingRunRoot, RunInitializationMarkerName)),
                    Is.False);
                Assert.That(
                    File.Exists(Path.Combine(Layout.FinalRunRoot, RunReadyMarkerName)), Is.False);
                Assert.That(
                    File.Exists(Path.Combine(Layout.FinalRunRoot, RunInitializationMarkerName)),
                    Is.False);

                Assert.That(
                    Directory.Exists(Path.GetDirectoryName(Layout.StagingRunRoot)), Is.True);
                Assert.That(
                    Directory.Exists(Path.GetDirectoryName(Layout.FinalRunRoot)), Is.True);
            }
        }

        /// <summary>
        /// A lock handle that counts its own releases: the one test seam this
        /// end-to-end path keeps, since acquiring and releasing a real OS lock
        /// is not what it is about.
        /// </summary>
        private sealed class CountingHandle : ICaptureRunLockHandle
        {
            private int _disposeCalls;

            internal CountingHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            internal int DisposeCallCount => _disposeCalls;

            public void Dispose()
            {
                _disposeCalls++;
            }
        }

        private sealed class FakeRecoveryInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            internal FakeRecoveryInspector(
                CaptureRunInitializationRootObservation staging,
                CaptureRunInitializationRootObservation final)
            {
                _staging = staging;
                _final = final;
            }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(
                CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                return new CaptureRunInitializationRecoveryInspectionSnapshot(
                    this, operation, _staging, _final);
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            public CaptureRunInitializationRecoveryCleanupReceipt Execute(
                CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(
                CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeMarkerWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }
    }
}
