using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC publication recovery orphan
    /// cleanup orchestration boundary: which classifications reach a cleanup at
    /// all, that exactly one attempt runs, and that the exact attempt result
    /// comes back unchanged.
    /// </summary>
    /// <remarks>
    /// The cleaner is a small fake that deletes nothing and only records the
    /// operation it was handed. What a cleanup targets, in which order, and
    /// which result shapes the execution coordinator refuses belong to those
    /// units' own fixtures and are not restated here.
    /// </remarks>
    public class NvencRunPublicationRecoveryIncompleteCleanupOrchestrationContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private int _runIds;

        [TearDown]
        public void TearDown()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }

            _owners.Clear();
        }

        // ---- Admission ----

        [Test]
        public void Constructor_NullCleanupExecution_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
                    null));
            Assert.That(ex.ParamName, Is.EqualTo("cleanupExecution"));
        }

        [Test]
        public void Execute_NullDecision_RejectedWithoutTouchingTheCleaner()
        {
            Harness h = MakeHarness();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => h.Coordinator.Execute(null, h.Owner));

            Assert.That(ex.ParamName, Is.EqualTo("decision"));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_NullOwnershipLease_RejectedWithoutTouchingTheCleaner()
        {
            Harness h = MakeHarness();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => h.Coordinator.Execute(h.Incomplete(), null));

            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_ADispositionThatIsNotIncomplete_RejectedWithoutTouchingTheCleaner()
        {
            // The operation factory is the sole authority on this; the three
            // other dispositions are checked representatively.
            Harness h = MakeHarness();

            foreach (NvencRunPublicationRecoveryDecision decision in
                new[] { h.Collision(), h.Deferred(), h.Recoverable() })
            {
                Assert.That(decision.Disposition,
                    Is.Not.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));

                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => h.Coordinator.Execute(decision, h.Owner));

                Assert.That(ex.ParamName, Is.EqualTo("decision"),
                    decision.Disposition.ToString());
                Assert.That(h.Cleaner.CallCount, Is.EqualTo(0),
                    decision.Disposition.ToString());
            }
        }

        [Test]
        public void Execute_AfterTheLockWasReleased_RejectedWithoutTouchingTheCleaner()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision incomplete = h.Incomplete();

            ReleaseAllLocks();
            Assert.That(incomplete.IsValid, Is.False);
            Assert.That(h.Owner.CanRelease, Is.False);

            Assert.Throws<ArgumentException>(() => h.Coordinator.Execute(incomplete, h.Owner));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_AnotherRunsOwnershipLease_RejectedWithoutTouchingTheCleaner()
        {
            Harness h = MakeHarness();
            Harness other = MakeHarness();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => h.Coordinator.Execute(h.Incomplete(), other.Owner));

            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(0));
            Assert.That(other.Cleaner.CallCount, Is.EqualTo(0));
        }

        // ---- The two terminal results ----

        [Test]
        public void Execute_CleanedAttempt_RunsOnceAndReturnsThatExactResult()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision incomplete = h.Incomplete();

            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result =
                h.Coordinator.Execute(incomplete, h.Owner);

            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));

            // The operation the cleaner was handed carries exactly what was
            // passed in.
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation =
                h.Cleaner.LastOperation;
            Assert.That(operation, Is.Not.Null);
            Assert.That(ReferenceEquals(operation.Decision, incomplete), Is.True);
            Assert.That(ReferenceEquals(operation.OwnershipLease, h.Owner), Is.True);

            Assert.That(result.IsCleaned, Is.True);
            Assert.That(result.IsIssuedFor(h.Cleaner, operation), Is.True);
            Assert.That(ReferenceEquals(result.CleanupOperation, operation), Is.True);
            Assert.That(ReferenceEquals(result.Receipt, h.Cleaner.LastResult.Receipt), Is.True);
            Assert.That(result.Receipt.IsIssuedFor(h.Cleaner, operation), Is.True);

            // The exact result reference the execution coordinator returned.
            Assert.That(result.Status, Is.EqualTo(h.Cleaner.LastResult.Status));
            Assert.That(ReferenceEquals(result.Cleaner, h.Cleaner), Is.True);
        }

        [Test]
        public void Execute_FailedAttempt_ReturnsThatExactResultWithoutRetrying()
        {
            Harness h = MakeHarness();
            h.Cleaner.Fail = true;

            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result =
                h.Coordinator.Execute(h.Incomplete(), h.Owner);

            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(result.IsFailed, Is.True);
            Assert.That(result.Receipt, Is.Null);
            Assert.That(result.IsIssuedFor(h.Cleaner, h.Cleaner.LastOperation), Is.True);
        }

        [Test]
        public void Execute_CleanerException_PropagatesByTheSameReferenceWithoutRepeating()
        {
            IOException thrown = new IOException("cleanup faulted.");
            Harness h = MakeHarness();
            h.Cleaner.Throw = thrown;

            IOException caught = Assert.Throws<IOException>(
                () => h.Coordinator.Execute(h.Incomplete(), h.Owner));

            Assert.That(ReferenceEquals(caught, thrown), Is.True);
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
        }

        // ---- What this boundary must leave alone ----

        [Test]
        public void Execute_LeavesTheDecisionGraphAndTheLeaseUnchanged()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision incomplete = h.Incomplete();
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = incomplete.Snapshot;

            Assert.That(h.Coordinator.Execute(incomplete, h.Owner).IsCleaned, Is.True);

            Assert.That(incomplete.IsValid, Is.True);
            Assert.That(incomplete.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));
            Assert.That(ReferenceEquals(incomplete.Snapshot, snapshot), Is.True);
            Assert.That(ReferenceEquals(snapshot.Operation.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(
                ReferenceEquals(snapshot.Operation.LockIdentityEvidence, h.LockIdentityEvidence),
                Is.True);
            Assert.That(ReferenceEquals(incomplete.RootLayout, h.Layout), Is.True);
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);

            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Owner.CanRelease, Is.True);
            Assert.That(h.Owner.IsReleaseComplete, Is.False);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Coordinator_HoldsOnlyItsCleanupExecution()
        {
            Type type =
                typeof(NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "every held reference must be readonly.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator),
            }));
        }

        // ---- Fixture helpers ----

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private Harness MakeHarness()
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            FakeCleaner cleaner = new FakeCleaner();

            return new Harness(
                layout,
                openOutcome,
                new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout),
                owner,
                firstHandle,
                secondHandle,
                cleaner,
                new NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
                    new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner)));
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

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
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
                            hasNonMarkerEntry: false)),
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

        /// <summary>
        /// A distinct layout per call, so another Run's lease is genuinely
        /// another Run rather than the same paths again.
        /// </summary>
        private CaptureRunRootLayout MakeLayout()
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

        /// <summary>
        /// One Run holding its lease, with the orchestration boundary wired to
        /// a cleaner that deletes nothing.
        /// </summary>
        private sealed class Harness
        {
            private readonly NvencRunPublicationRecoveryInspectionOperation _inspection;

            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                NvencRunPublicationRecoveryInspectionOperation inspection,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle,
                FakeCleaner cleaner,
                NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator coordinator)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                _inspection = inspection;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
                Cleaner = cleaner;
                Coordinator = coordinator;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
                _inspection.LockIdentityEvidence;

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal FakeCleaner Cleaner { get; }

            internal NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator
                Coordinator { get; }

            /// <summary>
            /// No finished plan, with the previous process's NVENC precommit
            /// temporary still in place.
            /// </summary>
            internal NvencRunPublicationRecoveryDecision Incomplete()
            {
                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        true,
                        default));
            }

            /// <summary>A finished plan next to the NVENC precommit temporary.</summary>
            internal NvencRunPublicationRecoveryDecision Collision()
            {
                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        MakePlan(_inspection),
                        true,
                        default));
            }

            /// <summary>A canonical plan whose chunk could not be verified.</summary>
            internal NvencRunPublicationRecoveryDecision Deferred()
            {
                CapturePublicationPlan plan = MakePlan(_inspection);

                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        false,
                        new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Deferred,
                            CaptureArtifactVerificationStatus.None,
                            CaptureArtifactVerificationFailureReason.BufferUnavailable,
                            0)));
            }

            /// <summary>A canonical plan whose chunk verified.</summary>
            internal NvencRunPublicationRecoveryDecision Recoverable()
            {
                CapturePublicationPlan plan = MakePlan(_inspection);

                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        false,
                        new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Completed,
                            CaptureArtifactVerificationStatus.MatchesExpected,
                            CaptureArtifactVerificationFailureReason.None,
                            ChunkByteLength)));
            }
        }

        /// <summary>
        /// A cleaner that deletes nothing: it records the operation it was
        /// handed and mints an ordinary result through the existing factories.
        /// </summary>
        private sealed class FakeCleaner : INvencRunPublicationRecoveryIncompleteCleaner
        {
            internal int CallCount { get; private set; }

            internal NvencRunPublicationRecoveryIncompleteCleanupOperation LastOperation
            {
                get;
                private set;
            }

            internal NvencRunPublicationRecoveryIncompleteCleanupAttemptResult LastResult
            {
                get;
                private set;
            }

            internal bool Fail { get; set; }

            internal Exception Throw { get; set; }

            public NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (Throw != null)
                {
                    throw Throw;
                }

                LastResult = Fail
                    ? NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                        this, operation)
                    : NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                        this, operation);

                return LastResult;
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
