using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Managed end-to-end test of the Phase 0.11 NVENC recovery happy path over
    /// a real filesystem: from the publication recovery inspection through the
    /// Capture Index commit, CaptureComplete, the staging cleanup, and the
    /// release of the Run's Session Ownership Lease.
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
    /// GPU, Worker, Publication Service, or process state is involved.
    /// </para>
    /// <para>
    /// One representative healthy path is covered here - a Run whose Capture
    /// Index still has to be committed. The collision, deferred,
    /// already-authoritative, failed-cleanup, and partial-release shapes belong
    /// to the contract fixtures that own them.
    /// </para>
    /// </remarks>
    public class NvencCapturePublicationPhase011RecoveryEndToEndTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string WriterHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const string CaptureIndexName = "capture.index";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string PublicationPlanName = "publication.plan";

        private const string PublicationPlanTemporaryName =
            "publication.plan.nvenc-precommit.tmp";

        private const string ChunksDirectoryName = "chunks";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

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
        public void RecoveredRun_CommitsItsCaptureIndexCompletesCleansUpAndReleasesTheLock()
        {
            RequireNoFollowCapabilities();

            Sandbox sandbox = MakeSandbox();

            // ---- 1. Publication recovery: what is actually on disk ----
            NvencRunPublicationRecoveryDecision publicationRecovery =
                new NvencRunPublicationRecoveryOrchestrationCoordinator(
                        new NvencRunPublicationRecoveryInspectionExecutionCoordinator(
                            new NvencRunPublicationRecoveryInspector(
                                CaptureArtifactNoFollowOpen.Create(),
                                new CaptureArtifactVerificationBufferPool(
                                    VerificationBufferLength))))
                    .Execute(sandbox.OpenOutcome);

            Assert.That(publicationRecovery.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));
            Assert.That(publicationRecovery.IsValid, Is.True);

            // The authoritative plan is the one read from disk, and its chunk
            // verified against it.
            NvencRunPublicationRecoveryInspectionSnapshot publicationSnapshot =
                publicationRecovery.Snapshot;
            Assert.That(ReferenceEquals(
                    publicationRecovery.AuthoritativePlan, publicationSnapshot.PublicationPlan),
                Is.True);
            Assert.That(publicationSnapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Canonical));
            Assert.That(publicationSnapshot.PrecommitTemporaryPresent, Is.False);
            Assert.That(publicationSnapshot.ChunkVerification.Status,
                Is.EqualTo(CaptureArtifactVerificationStatus.MatchesExpected));
            Assert.That(publicationSnapshot.ChunkVerification.ObservedByteLength,
                Is.EqualTo(sandbox.ChunkBytes.LongLength));

            byte[] canonicalPlan = CapturePublicationPlanCodec.SerializeCanonical(
                publicationRecovery.AuthoritativePlan);

            // ---- 2. Capture Index recovery: no index and no temporary yet ----
            NvencRunCaptureIndexRecoveryDecision captureIndexRecovery =
                new NvencRunCaptureIndexRecoveryOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(
                            new NvencRunCaptureIndexRecoveryInspector(
                                CaptureArtifactNoFollowOpen.Create())))
                    .Execute(publicationRecovery);

            Assert.That(captureIndexRecovery.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            Assert.That(captureIndexRecovery.CommitMode,
                Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
            Assert.That(captureIndexRecovery.Snapshot.FinalIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.Absent));
            Assert.That(captureIndexRecovery.Snapshot.TemporaryIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.Absent));

            // ---- 3. Commit the Capture Index, then CaptureComplete ----
            NvencRunCaptureIndexRecoveryCommitter committer =
                new NvencRunCaptureIndexRecoveryCommitter(sandbox.Layout);

            NvencRunCaptureCompleteRecoveryReceipt captureComplete =
                new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)),
                        new NvencRunCaptureCompleteRecoveryExecutionCoordinator(
                            new NvencRunCaptureCompleteRecoveryCompleter()))
                    .Execute(captureIndexRecovery);

            Assert.That(captureComplete.IsValid, Is.True);
            Assert.That(captureComplete.HasCommitReceipt, Is.True);

            NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt =
                captureComplete.CaptureIndexRecoveryCommitReceipt;
            Assert.That(commitReceipt, Is.Not.Null);
            Assert.That(commitReceipt.IsIssuedFor(committer, commitReceipt.Operation), Is.True);
            Assert.That(ReferenceEquals(
                    commitReceipt.Operation.CaptureIndexRecoveryDecision, captureIndexRecovery),
                Is.True);
            Assert.That(ReferenceEquals(
                    captureComplete.CaptureIndexRecoveryDecision, captureIndexRecovery),
                Is.True);

            // The committed index is exactly the authoritative plan's canonical
            // bytes, and the temporary is gone.
            Assert.That(File.Exists(sandbox.CaptureIndexPath), Is.True);
            Assert.That(File.ReadAllBytes(sandbox.CaptureIndexPath), Is.EqualTo(canonicalPlan));
            Assert.That(File.Exists(sandbox.CaptureIndexTemporaryPath), Is.False);

            // ---- 4. Clean up the staging side ----
            NvencRunCaptureCompleteRecoveryCleaner cleaner =
                new NvencRunCaptureCompleteRecoveryCleaner(sandbox.Layout);

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanup =
                new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                        new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner))
                    .Execute(captureComplete);

            Assert.That(cleanup.IsCleaned, Is.True);
            Assert.That(cleanup.Receipt, Is.Not.Null);
            Assert.That(cleanup.Receipt.IsIssuedFor(cleaner, cleanup.Operation), Is.True);
            Assert.That(ReferenceEquals(cleanup.CaptureCompleteReceipt, captureComplete), Is.True);
            Assert.That(ReferenceEquals(
                    cleanup.CaptureIndexRecoveryDecision, captureIndexRecovery),
                Is.True);
            Assert.That(ReferenceEquals(
                    cleanup.AuthoritativePlan, publicationRecovery.AuthoritativePlan),
                Is.True);
            Assert.That(ReferenceEquals(cleanup.RootLayout, sandbox.Layout), Is.True);
            Assert.That(cleanup.TestRunId, Is.EqualTo(sandbox.Layout.TestRunId));
            Assert.That(cleanup.RunInitializationId, Is.EqualTo(InitId));

            Assert.That(Directory.Exists(sandbox.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(sandbox.StagingRunRootParent), Is.True);
            sandbox.AssertPublishedSideIntact(canonicalPlan);

            // ---- 5. Release the Run's Session Ownership Lease ----
            NvencRunCaptureCompleteRecoveryOwnershipReleaser releaser =
                new NvencRunCaptureCompleteRecoveryOwnershipReleaser();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator releaseCoordinator =
                new NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator(
                    cleanup,
                    sandbox.Owner,
                    new NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator(
                        releaser));

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt releaseReceipt =
                releaseCoordinator.Release();

            Assert.That(releaseReceipt.IsValid, Is.True);
            Assert.That(
                releaseReceipt.IsIssuedFor(releaser, releaseCoordinator.Operation), Is.True);
            Assert.That(releaseCoordinator.IsReleased, Is.True);

            // The lease and its handles, which is what this seam holds.
            Assert.That(sandbox.Owner.IsReleaseComplete, Is.True);
            Assert.That(sandbox.Owner.CanRelease, Is.False);
            Assert.That(sandbox.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(sandbox.SecondHandle.DisposeCallCount, Is.EqualTo(1));

            // ---- 6. A second release changes nothing ----
            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt again =
                releaseCoordinator.Release();

            Assert.That(ReferenceEquals(again, releaseReceipt), Is.True);
            Assert.That(sandbox.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(sandbox.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(Directory.Exists(sandbox.Layout.StagingRunRoot), Is.False);
            sandbox.AssertPublishedSideIntact(canonicalPlan);

            // The committed index is still byte-for-byte the plan it was built
            // from.
            Assert.That(File.ReadAllBytes(sandbox.CaptureIndexPath), Is.EqualTo(canonicalPlan));
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
                    "No-follow commit and directory flush are not available on this platform.");
            }
        }

        /// <summary>
        /// A real staging and final tree for one Run left exactly as a crash
        /// after the artifact publication leaves it: a finished publication
        /// plan, the published chunk, and no Capture Index.
        /// </summary>
        private Sandbox MakeSandbox()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "zantetsuken-phase011-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(root, "staging"), Path.Combine(root, "final"), 1);

            byte[] chunkBytes = new byte[4096];
            for (int i = 0; i < chunkBytes.Length; i++)
            {
                chunkBytes[i] = (byte)((i * 31) + 7);
            }

            CapturePublicationPlan plan = new CapturePublicationPlan(
                layout.TestRunId,
                InitId,
                WriterHash,
                new[]
                {
                    NvencRunChunkArtifactDescriptorFactory.Create(
                        ArtifactId, chunkBytes.LongLength, Sha256Hex(chunkBytes)),
                },
                MakeFrameEvidence());

            CaptureRunMarkerBinding documents = new CaptureRunMarkerBinding(
                layout.TestRunId,
                InitId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);

            Directory.CreateDirectory(Path.Combine(layout.StagingRunRoot, ChunksDirectoryName));
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, RunInitializationMarkerName),
                CaptureRunInitializationMarkerCodec.SerializeCanonical(documents.StagingInitialization));
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, RunReadyMarkerName),
                CaptureRunReadyMarkerCodec.SerializeCanonical(documents.StagingReady));
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, PublicationPlanName),
                CapturePublicationPlanCodec.SerializeCanonical(plan));

            string finalChunkPath = Path.Combine(
                layout.FinalRunRoot,
                NvencRunChunkArtifactDescriptorFactory.FinalRelativePath.Replace(
                    '/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(finalChunkPath));
            File.WriteAllBytes(finalChunkPath, chunkBytes);
            File.WriteAllBytes(
                Path.Combine(layout.FinalRunRoot, RunInitializationMarkerName),
                CaptureRunInitializationMarkerCodec.SerializeCanonical(documents.FinalInitialization));
            File.WriteAllBytes(
                Path.Combine(layout.FinalRunRoot, RunReadyMarkerName),
                CaptureRunReadyMarkerCodec.SerializeCanonical(documents.StagingReady));

            // The state this recovery is about: nothing committed, and no
            // temporary of either kind left behind.
            Assert.That(
                File.Exists(Path.Combine(layout.FinalRunRoot, CaptureIndexName)), Is.False);
            Assert.That(
                File.Exists(Path.Combine(layout.FinalRunRoot, CaptureIndexTemporaryName)),
                Is.False);
            Assert.That(
                File.Exists(Path.Combine(layout.StagingRunRoot, PublicationPlanTemporaryName)),
                Is.False);

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            Assert.That(openOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(openOutcome.Session, Is.Null);

            return new Sandbox(layout, openOutcome, owner, firstHandle, secondHandle, chunkBytes);
        }

        private static CaptureFrameEvidenceEntry[] MakeFrameEvidence()
        {
            CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[3];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new CaptureFrameEvidenceEntry(i + 1, new[] { ArtifactId });
            }

            return entries;
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lease, through the
        /// ordinary constructors and the same lock-handle seam the contract
        /// fixtures use. Both roots are described as they actually are on
        /// disk: canonical markers plus the non-marker entries the staging
        /// chunks directory and the published chunk are.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CountingHandle firstHandle,
            out CountingHandle secondHandle)
        {
            CaptureRunMarkerBinding binding = new CaptureRunMarkerBinding(
                layout.TestRunId, InitId, layout.StagingRunRootSha256, layout.FinalRunRootSha256);

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
                CaptureRunRootRole.Staging,
                binding.StagingInitialization,
                binding.StagingReady,
                hasNonMarkerEntry: true);
            CaptureRunInitializationRootObservation final = MakeRootObservation(
                CaptureRunRootRole.Final,
                binding.FinalInitialization,
                binding.FinalReady,
                hasNonMarkerEntry: true);

            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    new FakeRecoveryInspector(staging, final),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            firstHandle = new CountingHandle(pathSet.FirstLockPath);
            secondHandle = new CountingHandle(pathSet.SecondLockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, firstHandle, secondHandle);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);

            CaptureRunLockIdentityEvidence identity =
                CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

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

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                char[] hex = new char[hash.Length * 2];
                const string Digits = "0123456789abcdef";
                for (int i = 0; i < hash.Length; i++)
                {
                    hex[i * 2] = Digits[hash[i] >> 4];
                    hex[(i * 2) + 1] = Digits[hash[i] & 0xF];
                }

                return new string(hex);
            }
        }

        /// <summary>One Run's real tree, its open outcome, and its lock handles.</summary>
        private sealed class Sandbox
        {
            private readonly byte[] _finalInitBytes;
            private readonly byte[] _finalReadyBytes;

            internal Sandbox(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle,
                byte[] chunkBytes)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
                ChunkBytes = chunkBytes;

                _finalInitBytes = File.ReadAllBytes(
                    Path.Combine(layout.FinalRunRoot, RunInitializationMarkerName));
                _finalReadyBytes = File.ReadAllBytes(
                    Path.Combine(layout.FinalRunRoot, RunReadyMarkerName));
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal byte[] ChunkBytes { get; }

            internal string StagingRunRootParent => Path.GetDirectoryName(Layout.StagingRunRoot);

            internal string CaptureIndexPath => Path.Combine(Layout.FinalRunRoot, CaptureIndexName);

            internal string CaptureIndexTemporaryPath =>
                Path.Combine(Layout.FinalRunRoot, CaptureIndexTemporaryName);

            internal string FinalChunkPath => Path.Combine(
                Layout.FinalRunRoot,
                NvencRunChunkArtifactDescriptorFactory.FinalRelativePath.Replace(
                    '/', Path.DirectorySeparatorChar));

            /// <summary>
            /// The published side by content, not by timestamp: exact bytes,
            /// length, and SHA-256 for the chunk and for the committed index
            /// against the canonical plan it was built from, and the final
            /// markers still in place.
            /// </summary>
            internal void AssertPublishedSideIntact(byte[] expectedCaptureIndex)
            {
                Assert.That(File.Exists(FinalChunkPath), Is.True);
                byte[] chunk = File.ReadAllBytes(FinalChunkPath);
                Assert.That(chunk, Is.EqualTo(ChunkBytes));
                Assert.That(chunk.Length, Is.EqualTo(ChunkBytes.Length));
                Assert.That(new FileInfo(FinalChunkPath).Length,
                    Is.EqualTo(ChunkBytes.LongLength));
                Assert.That(Sha256Hex(chunk), Is.EqualTo(Sha256Hex(ChunkBytes)));

                Assert.That(File.Exists(CaptureIndexPath), Is.True);
                byte[] index = File.ReadAllBytes(CaptureIndexPath);
                Assert.That(index, Is.EqualTo(expectedCaptureIndex));
                Assert.That(new FileInfo(CaptureIndexPath).Length,
                    Is.EqualTo(expectedCaptureIndex.LongLength));
                Assert.That(Sha256Hex(index), Is.EqualTo(Sha256Hex(expectedCaptureIndex)));

                Assert.That(File.Exists(CaptureIndexTemporaryPath), Is.False);

                Assert.That(
                    File.ReadAllBytes(
                        Path.Combine(Layout.FinalRunRoot, RunInitializationMarkerName)),
                    Is.EqualTo(_finalInitBytes));
                Assert.That(
                    File.ReadAllBytes(Path.Combine(Layout.FinalRunRoot, RunReadyMarkerName)),
                    Is.EqualTo(_finalReadyBytes));
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
