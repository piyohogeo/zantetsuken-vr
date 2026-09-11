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
    /// Contract tests for the recovery worker composition factory: what it
    /// refuses before building anything, that a built worker is unstarted and
    /// has done nothing, that two calls share nothing, and one representative
    /// real-tree pass proving the composed graph actually recovers a Run.
    /// </summary>
    /// <remarks>
    /// The composition tests use recording collaborators to show that building
    /// opens no file; the sandbox test uses the production wiring over a real
    /// temporary tree. The classification table and the branch semantics belong
    /// to the existing end-to-end tests and worker fixture and are not repeated
    /// here.
    /// </remarks>
    public class NvencRunPublicationRecoveryWorkerFactoryContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string WriterHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const string CaptureIndexName = "capture.index";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string PublicationPlanName = "publication.plan";

        private const string ChunksDirectoryName = "chunks";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

        private const int VerificationBufferLength = 64 * 1024;

        private const int WatchdogMilliseconds = 10000;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<string> _sandboxes = new List<string>();

        private int _runIds;

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

        // ---- Construction ----

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            RecordingOpener opener = new RecordingOpener();
            CaptureArtifactVerificationBufferPool pool =
                new CaptureArtifactVerificationBufferPool(VerificationBufferLength);
            RecordingCommitFileSystem commit = new RecordingCommitFileSystem();
            RecordingCleanupFileSystem cleanup = new RecordingCleanupFileSystem();

            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryWorkerFactory(
                        null, pool, commit, cleanup)).ParamName,
                Is.EqualTo("opener"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryWorkerFactory(
                        opener, null, commit, cleanup)).ParamName,
                Is.EqualTo("verificationBufferPool"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryWorkerFactory(
                        opener, pool, null, cleanup)).ParamName,
                Is.EqualTo("commitFileSystem"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryWorkerFactory(
                        opener, pool, commit, null)).ParamName,
                Is.EqualTo("cleanupFileSystem"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryWorkerFactory(null)).ParamName,
                Is.EqualTo("verificationBufferPool"));
        }

        // ---- Admission, before anything is built ----

        [Test]
        public void Create_NullArguments_RejectedWithoutTouchingAnything()
        {
            Harness h = MakeHarness();

            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => h.Factory.Create(null, h.Owner)).ParamName,
                Is.EqualTo("openOutcome"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => h.Factory.Create(h.OpenOutcome, null)).ParamName,
                Is.EqualTo("ownershipLease"));

            h.AssertNothingTouched("after null arguments");
        }

        [Test]
        public void Create_OutcomeInvalidatedByAReleasedLock_RejectedWithoutTouchingAnything()
        {
            Harness h = MakeHarness();

            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }

            Assert.That(h.OpenOutcome.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => h.Factory.Create(h.OpenOutcome, h.Owner));

            Assert.That(ex.ParamName, Is.EqualTo("openOutcome"));
            Assert.That(h.Opener.CallCount, Is.EqualTo(0));
            Assert.That(h.CommitFileSystem.CallCount, Is.EqualTo(0));
            Assert.That(h.CleanupFileSystem.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Create_AnotherLeaseOverTheSameLayout_RejectedWithoutTouchingAnything()
        {
            Harness h = MakeHarness();

            // A second live lease over this Run's own layout: a different path
            // set instance, so it is not the lock this outcome was opened
            // under.
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(h.Layout);
            CountingHandle foreignFirst = new CountingHandle(pathSet.FirstLockPath);
            CountingHandle foreignSecond = new CountingHandle(pathSet.SecondLockPath);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet, foreignFirst, foreignSecond);
            CaptureRunInitializationSessionOwnershipLease foreignOwner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(foreignOwner);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => h.Factory.Create(h.OpenOutcome, foreignOwner));

            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));
            h.AssertNothingTouched("after a foreign lease");
            Assert.That(foreignFirst.DisposeCallCount, Is.EqualTo(0));
            Assert.That(foreignSecond.DisposeCallCount, Is.EqualTo(0));
            Assert.That(foreignOwner.IsCreated, Is.True);
        }

        // ---- What Create produces, and what it does not do ----

        [Test]
        public void Create_ReturnsAnUnstartedWorkerThatHasDoneNothing()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryWorkerService worker =
                h.Factory.Create(h.OpenOutcome, h.Owner);

            Assert.That(worker, Is.Not.Null);
            Assert.That(worker.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));
            Assert.That(worker.IsStopped, Is.False, "no thread exists yet.");
            Assert.That(worker.TryGetFailure(out Exception failure), Is.False);
            Assert.That(failure, Is.Null);
            Assert.That(
                worker.TryCollectTerminal(
                    out NvencRunPublicationRecoveryTerminalResult terminal),
                Is.False);
            Assert.That(terminal.IsValid, Is.False);

            // Composition alone inspects, commits, cleans up, releases, and
            // changes nothing.
            h.AssertNothingTouched("after Create");
            Assert.That(worker.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));

            worker.Dispose();
        }

        [Test]
        public void Create_CalledTwice_ReturnsSeparateWorkersThatShareNoState()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryWorkerService first =
                h.Factory.Create(h.OpenOutcome, h.Owner);
            NvencRunPublicationRecoveryWorkerService second =
                h.Factory.Create(h.OpenOutcome, h.Owner);

            Assert.That(ReferenceEquals(first, second), Is.False);

            // Starting nothing, the two are independent observations.
            Assert.That(first.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));
            Assert.That(second.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));

            // Disposing one leaves the other usable.
            first.Dispose();
            Assert.That(first.IsStopped, Is.True);
            Assert.That(second.IsStopped, Is.False);
            Assert.That(second.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));

            second.Dispose();
            h.AssertNothingTouched("after two compositions");
        }

        [Test]
        public void Factory_HoldsOnlyItsReadonlyCollaborators()
        {
            System.Reflection.FieldInfo[] fields =
                typeof(NvencRunPublicationRecoveryWorkerFactory).GetFields(
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);

            List<Type> types = new List<Type>();
            foreach (System.Reflection.FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "the factory holds no mutable state.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(ICaptureArtifactNoFollowOpener),
                typeof(CaptureArtifactVerificationBufferPool),
                typeof(ICaptureIndexCommitFileSystem),
                typeof(ICaptureCompleteCleanupFileSystem),
            }));
        }

        // ---- One representative real-tree pass ----

        [Test]
        public void ComposedWorker_RecoversARealRunAndReleasesItsLease()
        {
            RequireNoFollowCapabilities();

            Sandbox sandbox = MakeSandbox();

            NvencRunPublicationRecoveryWorkerFactory factory =
                new NvencRunPublicationRecoveryWorkerFactory(
                    new CaptureArtifactVerificationBufferPool(VerificationBufferLength));

            NvencRunPublicationRecoveryWorkerService worker =
                factory.Create(sandbox.OpenOutcome, sandbox.Owner);

            try
            {
                Assert.That(worker.State,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));

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

                // The recoverable path: Capture Index committed, staging
                // cleaned up, lease released.
                Assert.That(terminal.IsCaptureCompleted, Is.True);
                Assert.That(ReferenceEquals(terminal.OpenOutcome, sandbox.OpenOutcome), Is.True);
                Assert.That(ReferenceEquals(terminal.OwnershipLease, sandbox.Owner), Is.True);

                byte[] canonicalPlan = CapturePublicationPlanCodec.SerializeCanonical(
                    terminal.PublicationRecoveryDecision.AuthoritativePlan);
                Assert.That(File.Exists(sandbox.CaptureIndexPath), Is.True);
                Assert.That(File.ReadAllBytes(sandbox.CaptureIndexPath),
                    Is.EqualTo(canonicalPlan));
                Assert.That(File.Exists(sandbox.CaptureIndexTemporaryPath), Is.False);

                Assert.That(Directory.Exists(sandbox.Layout.StagingRunRoot), Is.False);
                Assert.That(
                    Directory.Exists(Path.GetDirectoryName(sandbox.Layout.StagingRunRoot)),
                    Is.True);
                sandbox.AssertPublishedChunkIntact();

                Assert.That(sandbox.Owner.IsReleaseComplete, Is.True);
                Assert.That(sandbox.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(sandbox.SecondHandle.DisposeCallCount, Is.EqualTo(1));

                // At most once.
                Assert.That(
                    worker.TryCollectTerminal(
                        out NvencRunPublicationRecoveryTerminalResult again),
                    Is.False);
                Assert.That(again.IsValid, Is.False);
            }
            finally
            {
                WaitUntilStopped(worker);
                worker.Dispose();
            }
        }

        // ---- Fixture helpers ----

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

        private Harness MakeHarness()
        {
            CaptureRunRootLayout layout = MakeFakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            return new Harness(layout, openOutcome, owner, firstHandle, secondHandle);
        }

        /// <summary>
        /// A real staging and final tree in a representative recoverable
        /// shape: a canonical finished plan, the published chunk it declares,
        /// and no Capture Index. Why the tree looks this way is not part of
        /// this fixture's claim.
        /// </summary>
        private Sandbox MakeSandbox()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "zantetsuken-phase011-factory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(root, "staging"), Path.Combine(root, "final"), 1);

            byte[] chunkBytes = new byte[4096];
            for (int i = 0; i < chunkBytes.Length; i++)
            {
                chunkBytes[i] = (byte)((i * 13) + 5);
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

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            return new Sandbox(
                layout, openOutcome, owner, firstHandle, secondHandle, chunkBytes);
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
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors and the same lock-handle seam the contract
        /// fixtures use.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CountingHandle firstHandle,
            out CountingHandle secondHandle)
        {
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
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
                            hasNonMarkerEntry: true)),
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

        private CaptureRunRootLayout MakeFakeLayout()
        {
            string root = Path.DirectorySeparatorChar == '\\'
                ? "C:\\zantetsuken-fake"
                : "/zantetsuken-fake";
            _runIds++;

            return new CaptureRunRootLayout(
                Path.Combine(root, "staging-" + _runIds),
                Path.Combine(root, "final-" + _runIds),
                1);
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

        /// <summary>
        /// One Run over no real tree, with recording collaborators so
        /// composition can be shown to touch none of them.
        /// </summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;

                Factory = new NvencRunPublicationRecoveryWorkerFactory(
                    Opener,
                    new CaptureArtifactVerificationBufferPool(VerificationBufferLength),
                    CommitFileSystem,
                    CleanupFileSystem);
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal RecordingOpener Opener { get; } = new RecordingOpener();

            internal RecordingCommitFileSystem CommitFileSystem { get; } =
                new RecordingCommitFileSystem();

            internal RecordingCleanupFileSystem CleanupFileSystem { get; } =
                new RecordingCleanupFileSystem();

            internal NvencRunPublicationRecoveryWorkerFactory Factory { get; }

            internal void AssertNothingTouched(string message)
            {
                Assert.That(Opener.CallCount, Is.EqualTo(0), message);
                Assert.That(CommitFileSystem.CallCount, Is.EqualTo(0), message);
                Assert.That(CleanupFileSystem.CallCount, Is.EqualTo(0), message);

                Assert.That(FirstHandle.DisposeCallCount, Is.EqualTo(0), message);
                Assert.That(SecondHandle.DisposeCallCount, Is.EqualTo(0), message);
                Assert.That(Owner.IsCreated, Is.True, message);
                Assert.That(Owner.CanRelease, Is.True, message);
                Assert.That(OpenOutcome.IsValid, Is.True, message);

                Assert.That(Directory.Exists(Layout.StagingRunRoot), Is.False, message);
                Assert.That(Directory.Exists(Layout.FinalRunRoot), Is.False, message);
            }
        }

        /// <summary>One Run over a real temporary tree.</summary>
        private sealed class Sandbox
        {
            private readonly byte[] _chunkBytes;

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
                _chunkBytes = chunkBytes;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal string CaptureIndexPath => Path.Combine(Layout.FinalRunRoot, CaptureIndexName);

            internal string CaptureIndexTemporaryPath =>
                Path.Combine(Layout.FinalRunRoot, CaptureIndexTemporaryName);

            internal string FinalChunkPath => Path.Combine(
                Layout.FinalRunRoot,
                NvencRunChunkArtifactDescriptorFactory.FinalRelativePath.Replace(
                    '/', Path.DirectorySeparatorChar));

            internal void AssertPublishedChunkIntact()
            {
                Assert.That(File.Exists(FinalChunkPath), Is.True);
                byte[] chunk = File.ReadAllBytes(FinalChunkPath);
                Assert.That(chunk, Is.EqualTo(_chunkBytes));
                Assert.That(Sha256Hex(chunk), Is.EqualTo(Sha256Hex(_chunkBytes)));
            }
        }

        /// <summary>
        /// A no-follow opener that opens nothing and records that it was asked.
        /// </summary>
        private sealed class RecordingOpener : ICaptureArtifactNoFollowOpener
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public bool IsSupported => true;

            public CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath)
            {
                Interlocked.Increment(ref _callCount);

                return CaptureArtifactNoFollowOpenResult.Of(
                    CaptureArtifactNoFollowOpenStatus.Absent);
            }
        }

        /// <summary>
        /// A commit filesystem that records any call. Composition must make
        /// none.
        /// </summary>
        private sealed class RecordingCommitFileSystem : ICaptureIndexCommitFileSystem
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public bool IsSupported
            {
                get
                {
                    Interlocked.Increment(ref _callCount);
                    return true;
                }
            }

            public bool IsDirectoryFlushSupported
            {
                get
                {
                    Interlocked.Increment(ref _callCount);
                    return true;
                }
            }

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no commit.");
            }

            public CaptureIndexFileOpen TryOpen(
                CaptureIndexCommitDirectory directory, string name)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no commit.");
            }

            public CaptureIndexCommitFile CreateNew(
                CaptureIndexCommitDirectory directory, string name)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no commit.");
            }

            public void FlushFileData(CaptureIndexCommitFile file)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no commit.");
            }

            public void Rename(
                CaptureIndexCommitFile file,
                CaptureIndexCommitDirectory directory,
                string newName)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no commit.");
            }

            public void Delete(CaptureIndexCommitFile file)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no commit.");
            }

            public void FlushDirectory(CaptureIndexCommitDirectory directory)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no commit.");
            }
        }

        /// <summary>
        /// A cleanup filesystem that records any call. Composition must make
        /// none.
        /// </summary>
        private sealed class RecordingCleanupFileSystem : ICaptureCompleteCleanupFileSystem
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public bool IsSupported
            {
                get
                {
                    Interlocked.Increment(ref _callCount);
                    return true;
                }
            }

            public bool IsDirectoryFlushSupported
            {
                get
                {
                    Interlocked.Increment(ref _callCount);
                    return true;
                }
            }

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no cleanup.");
            }

            public CaptureIndexDirectoryOpen TryOpenDirectory(string absolutePath)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no cleanup.");
            }

            public CaptureIndexFileOpen TryOpen(
                CaptureIndexCommitDirectory directory, string name)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no cleanup.");
            }

            public void Delete(CaptureIndexCommitFile file)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no cleanup.");
            }

            public void FlushDirectory(CaptureIndexCommitDirectory directory)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no cleanup.");
            }

            public void DeleteDirectory(CaptureIndexCommitDirectory directory)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no cleanup.");
            }

            public bool IsDirectoryEmpty(CaptureIndexCommitDirectory directory)
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("This fixture performs no cleanup.");
            }
        }

        /// <summary>A lock handle that counts its own releases.</summary>
        private sealed class CountingHandle : ICaptureRunLockHandle
        {
            private int _disposeCalls;

            internal CountingHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            internal int DisposeCallCount => Volatile.Read(ref _disposeCalls);

            public void Dispose()
            {
                Interlocked.Increment(ref _disposeCalls);
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
