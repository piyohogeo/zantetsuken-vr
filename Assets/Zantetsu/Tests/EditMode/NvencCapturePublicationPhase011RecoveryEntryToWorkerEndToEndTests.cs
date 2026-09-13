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
    /// entry to the Phase 0.11 recovery worker, driven only through the
    /// application-side owner: opening the Run starts the recovery, the
    /// terminal is collected from that owner, and once the recovery finishes
    /// the Run's two real OS locks can be acquired again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock acquisition, the initialization entry, the startup coordinator,
    /// and everything from the recovery factory onwards are the production
    /// concretes over a real temporary tree, with one process state and one
    /// verification buffer pool. Nothing is wired by hand here: the owner is
    /// what opens the Run, composes the worker, starts it, and hands back the
    /// terminal, and this fixture never reaches the worker itself. Unlike the
    /// other recovery end-to-end tests, the lock is the real one the entry
    /// acquired - there is no lock-handle seam - so the release is shown by
    /// re-acquiring both locks through the production backend afterwards.
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
        public void OpenedRun_RecoversThroughItsOwnerAndItsRealLockCanBeTakenAgain()
        {
            RequireCapabilities();

            Sandbox sandbox = MakeSandbox();
            byte[] canonicalPlan = CapturePublicationPlanCodec.SerializeCanonical(sandbox.Plan);

            // The representative recoverable shape, before anything runs.
            sandbox.AssertSeededTreeIntact();

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

            // This process may recover, and it shares one process state, one
            // verification buffer pool, and one factory.
            NvencCaptureProcessState processState = new NvencCaptureProcessState();
            Assert.That(processState.State, Is.EqualTo(NvencCaptureProcessStatus.Running));

            NvencRunPublicationRecoveryStartupCoordinator startup =
                new NvencRunPublicationRecoveryStartupCoordinator(
                    entry,
                    new NvencRunPublicationRecoveryWorkerFactory(
                        processState,
                        new CaptureArtifactVerificationBufferPool(VerificationBufferLength)));

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease callerLease = null;
            bool checksPassed = false;

            try
            {
                // The owner opens the Run through the regular entry and starts
                // the recovery itself.
                Assert.That(
                    startup.TryOpen(
                        sandbox.Layout,
                        MaximumRootEntryCount,
                        out outcome,
                        out callerLease),
                    Is.True);

                Assert.That(outcome, Is.Not.Null);
                Assert.That(outcome.Status,
                    Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
                Assert.That(outcome.Session, Is.Null);
                Assert.That(ReferenceEquals(outcome.RootLayout, sandbox.Layout), Is.True);
                Assert.That(callerLease, Is.Null,
                    "a started recovery owns the lease, so none is handed back.");
                Assert.That(freshStart.CallCount, Is.EqualTo(0),
                    "a publication recovery starts nothing fresh.");

                // The owner holds that exact Run's recovery.
                Assert.That(startup.HasActiveRecovery, Is.True);
                Assert.That(
                    ReferenceEquals(startup.ActiveRecoveryOpenOutcome, outcome), Is.True);

                CaptureRunInitializationSessionOwnershipLease recoveryLease =
                    startup.ActiveRecoveryOwnershipLease;
                Assert.That(recoveryLease, Is.Not.Null);
                Assert.That(outcome.LockIdentityEvidence, Is.Not.Null);
                Assert.That(
                    outcome.LockIdentityEvidence.IsBoundTo(recoveryLease), Is.True,
                    "the active lease is the one this Run's lock identity evidence was issued for.");

                // The exact lock path set the entry acquired; kept here
                // because the outcome stops being valid once its lock is
                // released.
                CaptureRunLockPathSet pathSet = outcome.LockPathSet;
                Assert.That(pathSet, Is.Not.Null);

                // While the recovery holds the Run's locks, nobody else can
                // take them.
                AssertBothLocksAreHeld(lockBackend, pathSet);

                // The main thread polls the owner; it never waits on the
                // worker and never reaches it.
                NvencRunPublicationRecoveryTerminalResult terminal =
                    CollectTerminal(startup);

                Assert.That(terminal.IsCaptureCompleted, Is.True);
                Assert.That(ReferenceEquals(terminal.OpenOutcome, outcome), Is.True);
                Assert.That(ReferenceEquals(terminal.OwnershipLease, recoveryLease), Is.True);

                // The published side: the index is the canonical plan and the
                // staging Run root is gone.
                Assert.That(File.Exists(sandbox.CaptureIndexPath), Is.True);
                Assert.That(File.ReadAllBytes(sandbox.CaptureIndexPath),
                    Is.EqualTo(canonicalPlan));
                Assert.That(File.Exists(sandbox.CaptureIndexTemporaryPath), Is.False);
                sandbox.AssertPublishedSideIntact();

                Assert.That(Directory.Exists(sandbox.Layout.StagingRunRoot), Is.False);
                Assert.That(
                    Directory.Exists(Path.GetDirectoryName(sandbox.Layout.StagingRunRoot)),
                    Is.True);

                // The lease is fully released, and the Run's two real locks can
                // be taken again.
                Assert.That(recoveryLease.IsReleaseComplete, Is.True);
                Assert.That(recoveryLease.CanRelease, Is.False);
                AssertBothLocksCanBeAcquiredAgain(lockBackend, pathSet);

                // The collection freed the slot: nothing is retained, and the
                // owner is back in the state that admits the next open.
                Assert.That(startup.HasActiveRecovery, Is.False);
                Assert.That(startup.ActiveRecoveryOpenOutcome, Is.Null);
                Assert.That(startup.ActiveRecoveryOwnershipLease, Is.Null);
                Assert.That(startup.RecoveryWorkerState,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));
                Assert.That(startup.TryGetRecoveryFailure(out Exception failure), Is.False,
                    failure?.ToString());
                Assert.That(failure, Is.Null);
                Assert.That(startup.TryRequestRecoveryReleaseRetry(), Is.False);

                // At most once.
                Assert.That(
                    startup.TryCollectRecoveryTerminal(
                        out NvencRunPublicationRecoveryTerminalResult again),
                    Is.False);
                Assert.That(again.IsValid, Is.False);

                checksPassed = true;
            }
            finally
            {
                // Anything the owner still holds is finished the only way it
                // offers - a bounded convergence and the explicit release
                // retry - and a lease that was handed back to this fixture is
                // this fixture's to release. What the teardown could not finish
                // is reported only when there is no earlier failure it would
                // hide.
                Exception cleanupFailure = FinishActiveRecovery(startup);
                Exception leaseFailure = ReleaseQuietly(callerLease);

                if (checksPassed && (cleanupFailure ?? leaseFailure) != null)
                {
                    throw new AssertionException(
                        "the recovery could not be finished at teardown.",
                        cleanupFailure ?? leaseFailure);
                }
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
        /// Neither of the Run's real locks can be taken while the recovery
        /// holds them. Contention is an ordinary false, never an exception, so
        /// no handle is left with this fixture.
        /// </summary>
        private static void AssertBothLocksAreHeld(
            CaptureRunLockOsBackend backend, CaptureRunLockPathSet pathSet)
        {
            Assert.That(
                backend.TryAcquire(
                    pathSet.FirstLockPath, out ICaptureRunLockHandle first),
                Is.False,
                "the recovery still holds the first lock.");
            Assert.That(first, Is.Null);
            Assert.That(
                backend.TryAcquire(
                    pathSet.SecondLockPath, out ICaptureRunLockHandle second),
                Is.False,
                "the recovery still holds the second lock.");
            Assert.That(second, Is.Null);
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

        /// <summary>
        /// Releases what this fixture owns and returns the failure instead of
        /// throwing it, so a teardown can never replace an earlier failure.
        /// </summary>
        private static Exception ReleaseQuietly(IDisposable resource)
        {
            try
            {
                resource?.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        /// <summary>
        /// Polls the owner the way a main thread would, with a bound, until the
        /// terminal is collectable. It never waits on the worker, and a failure
        /// is reported rather than waited out.
        /// </summary>
        private static NvencRunPublicationRecoveryTerminalResult CollectTerminal(
            NvencRunPublicationRecoveryStartupCoordinator startup)
        {
            Stopwatch watchdog = Stopwatch.StartNew();
            while (true)
            {
                if (startup.TryCollectRecoveryTerminal(
                        out NvencRunPublicationRecoveryTerminalResult terminal))
                {
                    return terminal;
                }

                if (startup.TryGetRecoveryFailure(out Exception failure))
                {
                    Assert.Fail(
                        "the recovery reported a failure instead of a terminal ("
                        + startup.RecoveryWorkerState + "): " + failure);
                }

                if (watchdog.ElapsedMilliseconds > WatchdogMilliseconds)
                {
                    Assert.Fail(
                        "no terminal was collectable in time; the recovery is "
                        + startup.RecoveryWorkerState + ".");
                }

                Thread.Yield();
            }
        }

        /// <summary>
        /// Finishes a recovery the owner may still hold, using only what the
        /// owner offers: a bounded convergence on the terminal, and one
        /// explicit release-retry request each time it is parked. Nothing is
        /// forced or unlocked, a faulted recovery is left exactly as it is, and
        /// the failure this could not resolve is returned rather than thrown.
        /// </summary>
        private static Exception FinishActiveRecovery(
            NvencRunPublicationRecoveryStartupCoordinator startup)
        {
            try
            {
                if (!startup.HasActiveRecovery)
                {
                    return null;
                }

                Stopwatch watchdog = Stopwatch.StartNew();
                while (startup.HasActiveRecovery)
                {
                    if (startup.TryCollectRecoveryTerminal(
                            out NvencRunPublicationRecoveryTerminalResult collected))
                    {
                        return null;
                    }

                    if (startup.RecoveryWorkerState
                        == NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry)
                    {
                        startup.TryRequestRecoveryReleaseRetry();
                    }
                    else if (startup.RecoveryWorkerState
                        == NvencRunPublicationRecoveryWorkerState.Faulted)
                    {
                        // The owner keeps a faulted recovery, and this fixture
                        // does not work around that.
                        return null;
                    }

                    if (watchdog.ElapsedMilliseconds > WatchdogMilliseconds)
                    {
                        return new TimeoutException(
                            "the recovery did not reach a terminal within "
                            + WatchdogMilliseconds + " ms; it is "
                            + startup.RecoveryWorkerState + ".");
                    }

                    Thread.Yield();
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex;
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
        /// The one fixture-local fake: it returns a canonical observation pair
        /// matching this sandbox - both roots initialized and ready for the
        /// same Run, with non-marker entries present - which routes this Run to
        /// a publication recovery. It builds that pair from the layout and this
        /// fixture's initialization ID; it opens, enumerates, and reads nothing,
        /// and infers no history.
        /// </summary>
        private sealed class FakeInitializationRecoveryInspector
            : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunMarkerBinding _binding;

            internal FakeInitializationRecoveryInspector(CaptureRunRootLayout layout)
            {
                _binding = new CaptureRunMarkerBinding(
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
