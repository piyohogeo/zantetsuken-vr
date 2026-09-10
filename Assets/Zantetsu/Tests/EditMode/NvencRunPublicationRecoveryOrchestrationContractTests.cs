using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC publication recovery
    /// orchestration boundary: admission of the open outcome, exactly one
    /// inspection and one classification of that exact snapshot, and the
    /// correlation the returned classification carries.
    /// </summary>
    /// <remarks>
    /// The inspector is a small recording fake returning in-memory snapshots,
    /// so the four dispositions are reached without a filesystem. The
    /// classifier's own cascade is already fixed by its dedicated fixture and
    /// is not repeated here: one representative path per disposition plus the
    /// orchestration correlation is the whole subject. No sleep, probabilistic
    /// repetition, or reflection over production private field names is used.
    /// </remarks>
    public class NvencRunPublicationRecoveryOrchestrationContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
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

        // ---- Admission ----

        [Test]
        public void Constructor_NullInspectionExecution_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryOrchestrationCoordinator(null));

            Assert.That(ex.ParamName, Is.EqualTo("inspectionExecution"));
        }

        [Test]
        public void Execute_NullOutcome_RejectedWithoutInspectorContact()
        {
            FakeInspector inspector = new FakeInspector();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => MakeCoordinator(inspector).Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("openOutcome"));
            Assert.That(inspector.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_OutcomeThatDoesNotRequirePublicationRecovery_RejectedWithoutInspectorContact()
        {
            FakeInspector inspector = new FakeInspector();
            CaptureRunInitializationOpenOutcome collision = MakeCollisionOutcome(MakeLayout());

            Assert.That(collision.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.RunRootCollision));

            Assert.Throws<ArgumentException>(
                () => MakeCoordinator(inspector).Execute(collision));
            Assert.That(inspector.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_OutcomeWhoseLockWasReleased_RejectedWithoutInspectorContact()
        {
            FakeInspector inspector = new FakeInspector();
            CaptureRunInitializationOpenOutcome outcome = MakeRecoveryOutcome(
                MakeLayout(), out CaptureRunInitializationSessionOwnershipLease owner);

            owner.Dispose();

            Assert.Throws<ArgumentException>(() => MakeCoordinator(inspector).Execute(outcome));
            Assert.That(inspector.CallCount, Is.EqualTo(0));
        }

        // ---- The four dispositions ----

        [Test]
        public void Execute_PublicationRecoveryRequired_InspectsOnceAndForwardsTheExactGraph()
        {
            FakeInspector inspector = new FakeInspector(Recoverable);
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationOpenOutcome outcome = MakeRecoveryOutcome(layout);
            NvencRunPublicationRecoveryOrchestrationCoordinator coordinator = MakeCoordinator(inspector);

            NvencRunPublicationRecoveryDecision decision = coordinator.Execute(outcome);

            Assert.That(inspector.CallCount, Is.EqualTo(1));
            Assert.That(decision.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));
            Assert.That(decision.IsValid, Is.True);

            // The classification carries the very graph that was inspected:
            // nothing is copied, rebuilt, or wrapped.
            Assert.That(ReferenceEquals(decision.Snapshot, inspector.LastSnapshot), Is.True);
            Assert.That(ReferenceEquals(decision.Operation, inspector.LastOperation), Is.True);
            Assert.That(ReferenceEquals(decision.Operation, decision.Snapshot.Operation), Is.True);
            Assert.That(ReferenceEquals(decision.Operation.OpenOutcome, outcome), Is.True);
            Assert.That(ReferenceEquals(decision.RootLayout, layout), Is.True);
            Assert.That(ReferenceEquals(
                    decision.AuthoritativePlan, decision.Snapshot.PublicationPlan),
                Is.True);
            Assert.That(decision.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(ReferenceEquals(decision.RunInitializationId, outcome.RunInitializationId),
                Is.True);
        }

        [Test]
        public void Execute_Incomplete_HasNoAuthoritativePlan()
        {
            FakeInspector inspector = new FakeInspector(Incomplete);

            NvencRunPublicationRecoveryDecision decision =
                MakeCoordinator(inspector).Execute(MakeRecoveryOutcome(MakeLayout()));

            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));
            Assert.That(decision.AuthoritativePlan, Is.Null);
            Assert.That(decision.Snapshot.PublicationPlan, Is.Null);
            Assert.That(decision.IsValid, Is.True);
        }

        [Test]
        public void Execute_Collision_HasNoAuthoritativePlan_AndChangesNoFile()
        {
            FakeInspector inspector = new FakeInspector(Collision);
            CaptureRunRootLayout layout = MakeSandboxLayout(out string sandbox);
            Dictionary<string, string> before = SnapshotFileSet(sandbox);

            NvencRunPublicationRecoveryDecision decision =
                MakeCoordinator(inspector).Execute(MakeRecoveryOutcome(layout));

            Assert.That(decision.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
            Assert.That(decision.AuthoritativePlan, Is.Null);
            Assert.That(SnapshotFileSet(sandbox), Is.EqualTo(before));
        }

        [Test]
        public void Execute_Deferred_IsNotGuessedIntoAnotherDisposition()
        {
            FakeInspector inspector = new FakeInspector(Deferred);

            NvencRunPublicationRecoveryDecision decision =
                MakeCoordinator(inspector).Execute(MakeRecoveryOutcome(MakeLayout()));

            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Deferred));
            Assert.That(decision.AuthoritativePlan, Is.Null);
            Assert.That(decision.IsValid, Is.True);
        }

        // ---- Failure paths ----

        [Test]
        public void Execute_InspectorException_PropagatesSameReferenceAndIsNotRetried()
        {
            IOException failure = new IOException("inspection failed");
            FakeInspector inspector = new FakeInspector { ExceptionToThrow = failure };

            IOException thrown = Assert.Throws<IOException>(
                () => MakeCoordinator(inspector).Execute(MakeRecoveryOutcome(MakeLayout())));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(inspector.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_NullForeignOrInvalidSnapshot_ProducesNoResult()
        {
            // Null.
            FakeInspector nulls = new FakeInspector((operation, fixture) => null);
            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(nulls).Execute(MakeRecoveryOutcome(MakeLayout())));
            Assert.That(nulls.CallCount, Is.EqualTo(1));

            // A snapshot describing another Run's operation.
            FakeInspector foreign = new FakeInspector(
                (operation, fixture) => Incomplete(fixture.MakeOperation(), fixture));
            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(foreign).Execute(MakeRecoveryOutcome(MakeLayout())));
            Assert.That(foreign.CallCount, Is.EqualTo(1));

            // A snapshot whose operation lost its lock while it was built.
            FakeInspector invalid = new FakeInspector(
                (operation, fixture) =>
                {
                    NvencRunPublicationRecoveryInspectionSnapshot snapshot =
                        Incomplete(operation, fixture);
                    fixture.ReleaseAllLocks();
                    return snapshot;
                });
            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(invalid).Execute(MakeRecoveryOutcome(MakeLayout())));
            Assert.That(invalid.CallCount, Is.EqualTo(1));
        }

        // ---- The classification ----

        [Test]
        public void Classification_AfterLockRelease_BecomesInvalid()
        {
            FakeInspector inspector = new FakeInspector(Recoverable);
            NvencRunPublicationRecoveryDecision decision =
                MakeCoordinator(inspector).Execute(MakeRecoveryOutcome(MakeLayout()));

            Assert.That(decision.IsValid, Is.True);

            ReleaseAllLocks();

            Assert.That(decision.IsValid, Is.False);
            Assert.That(decision.Operation.IsValid, Is.False);

            // It still carries the same graph; it simply no longer claims to be
            // valid, and it released nothing itself.
            Assert.That(ReferenceEquals(decision.Snapshot, inspector.LastSnapshot), Is.True);
        }

        [Test]
        public void Execute_LeavesTheLeaseAndTheOpenOutcomeUntouched()
        {
            FakeInspector inspector = new FakeInspector(Recoverable);
            CaptureRunInitializationOpenOutcome outcome = MakeRecoveryOutcome(
                MakeLayout(), out CaptureRunInitializationSessionOwnershipLease owner);

            NvencRunPublicationRecoveryDecision decision =
                MakeCoordinator(inspector).Execute(outcome);

            // Nothing here owns or releases the lock, converts the outcome into
            // a session, or advances it past classification.
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);
            Assert.That(outcome.IsValid, Is.True);
            Assert.That(outcome.Session, Is.Null);
            Assert.That(outcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(decision.Operation.IsValid, Is.True);
        }

        // ---- Fixture helpers ----

        private NvencRunPublicationRecoveryOrchestrationCoordinator MakeCoordinator(
            FakeInspector inspector)
        {
            inspector.Fixture = this;
            return new NvencRunPublicationRecoveryOrchestrationCoordinator(
                new NvencRunPublicationRecoveryInspectionExecutionCoordinator(inspector));
        }

        private static NvencRunPublicationRecoveryInspectionSnapshot Incomplete(
            NvencRunPublicationRecoveryInspectionOperation operation,
            NvencRunPublicationRecoveryOrchestrationContractTests fixture)
        {
            return new NvencRunPublicationRecoveryInspectionSnapshot(
                operation,
                CaptureRunPublicationDocumentObservationStatus.Absent,
                null,
                false,
                default);
        }

        private static NvencRunPublicationRecoveryInspectionSnapshot Recoverable(
            NvencRunPublicationRecoveryInspectionOperation operation,
            NvencRunPublicationRecoveryOrchestrationContractTests fixture)
        {
            CapturePublicationPlan plan = MakePlan(operation);
            return new NvencRunPublicationRecoveryInspectionSnapshot(
                operation,
                CaptureRunPublicationDocumentObservationStatus.Canonical,
                plan,
                false,
                new CaptureArtifactVerificationResult(
                    plan.GetArtifact(0),
                    CaptureArtifactVerificationExecutionDisposition.Completed,
                    CaptureArtifactVerificationStatus.MatchesExpected,
                    CaptureArtifactVerificationFailureReason.None,
                    ChunkByteLength));
        }

        /// <summary>
        /// A finished plan next to the NVENC precommit temporary: the
        /// representative collision, decided without a chunk verification.
        /// </summary>
        private static NvencRunPublicationRecoveryInspectionSnapshot Collision(
            NvencRunPublicationRecoveryInspectionOperation operation,
            NvencRunPublicationRecoveryOrchestrationContractTests fixture)
        {
            return new NvencRunPublicationRecoveryInspectionSnapshot(
                operation,
                CaptureRunPublicationDocumentObservationStatus.Canonical,
                MakePlan(operation),
                true,
                default);
        }

        private static NvencRunPublicationRecoveryInspectionSnapshot Deferred(
            NvencRunPublicationRecoveryInspectionOperation operation,
            NvencRunPublicationRecoveryOrchestrationContractTests fixture)
        {
            CapturePublicationPlan plan = MakePlan(operation);
            return new NvencRunPublicationRecoveryInspectionSnapshot(
                operation,
                CaptureRunPublicationDocumentObservationStatus.Canonical,
                plan,
                false,
                new CaptureArtifactVerificationResult(
                    plan.GetArtifact(0),
                    CaptureArtifactVerificationExecutionDisposition.Deferred,
                    CaptureArtifactVerificationStatus.None,
                    CaptureArtifactVerificationFailureReason.BufferUnavailable,
                    0));
        }

        private static CapturePublicationPlan MakePlan(
            NvencRunPublicationRecoveryInspectionOperation operation)
        {
            CaptureArtifactDescriptor[] artifacts = new[]
            {
                NvencRunChunkArtifactDescriptorFactory.Create(ArtifactId, ChunkByteLength, Hash64),
            };

            CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[3];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new CaptureFrameEvidenceEntry(i + 1, new[] { ArtifactId });
            }

            return new CapturePublicationPlan(
                operation.TestRunId, operation.RunInitializationId, Hash64, artifacts, entries);
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private NvencRunPublicationRecoveryInspectionOperation MakeOperation()
        {
            CaptureRunRootLayout layout = MakeLayout();
            return new NvencRunPublicationRecoveryInspectionOperation(
                MakeRecoveryOutcome(layout), layout);
        }

        /// <summary>
        /// A real sandbox with a small file set under both Run roots, so a
        /// classification can be shown to change nothing on disk.
        /// </summary>
        private CaptureRunRootLayout MakeSandboxLayout(out string sandbox)
        {
            sandbox = Path.Combine(
                Path.GetTempPath(), "zantetsuken-recovery-orch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(sandbox, "staging"), Path.Combine(sandbox, "final"), 1);

            Directory.CreateDirectory(layout.StagingRunRoot);
            Directory.CreateDirectory(layout.FinalRunRoot);
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, "publication.plan"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, "publication.plan.nvenc-precommit.tmp"),
                new byte[] { 4 });
            File.WriteAllBytes(
                Path.Combine(layout.FinalRunRoot, "run.ready"), new byte[] { 5, 6 });

            return layout;
        }

        private static Dictionary<string, string> SnapshotFileSet(string root)
        {
            Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal);
            using (SHA256 sha = SHA256.Create())
            {
                foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    files[path.Substring(root.Length)] =
                        bytes.Length.ToString() + ":" + BitConverter.ToString(sha.ComputeHash(bytes));
                }
            }

            return files;
        }

        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(CaptureRunRootLayout layout)
        {
            return MakeRecoveryOutcome(layout, out _);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
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
                hasNonMarkerEntry: false);

            return MakeOutcome(
                layout,
                staging,
                final,
                CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired,
                out owner);
        }

        /// <summary>
        /// A Run root collision outcome: valid, lock-holding, and terminal, but
        /// not the publication recovery this boundary admits.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeCollisionOutcome(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationRootObservation staging =
                new CaptureRunInitializationRootObservation(
                    CaptureRunRootRole.Staging,
                    true,
                    false,
                    CaptureRunMarkerObservationStatus.Absent,
                    null,
                    false,
                    CaptureRunMarkerObservationStatus.Absent,
                    null,
                    false,
                    true,
                    false);
            CaptureRunInitializationRootObservation final =
                new CaptureRunInitializationRootObservation(
                    CaptureRunRootRole.Final,
                    false,
                    false,
                    CaptureRunMarkerObservationStatus.Absent,
                    null,
                    false,
                    CaptureRunMarkerObservationStatus.Absent,
                    null,
                    false,
                    false,
                    false);

            return MakeOutcome(
                layout,
                staging,
                final,
                CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision,
                out _);
        }

        private CaptureRunInitializationOpenOutcome MakeOutcome(
            CaptureRunRootLayout layout,
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunInitializationRecoveryExecutionStatus expectedStatus,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    new FakeRecoveryInspector(staging, final),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet,
                new FakeHandle(pathSet.FirstLockPath),
                new FakeHandle(pathSet.SecondLockPath));
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);

            CaptureRunLockIdentityEvidence identity =
                CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(
                new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4));

            Assert.That(result.Status, Is.EqualTo(expectedStatus),
                "the fixture must reach the intended terminal outcome.");

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

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        /// <summary>
        /// Records the operation it was given and the snapshot it produced, and
        /// produces exactly what the test asked for - including a null, foreign,
        /// or invalidated snapshot.
        /// </summary>
        private sealed class FakeInspector : INvencRunPublicationRecoveryInspector
        {
            private readonly Func<
                NvencRunPublicationRecoveryInspectionOperation,
                NvencRunPublicationRecoveryOrchestrationContractTests,
                NvencRunPublicationRecoveryInspectionSnapshot> _observe;

            internal FakeInspector()
                : this(null)
            {
            }

            internal FakeInspector(
                Func<
                    NvencRunPublicationRecoveryInspectionOperation,
                    NvencRunPublicationRecoveryOrchestrationContractTests,
                    NvencRunPublicationRecoveryInspectionSnapshot> observe)
            {
                _observe = observe;
            }

            internal NvencRunPublicationRecoveryOrchestrationContractTests Fixture { get; set; }

            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal NvencRunPublicationRecoveryInspectionOperation LastOperation { get; private set; }

            internal NvencRunPublicationRecoveryInspectionSnapshot LastSnapshot { get; private set; }

            public NvencRunPublicationRecoveryInspectionSnapshot Inspect(
                NvencRunPublicationRecoveryInspectionOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                LastSnapshot = _observe(operation, Fixture);
                return LastSnapshot;
            }
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            internal FakeHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            public void Dispose()
            {
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
