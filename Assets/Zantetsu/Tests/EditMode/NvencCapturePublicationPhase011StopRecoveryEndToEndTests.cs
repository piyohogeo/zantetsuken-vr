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
    /// Managed end-to-end tests of the two Phase 0.11 NVENC publication
    /// recovery stopping paths over a real filesystem: a collision and a
    /// deferred chunk verification, each of which leaves every observed file
    /// exactly as it was and hands the Run's Session Ownership Lease back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inspection, its classification, and the release are the production
    /// concretes over the production no-follow backend, the existing
    /// verification buffer pool, and a real temporary tree. The lock handles
    /// behind the Session Ownership Lease are the same small counting seam the
    /// contract fixtures use, so what the release step shows is the production
    /// releaser driving that lease and its handles to a completed release - not
    /// the release of a real OS lock, which is not what these paths are about.
    /// </para>
    /// <para>
    /// Each case is one representative file set. Why a tree looks the way it
    /// does - another writer, an abort, a crash, or how far some earlier
    /// process got - is neither observed nor asserted here: both
    /// classifications rest on the file set alone, and the point of these two
    /// tests is that the file set survives them untouched.
    /// </para>
    /// <para>
    /// No Capture Index recovery, CaptureComplete, cleaner, Service, Worker,
    /// process state, or application composition is involved, no private field
    /// is rewritten, and no timestamp is used as evidence.
    /// </para>
    /// </remarks>
    public class NvencCapturePublicationPhase011StopRecoveryEndToEndTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string WriterHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const string PublicationPlanName = "publication.plan";

        private const string PrecommitTemporaryName =
            "publication.plan.nvenc-precommit.tmp";

        private const string CaptureIndexName = "capture.index";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

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
                    // Only this fixture's own verified temporary roots.
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
        public void Collision_LeavesTheObservedTreeUnchangedAndReleasesTheLease()
        {
            RequireNoFollowCapabilities();

            Sandbox sandbox = MakeSandbox(precommitTemporaryPresent: true);
            TreeSnapshot before = sandbox.CaptureTree();

            CaptureArtifactVerificationBufferPool pool =
                new CaptureArtifactVerificationBufferPool(VerificationBufferLength);

            NvencRunPublicationRecoveryDecision decision = Inspect(sandbox, pool);

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Canonical));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);

            // A finished plan standing next to the precommit temporary settles
            // it; the chunk is never verified at all.
            Assert.That(snapshot.HasChunkVerificationResult, Is.False);
            Assert.That(decision.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.AuthoritativePlan, Is.Null);

            before.AssertUnchanged(sandbox, "after the inspection and classification");
            Assert.That(pool.OutstandingRentCount, Is.Zero);

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                AssertStopsAndReleases(
                    sandbox,
                    decision,
                    before,
                    NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);

            // Everything the collision found is still exactly where it was.
            Assert.That(File.Exists(sandbox.PlanPath), Is.True);
            Assert.That(File.Exists(sandbox.PrecommitTemporaryPath), Is.True);
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.True);
            Assert.That(receipt.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
        }

        [Test]
        public void DeferredVerification_LeavesTheObservedTreeUnchangedAndReleasesTheLease()
        {
            RequireNoFollowCapabilities();

            Sandbox sandbox = MakeSandbox(precommitTemporaryPresent: false);
            TreeSnapshot before = sandbox.CaptureTree();

            CaptureArtifactVerificationBufferPool pool =
                new CaptureArtifactVerificationBufferPool(VerificationBufferLength);

            // The single buffer is held for the whole test, so the production
            // inspector takes its own BufferUnavailable path.
            CaptureArtifactVerificationBufferPool.Lease held = pool.TryRent();
            try
            {
                Assert.That(held, Is.Not.Null, "the pool must hand out its only buffer.");
                Assert.That(pool.OutstandingRentCount, Is.EqualTo(1));

                NvencRunPublicationRecoveryDecision decision = Inspect(sandbox, pool);

                NvencRunPublicationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
                Assert.That(snapshot.PublicationPlanStatus,
                    Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Canonical));
                Assert.That(snapshot.PrecommitTemporaryPresent, Is.False);

                CaptureArtifactVerificationResult verification = snapshot.ChunkVerification;
                Assert.That(snapshot.HasChunkVerificationResult, Is.True);
                Assert.That(verification.IsValid, Is.True);
                Assert.That(verification.ExecutionDisposition,
                    Is.EqualTo(CaptureArtifactVerificationExecutionDisposition.Deferred));
                Assert.That(verification.FailureReason,
                    Is.EqualTo(CaptureArtifactVerificationFailureReason.BufferUnavailable));
                Assert.That(verification.Status,
                    Is.EqualTo(CaptureArtifactVerificationStatus.None));
                Assert.That(verification.ObservedByteLength, Is.Zero);
                Assert.That(
                    ReferenceEquals(
                        verification.Descriptor, snapshot.PublicationPlan.GetArtifact(0)),
                    Is.True);

                Assert.That(decision.Disposition,
                    Is.EqualTo(NvencRunPublicationRecoveryDisposition.Deferred));
                Assert.That(decision.IsValid, Is.True);
                Assert.That(decision.AuthoritativePlan, Is.Null);

                before.AssertUnchanged(sandbox, "after the inspection and classification");
                Assert.That(pool.OutstandingRentCount, Is.EqualTo(1),
                    "the inspector returned nothing it did not rent.");

                NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                    AssertStopsAndReleases(
                        sandbox,
                        decision,
                        before,
                        NvencRunPublicationRecoveryDisposition.Deferred);

                Assert.That(receipt.Disposition,
                    Is.EqualTo(NvencRunPublicationRecoveryDisposition.Deferred));

                // The release is no owner of the buffer either: the lease this
                // test holds is still the only outstanding rent.
                Assert.That(pool.OutstandingRentCount, Is.EqualTo(1));
                Assert.That(File.Exists(sandbox.FinalChunkPath), Is.True);
            }
            finally
            {
                pool.Return(held);
            }

            Assert.That(pool.OutstandingRentCount, Is.Zero);
        }

        // ---- Shared wiring ----

        /// <summary>
        /// The production publication recovery inspection and classification,
        /// over the production no-follow opener and the given buffer pool.
        /// </summary>
        private static NvencRunPublicationRecoveryDecision Inspect(
            Sandbox sandbox, CaptureArtifactVerificationBufferPool pool)
        {
            return new NvencRunPublicationRecoveryOrchestrationCoordinator(
                    new NvencRunPublicationRecoveryInspectionExecutionCoordinator(
                        new NvencRunPublicationRecoveryInspector(
                            CaptureArtifactNoFollowOpen.Create(), pool)))
                .Execute(sandbox.OpenOutcome);
        }

        /// <summary>
        /// The production stopping release, its retention coordinator, and what
        /// a stop must leave behind: the exact tree it was handed.
        /// </summary>
        private static NvencRunPublicationRecoveryStopOwnershipReleaseReceipt
            AssertStopsAndReleases(
                Sandbox sandbox,
                NvencRunPublicationRecoveryDecision decision,
                TreeSnapshot before,
                NvencRunPublicationRecoveryDisposition expected)
        {
            NvencRunPublicationRecoveryStopOwnershipReleaser releaser =
                new NvencRunPublicationRecoveryStopOwnershipReleaser();
            NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator coordinator =
                new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                    decision,
                    sandbox.Owner,
                    new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
                        releaser));

            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation =
                coordinator.Operation;
            Assert.That(operation.Disposition, Is.EqualTo(expected));
            Assert.That(ReferenceEquals(operation.Decision, decision), Is.True);
            Assert.That(ReferenceEquals(operation.OwnershipLease, sandbox.Owner), Is.True);

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                coordinator.Release();

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(coordinator.IsReleased, Is.True);

            // The lease and its handles, which is what this seam holds.
            Assert.That(sandbox.Owner.IsReleaseComplete, Is.True);
            Assert.That(sandbox.Owner.CanRelease, Is.False);
            Assert.That(sandbox.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(sandbox.SecondHandle.DisposeCallCount, Is.EqualTo(1));

            before.AssertUnchanged(sandbox, "after the stopping release");

            // A second release changes neither the handles nor the tree.
            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt again = coordinator.Release();

            Assert.That(ReferenceEquals(again, receipt), Is.True);
            Assert.That(sandbox.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(sandbox.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            before.AssertUnchanged(sandbox, "after a second release");

            return receipt;
        }

        // ---- Fixture helpers ----

        /// <summary>
        /// Refuses the platform only on an explicit capability answer, before
        /// any sandbox is created. An I/O failure is never read as a missing
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
        }

        /// <summary>
        /// A real staging and final tree for one Run with a finished plan and
        /// the published chunk that plan declares, optionally with the NVENC
        /// precommit temporary beside it. Nothing about how it came to look
        /// this way is claimed.
        /// </summary>
        private Sandbox MakeSandbox(bool precommitTemporaryPresent)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "zantetsuken-phase011-stop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(root, "staging"), Path.Combine(root, "final"), 1);

            byte[] chunkBytes = new byte[4096];
            for (int i = 0; i < chunkBytes.Length; i++)
            {
                chunkBytes[i] = (byte)((i * 17) + 3);
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

            if (precommitTemporaryPresent)
            {
                File.WriteAllBytes(
                    Path.Combine(layout.StagingRunRoot, PrecommitTemporaryName),
                    new byte[] { 0x7b, 0x22, 0x3f, 0x01 });
            }

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

            // No Capture Index of either kind is part of these two paths.
            Assert.That(
                File.Exists(Path.Combine(layout.FinalRunRoot, CaptureIndexName)), Is.False);
            Assert.That(
                File.Exists(Path.Combine(layout.FinalRunRoot, CaptureIndexTemporaryName)),
                Is.False);
            Assert.That(
                File.Exists(Path.Combine(layout.StagingRunRoot, PrecommitTemporaryName)),
                Is.EqualTo(precommitTemporaryPresent));

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            Assert.That(openOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(openOutcome.Session, Is.Null);

            return new Sandbox(root, layout, openOutcome, owner, firstHandle, secondHandle);
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
        /// fixtures use. Both roots are described as they actually are on disk:
        /// canonical markers plus the non-marker entries the staging chunks
        /// directory and the published chunk are.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner,
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
            internal Sandbox(
                string root,
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Root = root;
                Layout = layout;
                OpenOutcome = openOutcome;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
            }

            internal string Root { get; }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal string PlanPath => Path.Combine(Layout.StagingRunRoot, PublicationPlanName);

            internal string PrecommitTemporaryPath =>
                Path.Combine(Layout.StagingRunRoot, PrecommitTemporaryName);

            internal string FinalChunkPath => Path.Combine(
                Layout.FinalRunRoot,
                NvencRunChunkArtifactDescriptorFactory.FinalRelativePath.Replace(
                    '/', Path.DirectorySeparatorChar));

            /// <summary>
            /// Every file and directory under this sandbox by content, not by
            /// timestamp.
            /// </summary>
            internal TreeSnapshot CaptureTree()
            {
                return TreeSnapshot.Of(Root);
            }
        }

        /// <summary>
        /// A whole temporary tree recorded by relative path, byte length,
        /// exact bytes, and SHA-256, so "unchanged" means unchanged in
        /// content and shape rather than in modification time.
        /// </summary>
        private sealed class TreeSnapshot
        {
            private readonly string _root;
            private readonly List<string> _directories;
            private readonly Dictionary<string, Entry> _files;

            private TreeSnapshot(
                string root, List<string> directories, Dictionary<string, Entry> files)
            {
                _root = root;
                _directories = directories;
                _files = files;
            }

            internal static TreeSnapshot Of(string root)
            {
                List<string> directories = new List<string>();
                foreach (string directory in Directory.GetDirectories(
                    root, "*", SearchOption.AllDirectories))
                {
                    directories.Add(Relative(root, directory));
                }

                directories.Sort(StringComparer.Ordinal);

                Dictionary<string, Entry> files = new Dictionary<string, Entry>(
                    StringComparer.Ordinal);
                foreach (string file in Directory.GetFiles(
                    root, "*", SearchOption.AllDirectories))
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    files[Relative(root, file)] = new Entry(bytes, Sha256Hex(bytes));
                }

                return new TreeSnapshot(root, directories, files);
            }

            internal void AssertUnchanged(Sandbox sandbox, string message)
            {
                TreeSnapshot now = Of(_root);

                Assert.That(now._directories, Is.EqualTo(_directories), message);
                Assert.That(now._files.Count, Is.EqualTo(_files.Count), message);

                foreach (KeyValuePair<string, Entry> expected in _files)
                {
                    Assert.That(now._files.ContainsKey(expected.Key), Is.True,
                        expected.Key + " " + message);

                    Entry observed = now._files[expected.Key];
                    Assert.That(observed.Bytes.Length, Is.EqualTo(expected.Value.Bytes.Length),
                        expected.Key + " " + message);
                    Assert.That(observed.Bytes, Is.EqualTo(expected.Value.Bytes),
                        expected.Key + " " + message);
                    Assert.That(observed.Sha256, Is.EqualTo(expected.Value.Sha256),
                        expected.Key + " " + message);
                    Assert.That(
                        new FileInfo(Path.Combine(_root, expected.Key)).Length,
                        Is.EqualTo(expected.Value.Bytes.LongLength),
                        expected.Key + " " + message);
                }

                // Both Run roots are still there, with nothing added or
                // removed anywhere under them.
                Assert.That(Directory.Exists(sandbox.Layout.StagingRunRoot), Is.True, message);
                Assert.That(Directory.Exists(sandbox.Layout.FinalRunRoot), Is.True, message);
            }

            private static string Relative(string root, string path)
            {
                return Path.GetFullPath(path)
                    .Substring(Path.GetFullPath(root).Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            private readonly struct Entry
            {
                internal Entry(byte[] bytes, string sha256)
                {
                    Bytes = bytes;
                    Sha256 = sha256;
                }

                internal byte[] Bytes { get; }

                internal string Sha256 { get; }
            }
        }

        /// <summary>
        /// A lock handle that counts its own releases: the one test seam these
        /// end-to-end paths keep, since acquiring and releasing a real OS lock
        /// is not what they are about.
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
