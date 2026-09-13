using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC publication recovery stop
    /// ownership release operation: which stopping classifications may release
    /// the Run's lease, what it forwards, and how its three predicates behave
    /// across a partial and a completed release.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release, never from rewriting private state. Nothing
    /// here runs a cleanup, touches the filesystem, or maps a disposition into
    /// a new status.
    /// </remarks>
    public class NvencRunPublicationRecoveryStopOwnershipReleaseOperationContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

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
        }

        // ---- Admission ----

        [Test]
        public void Create_NullArguments_Rejected()
        {
            Harness h = MakeHarness();

            ArgumentNullException leaseEx = Assert.Throws<ArgumentNullException>(
                () => NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    h.Collision(), null));
            Assert.That(leaseEx.ParamName, Is.EqualTo("ownershipLease"));

            ArgumentException decisionEx = Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    null, h.Owner));
            Assert.That(decisionEx.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Create_FromACollision_Issues()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision collision = h.Collision();

            AssertIssues(h, collision,
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void Create_FromADeferredVerification_Issues()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision deferred = h.Deferred();

            AssertIssues(h, deferred, NvencRunPublicationRecoveryDisposition.Deferred);
        }

        [Test]
        public void Create_FromAnIncompleteRun_Rejected()
        {
            // An incomplete Run is orphan cleanup's subject: its lease is not
            // released through this stopping operation before that separate
            // cleanup path has finished.
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision incomplete = h.Incomplete();

            Assert.That(incomplete.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    incomplete, h.Owner));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Create_FromARecoverableRun_Rejected()
        {
            // That Run continues into the Capture Index recovery, which owns
            // its own release.
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision recoverable = h.Recoverable();

            Assert.That(recoverable.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));

            Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    recoverable, h.Owner));
        }

        [Test]
        public void Create_DecisionWhoseLockWasReleased_Rejected()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision collision = h.Collision();

            ReleaseAllLocks();
            Assert.That(collision.IsValid, Is.False);

            Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    collision, h.Owner));
        }

        [Test]
        public void Create_ForeignOwnershipLease_Rejected()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision collision = h.Collision();

            // Another Run's lease.
            Harness other = MakeHarness();
            ArgumentException foreignRun = Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    collision, other.Owner));
            Assert.That(foreignRun.ParamName, Is.EqualTo("ownershipLease"));

            // A second lease over this Run's own layout: a different path set
            // instance, so it is not the lock this graph was issued for.
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(h.Layout);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet,
                new CountingHandle(pathSet.FirstLockPath),
                new CountingHandle(pathSet.SecondLockPath));
            CaptureRunInitializationSessionOwnershipLease second =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(second);

            ArgumentException otherPathSet = Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    collision, second));
            Assert.That(otherPathSet.ParamName, Is.EqualTo("ownershipLease"));
        }

        [Test]
        public void Create_ReleasesAndChangesNothing()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision collision = h.Collision();
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = collision.Snapshot;

            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation =
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    collision, h.Owner);

            Assert.That(operation, Is.Not.Null);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Owner.IsReleaseComplete, Is.False);

            Assert.That(collision.IsValid, Is.True);
            Assert.That(ReferenceEquals(collision.Snapshot, snapshot), Is.True);
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Canonical));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);

            // This boundary is filesystem-free.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
        }

        // ---- The three predicates across the lease's release states ----

        [Test]
        public void Operation_BeforeAnyRelease_IsBoundReleasableAndValid()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation =
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    h.Collision(), h.Owner);

            Assert.That(operation.IsBindingIntact, Is.True);
            Assert.That(operation.CanRelease, Is.True);
            Assert.That(operation.IsValid, Is.True);
        }

        [Test]
        public void Operation_AcrossReleaseStates_BehavesTheSameForBothStoppingDispositions()
        {
            Harness collision = MakeHarness(throwingFirstRelease: true);
            Harness deferred = MakeHarness(throwingFirstRelease: true);

            NvencRunPublicationRecoveryStopOwnershipReleaseOperation fromCollision =
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    collision.Collision(), collision.Owner);
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation fromDeferred =
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    deferred.Deferred(), deferred.Owner);

            AssertPredicates(fromCollision, true, true, true, "before any release");
            AssertPredicates(fromDeferred, true, true, true, "before any release");

            // The ordinary API's partial release: the second handle is
            // released, the first one throws, and the lease stays retryable.
            Assert.Throws<AggregateException>(() => collision.Owner.Dispose());
            Assert.Throws<AggregateException>(() => deferred.Owner.Dispose());

            Assert.That(collision.Owner.IsCreated, Is.False);
            Assert.That(collision.Owner.IsReleaseComplete, Is.False);
            AssertPredicates(fromCollision, true, true, false, "after a partial release");
            AssertPredicates(fromDeferred, true, true, false, "after a partial release");

            collision.Owner.Dispose();
            deferred.Owner.Dispose();

            Assert.That(collision.Owner.IsReleaseComplete, Is.True);
            AssertPredicates(fromCollision, true, false, false, "after a completed release");
            AssertPredicates(fromDeferred, true, false, false, "after a completed release");

            Assert.That(fromCollision.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
            Assert.That(fromDeferred.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Deferred));
        }

        // ---- Shape ----

        [Test]
        public void Operation_IsSealedNonDisposableAndHoldsTwoReadonlyReferences()
        {
            Type type = typeof(NvencRunPublicationRecoveryStopOwnershipReleaseOperation);

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
                typeof(NvencRunPublicationRecoveryDecision),
                typeof(CaptureRunInitializationSessionOwnershipLease),
            }));

            Assert.That(
                type.GetConstructors(BindingFlags.Instance | BindingFlags.Public), Is.Empty);
        }

        // ---- Fixture helpers ----

        private static void AssertIssues(
            Harness h,
            NvencRunPublicationRecoveryDecision decision,
            NvencRunPublicationRecoveryDisposition expected)
        {
            Assert.That(decision.Disposition, Is.EqualTo(expected));

            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation =
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(decision, h.Owner);

            Assert.That(operation.Disposition, Is.EqualTo(expected));
            Assert.That(operation.IsValid, Is.True);
            Assert.That(ReferenceEquals(operation.Decision, decision), Is.True);
            Assert.That(ReferenceEquals(operation.Snapshot, decision.Snapshot), Is.True);
            Assert.That(ReferenceEquals(operation.OwnershipLease, h.Owner), Is.True);
            Assert.That(ReferenceEquals(operation.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(
                    operation.LockIdentityEvidence, h.LockIdentityEvidence),
                Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, h.Layout), Is.True);
            Assert.That(operation.TestRunId, Is.EqualTo(h.Layout.TestRunId));
            Assert.That(ReferenceEquals(
                    operation.RunInitializationId, decision.RunInitializationId),
                Is.True);
        }

        private static void AssertPredicates(
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation,
            bool binding,
            bool canRelease,
            bool valid,
            string message)
        {
            Assert.That(operation.IsBindingIntact, Is.EqualTo(binding), message);
            Assert.That(operation.CanRelease, Is.EqualTo(canRelease), message);
            Assert.That(operation.IsValid, Is.EqualTo(valid), message);
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private Harness MakeHarness(bool throwingFirstRelease = false)
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                throwingFirstRelease,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            NvencRunPublicationRecoveryInspectionOperation inspection =
                new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout);

            return new Harness(
                layout, openOutcome, inspection, owner, firstHandle, secondHandle);
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
            bool throwingFirstRelease,
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
                hasNonMarkerEntry: false);

            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    new FakeRecoveryInspector(staging, final),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            firstHandle = new CountingHandle(pathSet.FirstLockPath, throwingFirstRelease);
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

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        /// <summary>
        /// One Run holding its lease, able to produce each publication recovery
        /// classification over the same graph.
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
                CountingHandle secondHandle)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                _inspection = inspection;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
                _inspection.LockIdentityEvidence;

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

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

            /// <summary>No finished plan at all.</summary>
            internal NvencRunPublicationRecoveryDecision Incomplete()
            {
                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        false,
                        default));
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
        /// A lock handle that counts its own releases and can fail the first
        /// one, producing the ordinary API's partial release.
        /// </summary>
        private sealed class CountingHandle : ICaptureRunLockHandle
        {
            private readonly bool _throwFirstRelease;
            private int _disposeCalls;

            internal CountingHandle(string lockPath, bool throwFirstRelease = false)
            {
                LockPath = lockPath;
                _throwFirstRelease = throwFirstRelease;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            internal int DisposeCallCount => _disposeCalls;

            public void Dispose()
            {
                _disposeCalls++;

                if (_throwFirstRelease && _disposeCalls == 1)
                {
                    throw new InvalidOperationException("First release fails.");
                }
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
