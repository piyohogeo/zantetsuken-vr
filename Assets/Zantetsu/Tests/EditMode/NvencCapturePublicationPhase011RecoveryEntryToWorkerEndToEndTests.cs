using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Managed end-to-end test from the regular Capture Run initialization
    /// entry to the Phase 0.11 recovery worker: the exact open outcome and
    /// ownership lease the entry returns are handed to the recovery worker
    /// factory, and once that recovery finishes the Run's two real OS locks can
    /// be acquired again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock acquisition, the initialization entry, and everything from the
    /// recovery factory onwards are the production concretes over a real
    /// temporary tree, with one process state and one verification buffer pool.
    /// Unlike the other recovery end-to-end tests, the lock here is the real one
    /// the entry acquired - there is no lock-handle seam - so the release is
    /// shown by re-acquiring both locks through the production backend
    /// afterwards.
    /// </para>
    /// <para>
    /// The one fixture-local fake is the generic initialization recovery
    /// inspector, because no production generic inspector exists yet and this
    /// unit does not introduce one. It reports the observation pair that routes
    /// to a publication recovery and nothing else: it infers no filesystem
    /// history and no NVENC disposition.
    /// </para>
    /// <para>
    /// One representative recoverable shape is covered - a canonical finished
    /// plan, the published chunk it declares, and no Capture Index - and why the
    /// tree looks that way is not part of this fixture's claim. The collision,
    /// deferred, and incomplete paths, the session and collision routes, and
    /// every deadline or retry concern belong elsewhere.
    /// </para>
    /// </remarks>
    public class NvencCapturePublicationPhase011RecoveryEntryToWorkerEndToEndTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string WriterHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const string CaptureIndexName = "capture.index";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string PublicationPlanName = "publication.plan";

        private const string PrecommitTemporaryName =
            "publication.plan.nvenc-precommit.tmp";

        private const string ChunksDirectoryName = "chunks";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

        private const int VerificationBufferLength = 64 * 1024;

        private const int MaximumRootEntryCount = 4;

        private const int WatchdogMilliseconds = 10000;

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
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
        public void OpenedRun_RecoversThroughItsWorkerAndItsRealLockCanBeTakenAgain()
        {
            RequireCapabilities();

            Sandbox sandbox = MakeSandbox();
            byte[] canonicalPlan = CapturePublicationPlanCodec.SerializeCanonical(sandbox.Plan);

            FakeInitializationRecoveryInspector inspector =
                new FakeInitializationRecoveryInspector(sandbox.Layout);
            RecordingFreshStart freshStart = new RecordingFreshStart();
            CaptureRunLockOsBackend lockBackend = CaptureRunLockOsBackend.Create();

            CaptureRunInitializationEntryCoordinator entry =
                new CaptureRunInitializationEntryCoordinator(
                    new CaptureRunLockAcquisitionCoordinator(lockBackend),
                    new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                        inspector,
                        new CaptureRunInitializationRecoveryExecutionCoordinator(
                            freshStart, freshStart, freshStart)),
                    new CaptureRunInitializationRecoverySessionRoutingCoordinator(
                        new CaptureRunInitializationRecoveryStartFreshCoordinator(
                            freshStart,
                            new CaptureRunInitializationExecutionCoordinator(
                                freshStart, freshStart))));

            // 1-2. The regular entry opens the Run: locks acquired, inspected,
            // and routed to a publication recovery the caller holds.
            Assert.That(
                entry.TryOpen(
                    sandbox.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome outcome,
                    out CaptureRunInitializationSessionOwnershipLease ownershipLease),
                Is.True);

            try
            {
                Assert.That(outcome, Is.Not.Null);
                Assert.That(ownershipLease, Is.Not.Null);
                Assert.That(outcome.Status,
                    Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
                Assert.That(outcome.Session, Is.Null);
                Assert.That(outcome.IsValid, Is.True);
                Assert.That(ReferenceEquals(outcome.RootLayout, sandbox.Layout), Is.True);

                // The evidence the entry produced is the one this lease was
                // issued for.
                Assert.That(outcome.LockIdentityEvidence, Is.Not.Null);
                Assert.That(
                    outcome.LockIdentityEvidence.IsIssuedFor(ownershipLease), Is.True);
                Assert.That(ownershipLease.IsCreated, Is.True);
                Assert.That(ownershipLease.CanRelease, Is.True);
                Assert.That(freshStart.CallCount, Is.EqualTo(0),
                    "a publication recovery starts nothing fresh.");

                // The exact lock path set the entry acquired; kept here
                // because the outcome stops being valid once its lock is
                // released.
                CaptureRunLockPathSet pathSet = outcome.LockPathSet;
                Assert.That(pathSet, Is.Not.Null);

                // While the Run holds its locks, nobody else can take them.
                Assert.That(
                    lockBackend.TryAcquire(
                        pathSet.FirstLockPath, out ICaptureRunLockHandle contended),
                    Is.False,
                    "the entry still holds the first lock.");
                Assert.That(contended, Is.Null);

                // 3. This process may recover.
                NvencCaptureProcessState processState = new NvencCaptureProcessState();
                Assert.That(processState.State, Is.EqualTo(NvencCaptureProcessStatus.Running));

                // 4. Composition alone changes nothing.
                NvencRunPublicationRecoveryWorkerService worker =
                    new NvencRunPublicationRecoveryWorkerFactory(
                            processState,
                            new CaptureArtifactVerificationBufferPool(VerificationBufferLength))
                        .Create(outcome, ownershipLease);

                try
                {
                    Assert.That(worker.State,
                        Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));
                    sandbox.AssertSeededTreeIntact();
                    Assert.That(ownershipLease.IsCreated, Is.True);
                    Assert.That(ownershipLease.IsReleaseComplete, Is.False);

                    // 5-8. The worker runs the whole recovery on its own
                    // thread and stops.
                    worker.Start();
                    WaitUntilStopped(worker);

                    Assert.That(worker.TryGetFailure(out Exception failure), Is.False,
                        failure?.ToString());
                    Assert.That(worker.State,
                        Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Completed));

                    Assert.That(
                        worker.TryCollectTerminal(
                            out NvencRunPublicationRecoveryTerminalResult terminal),
                        Is.True);
                    Assert.That(terminal.IsCaptureCompleted, Is.True);
                    Assert.That(ReferenceEquals(terminal.OpenOutcome, outcome), Is.True);
                    Assert.That(
                        ReferenceEquals(terminal.OwnershipLease, ownershipLease), Is.True);

                    // 9-10. The published side: the index is the canonical plan
                    // and the staging Run root is gone.
                    Assert.That(File.Exists(sandbox.CaptureIndexPath), Is.True);
                    Assert.That(File.ReadAllBytes(sandbox.CaptureIndexPath),
                        Is.EqualTo(canonicalPlan));
                    Assert.That(File.Exists(sandbox.CaptureIndexTemporaryPath), Is.False);
                    sandbox.AssertPublishedSideIntact();

                    Assert.That(Directory.Exists(sandbox.Layout.StagingRunRoot), Is.False);
                    Assert.That(
                        Directory.Exists(Path.GetDirectoryName(sandbox.Layout.StagingRunRoot)),
                        Is.True);

                    // 11-13. The lease is fully released, and the Run's two
                    // real locks can be taken again.
                    Assert.That(ownershipLease.IsReleaseComplete, Is.True);
                    Assert.That(ownershipLease.CanRelease, Is.False);
                    AssertBothLocksCanBeAcquiredAgain(lockBackend, pathSet);

                    // 14. At most once.
                    Assert.That(
                        worker.TryCollectTerminal(
                            out NvencRunPublicationRecoveryTerminalResult again),
                        Is.False);
                    Assert.That(again.IsValid, Is.False);
                }
                finally
                {
                    // 15. Dispose only after the physical stop.
                    WaitUntilStopped(worker);
                    worker.Dispose();
                }
            }
            finally
            {
                // The lease is the caller's to release; after a completed
                // recovery this is a no-op, and after a failure it is what
                // frees the real lock.
                ReleaseQuietly(ownershipLease);
            }
        }

        // ---- Fixture helpers ----

        private static void RequireCapabilities()
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
        /// Both of the Run's real locks are free again: each is acquired
        /// through the production backend and released here, whatever the
        /// assertions find.
        /// </summary>
        private static void AssertBothLocksCanBeAcquiredAgain(
            CaptureRunLockOsBackend backend, CaptureRunLockPathSet pathSet)
        {
            CaptureRunLockAcquisitionCoordinator coordinator =
                new CaptureRunLockAcquisitionCoordinator(backend);

            bool acquired = coordinator.TryAcquire(pathSet, out CaptureRunLockLease lease);

            try
            {
                Assert.That(acquired, Is.True,
                    "the released Run locks must be acquirable again.");
                Assert.That(lease, Is.Not.Null);
                Assert.That(lease.IsCreated, Is.True);
                Assert.That(ReferenceEquals(lease.PathSet, pathSet), Is.True);
            }
            finally
            {
                ReleaseQuietly(lease);
            }
        }

        private static void ReleaseQuietly(IDisposable resource)
        {
            try
            {
                resource?.Dispose();
            }
            catch (AggregateException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
        }

        private static void WaitUntilStopped(NvencRunPublicationRecoveryWorkerService worker)
        {
            Stopwatch watchdog = Stopwatch.StartNew();
            while (!worker.IsStopped)
            {
                if (watchdog.ElapsedMilliseconds > WatchdogMilliseconds)
                {
                    Assert.Fail("the recovery worker did not physically stop in time.");
                }

                Thread.Yield();
            }
        }

        /// <summary>
        /// A real staging and final tree in a representative recoverable
        /// shape: canonical markers in both roots, a canonical finished plan,
        /// the published chunk it declares, and no Capture Index or NVENC
        /// precommit temporary.
        /// </summary>
        private Sandbox MakeSandbox()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "zantetsuken-phase011-entry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(root, "staging"), Path.Combine(root, "final"), 1);

            byte[] chunkBytes = new byte[4096];
            for (int i = 0; i < chunkBytes.Length; i++)
            {
                chunkBytes[i] = (byte)((i * 29) + 11);
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

            CaptureRunInitializationDocumentSet documents =
                CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);

            Directory.CreateDirectory(Path.Combine(layout.StagingRunRoot, ChunksDirectoryName));
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, RunInitializationMarkerName),
                documents.GetStagingInitializationBytes());
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, RunReadyMarkerName),
                documents.GetStagingReadyBytes());
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
                documents.GetFinalInitializationBytes());
            File.WriteAllBytes(
                Path.Combine(layout.FinalRunRoot, RunReadyMarkerName),
                documents.GetFinalReadyBytes());

            Assert.That(
                File.Exists(Path.Combine(layout.FinalRunRoot, CaptureIndexName)), Is.False);
            Assert.That(
                File.Exists(Path.Combine(layout.FinalRunRoot, CaptureIndexTemporaryName)),
                Is.False);
            Assert.That(
                File.Exists(Path.Combine(layout.StagingRunRoot, PrecommitTemporaryName)),
                Is.False);

            return new Sandbox(layout, plan, chunkBytes);
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

        /// <summary>One Run's real tree and the plan its chunk belongs to.</summary>
        private sealed class Sandbox
        {
            private readonly byte[] _chunkBytes;
            private readonly byte[] _finalInitBytes;
            private readonly byte[] _finalReadyBytes;

            internal Sandbox(
                CaptureRunRootLayout layout, CapturePublicationPlan plan, byte[] chunkBytes)
            {
                Layout = layout;
                Plan = plan;
                _chunkBytes = chunkBytes;

                _finalInitBytes = File.ReadAllBytes(
                    Path.Combine(layout.FinalRunRoot, RunInitializationMarkerName));
                _finalReadyBytes = File.ReadAllBytes(
                    Path.Combine(layout.FinalRunRoot, RunReadyMarkerName));
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CapturePublicationPlan Plan { get; }

            internal string CaptureIndexPath => Path.Combine(Layout.FinalRunRoot, CaptureIndexName);

            internal string CaptureIndexTemporaryPath =>
                Path.Combine(Layout.FinalRunRoot, CaptureIndexTemporaryName);

            internal string FinalChunkPath => Path.Combine(
                Layout.FinalRunRoot,
                NvencRunChunkArtifactDescriptorFactory.FinalRelativePath.Replace(
                    '/', Path.DirectorySeparatorChar));

            internal void AssertSeededTreeIntact()
            {
                Assert.That(
                    File.Exists(Path.Combine(Layout.StagingRunRoot, PublicationPlanName)),
                    Is.True);
                Assert.That(
                    File.Exists(Path.Combine(Layout.StagingRunRoot, RunReadyMarkerName)),
                    Is.True);
                Assert.That(File.Exists(CaptureIndexPath), Is.False);
                Assert.That(File.Exists(CaptureIndexTemporaryPath), Is.False);
                AssertPublishedSideIntact();
            }

            /// <summary>
            /// The published chunk and the final markers by content, not by
            /// timestamp.
            /// </summary>
            internal void AssertPublishedSideIntact()
            {
                Assert.That(File.Exists(FinalChunkPath), Is.True);
                byte[] chunk = File.ReadAllBytes(FinalChunkPath);
                Assert.That(chunk, Is.EqualTo(_chunkBytes));
                Assert.That(chunk.Length, Is.EqualTo(_chunkBytes.Length));
                Assert.That(new FileInfo(FinalChunkPath).Length,
                    Is.EqualTo(_chunkBytes.LongLength));
                Assert.That(Sha256Hex(chunk), Is.EqualTo(Sha256Hex(_chunkBytes)));

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
        /// The one fixture-local fake: it reports the observation pair that
        /// routes this Run to a publication recovery - both roots initialized
        /// and ready for the same Run, with a non-marker entry on the staging
        /// side - derived from the markers this sandbox was seeded with. It
        /// reads no file and infers no history.
        /// </summary>
        private sealed class FakeInitializationRecoveryInspector
            : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunMarkerBinding _binding;

            internal FakeInitializationRecoveryInspector(CaptureRunRootLayout layout)
            {
                _binding = CaptureRunMarkerBindingFactory.Create(
                    layout.TestRunId,
                    InitId,
                    layout.StagingRunRootSha256,
                    layout.FinalRunRootSha256);
            }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(
                CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                return new CaptureRunInitializationRecoveryInspectionSnapshot(
                    this,
                    operation,
                    MakeObservation(
                        CaptureRunRootRole.Staging,
                        _binding.StagingInitialization,
                        _binding.StagingReady,
                        hasNonMarkerEntry: true),
                    MakeObservation(
                        CaptureRunRootRole.Final,
                        _binding.FinalInitialization,
                        _binding.FinalReady,
                        hasNonMarkerEntry: true));
            }

            private static CaptureRunInitializationRootObservation MakeObservation(
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
        }

        /// <summary>
        /// Every collaborator a publication recovery must not reach: the
        /// initialization cleanup, provisioning, marker writing, and fresh
        /// initialization ID. Any call is a failure of the path under test.
        /// </summary>
        private sealed class RecordingFreshStart
            : ICaptureRunInitializationRecoveryCleanupBackend,
              ICaptureRunRootProvisioner,
              ICaptureRunMarkerAtomicWriter,
              ICaptureRunInitializationIdSource
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public CaptureRunInitializationRecoveryCleanupReceipt Execute(
                CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException(
                    "A publication recovery performs no initialization cleanup.");
            }

            public CaptureRunRootProvisionReceipt ProvisionNew(
                CaptureRunRootProvisionOperation operation)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException(
                    "A publication recovery provisions no Run root.");
            }

            public CaptureRunMarkerWriteReceipt WriteAtomic(
                CaptureRunMarkerWriteOperation operation)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("A publication recovery writes no marker.");
            }

            public string Create()
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException(
                    "A publication recovery issues no fresh initialization ID.");
            }
        }
    }
}
