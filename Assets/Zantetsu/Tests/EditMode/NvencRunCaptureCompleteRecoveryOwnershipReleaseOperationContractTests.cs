using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC recovery ownership release
    /// operation: what may authorize a release, what it forwards, and how its
    /// three predicates separate the binding from the lease's liveness across a
    /// partial and a completed release.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release, never from rewriting private state. Nothing
    /// here disposes a lease on purpose except where a test says so.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryOwnershipReleaseOperationContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private const NvencRunCaptureIndexObservationStatus Absent =
            NvencRunCaptureIndexObservationStatus.Absent;

        private const NvencRunCaptureIndexObservationStatus Matches =
            NvencRunCaptureIndexObservationStatus.MatchesAuthoritative;

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
        public void Create_NullLease_Rejected()
        {
            Harness h = MakeHarness();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation.Create(
                    h.CleanupResult, null));

            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));
        }

        [Test]
        public void Create_DefaultOrInvalidCleanupResult_Rejected()
        {
            Harness h = MakeHarness();

            ArgumentException defaultEx = Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation.Create(
                    default, h.Owner));
            Assert.That(defaultEx.ParamName, Is.EqualTo("cleanupResult"));

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult = h.CleanupResult;
            h.Owner.Dispose();

            Assert.That(cleanupResult.IsValid, Is.False);
            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation.Create(
                    cleanupResult, h.Owner));
        }

        [Test]
        public void Create_ForeignOwnershipLease_Rejected()
        {
            Harness h = MakeHarness();

            // Another Run's lease.
            Harness other = MakeHarness();
            ArgumentException foreignRun = Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation.Create(
                    h.CleanupResult, other.Owner));
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
                () => NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation.Create(
                    h.CleanupResult, second));
            Assert.That(otherPathSet.ParamName, Is.EqualTo("ownershipLease"));
        }

        // ---- Both terminal cleanup shapes authorize a release ----

        [Test]
        public void Create_FromACleanedCleanup_IssuesAndForwardsTheExactGraph()
        {
            Harness h = MakeHarness();

            Assert.That(h.CleanupResult.IsCleaned, Is.True);
            AssertIssues(h, NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned);
        }

        [Test]
        public void Create_FromAFailedCleanup_IssuesJustTheSame()
        {
            // A cleanup failure must not strand the OS lock.
            Harness h = MakeHarness(cleanupFailed: true);

            Assert.That(h.CleanupResult.IsFailed, Is.True);
            Assert.That(h.CleanupResult.Receipt, Is.Null);
            AssertIssues(h, NvencRunCaptureCompleteRecoveryCleanupStatus.Failed);
        }

        [Test]
        public void Create_ReleasesNothing()
        {
            Harness h = MakeHarness();

            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation =
                NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation.Create(
                    h.CleanupResult, h.Owner);

            Assert.That(operation, Is.Not.Null);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Owner.IsReleaseComplete, Is.False);
        }

        // ---- The three predicates across the lease's release states ----

        [Test]
        public void Operation_BeforeAnyRelease_IsBoundReleasableAndValid()
        {
            Harness h = MakeHarness();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation = h.Issue();

            Assert.That(operation.IsBindingIntact, Is.True);
            Assert.That(operation.CanRelease, Is.True);
            Assert.That(operation.IsValid, Is.True);
        }

        [Test]
        public void Operation_AfterAPartialRelease_StaysBoundAndReleasableButNotValid()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation = h.Issue();

            // The ordinary API's partial release: the second handle is
            // released, the first one throws, and the lease stays retryable.
            Assert.Throws<AggregateException>(() => h.Owner.Dispose());

            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsCreated, Is.False);
            Assert.That(h.Owner.IsReleaseComplete, Is.False);

            Assert.That(operation.IsBindingIntact, Is.True);
            Assert.That(operation.CanRelease, Is.True);
            Assert.That(operation.IsValid, Is.False);
        }

        [Test]
        public void Operation_AfterACompletedRelease_StaysBoundButIsNeitherReleasableNorValid()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation = h.Issue();

            Assert.Throws<AggregateException>(() => h.Owner.Dispose());
            h.Owner.Dispose();

            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(h.Owner.CanRelease, Is.False);

            Assert.That(operation.IsBindingIntact, Is.True);
            Assert.That(operation.CanRelease, Is.False);
            Assert.That(operation.IsValid, Is.False);
        }

        [Test]
        public void Operation_CleanedAndFailedCleanups_BehaveIdenticallyAcrossReleaseStates()
        {
            Harness cleaned = MakeHarness(throwingFirstRelease: true);
            Harness failed = MakeHarness(throwingFirstRelease: true, cleanupFailed: true);

            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation fromCleaned = cleaned.Issue();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation fromFailed = failed.Issue();

            AssertSamePredicates(fromCleaned, fromFailed, "before any release");

            Assert.Throws<AggregateException>(() => cleaned.Owner.Dispose());
            Assert.Throws<AggregateException>(() => failed.Owner.Dispose());
            AssertSamePredicates(fromCleaned, fromFailed, "after a partial release");

            cleaned.Owner.Dispose();
            failed.Owner.Dispose();
            AssertSamePredicates(fromCleaned, fromFailed, "after a completed release");

            Assert.That(fromCleaned.CleanupStatus,
                Is.EqualTo(NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned));
            Assert.That(fromFailed.CleanupStatus,
                Is.EqualTo(NvencRunCaptureCompleteRecoveryCleanupStatus.Failed));
        }

        // ---- The evidence predicate this separation rests on ----

        [Test]
        public void LockIdentityEvidence_BindingSurvivesPartialAndCompletedRelease()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            CaptureRunLockIdentityEvidence evidence = h.Issue().LockIdentityEvidence;

            Assert.That(evidence.IsBoundTo(h.Owner), Is.True);
            Assert.That(evidence.IsIssuedFor(h.Owner), Is.True);
            Assert.That(evidence.IsValid, Is.True);

            Assert.Throws<AggregateException>(() => h.Owner.Dispose());

            // Only the binding survives a partial release.
            Assert.That(evidence.IsBoundTo(h.Owner), Is.True);
            Assert.That(evidence.IsIssuedFor(h.Owner), Is.False);
            Assert.That(evidence.IsValid, Is.False);

            h.Owner.Dispose();

            Assert.That(evidence.IsBoundTo(h.Owner), Is.True);
            Assert.That(evidence.IsIssuedFor(h.Owner), Is.False);
            Assert.That(evidence.IsValid, Is.False);

            // It is still a binding to one exact lease, not to any lease.
            Assert.That(evidence.IsBoundTo(MakeHarness().Owner), Is.False);
            Assert.That(evidence.IsBoundTo(null), Is.False);
        }

        // ---- Shape ----

        [Test]
        public void Operation_IsSealedNonDisposableAndHoldsTwoReadonlyReferences()
        {
            Type type = typeof(NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "every held value must be readonly.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(NvencRunCaptureCompleteRecoveryCleanupAttemptResult),
                typeof(CaptureRunInitializationSessionOwnershipLease),
            }));

            Assert.That(
                type.GetConstructors(BindingFlags.Instance | BindingFlags.Public), Is.Empty);
        }

        // ---- Fixture helpers ----

        private static void AssertIssues(
            Harness h, NvencRunCaptureCompleteRecoveryCleanupStatus expected)
        {
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation =
                NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation.Create(
                    h.CleanupResult, h.Owner);

            Assert.That(operation.CleanupStatus, Is.EqualTo(expected));
            Assert.That(operation.IsValid, Is.True);
            Assert.That(ReferenceEquals(operation.OwnershipLease, h.Owner), Is.True);
            Assert.That(ReferenceEquals(
                    operation.CleanupOperation, h.CleanupResult.Operation),
                Is.True);
            Assert.That(ReferenceEquals(operation.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(
                    operation.LockIdentityEvidence, h.LockIdentityEvidence),
                Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, h.Layout), Is.True);
            Assert.That(operation.TestRunId, Is.EqualTo(h.Layout.TestRunId));
            Assert.That(ReferenceEquals(
                    operation.RunInitializationId, h.OpenOutcome.RunInitializationId),
                Is.True);
            Assert.That(operation.HasCommitReceipt, Is.EqualTo(h.CleanupResult.HasCommitReceipt));
        }

        private static void AssertSamePredicates(
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation first,
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation second,
            string message)
        {
            Assert.That(second.IsBindingIntact, Is.EqualTo(first.IsBindingIntact), message);
            Assert.That(second.CanRelease, Is.EqualTo(first.CanRelease), message);
            Assert.That(second.IsValid, Is.EqualTo(first.IsValid), message);
        }

        private Harness MakeHarness(bool throwingFirstRelease = false, bool cleanupFailed = false)
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                throwingFirstRelease,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            NvencRunPublicationRecoveryInspectionOperation recoveryOperation =
                new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout);
            CapturePublicationPlan plan = MakePlan(recoveryOperation);

            NvencRunPublicationRecoveryDecision recovery =
                NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        recoveryOperation,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        false,
                        new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Completed,
                            CaptureArtifactVerificationStatus.MatchesExpected,
                            CaptureArtifactVerificationFailureReason.None,
                            ChunkByteLength)));

            NvencRunCaptureCompleteRecoveryReceipt captureComplete =
                new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                                new FakeCommitter())),
                        new NvencRunCaptureCompleteRecoveryExecutionCoordinator(
                            new NvencRunCaptureCompleteRecoveryCompleter()))
                    .Execute(NvencRunCaptureIndexRecoveryClassifier.Classify(
                        new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                            new NvencRunCaptureIndexRecoveryInspectionOperation(recovery),
                            Matches,
                            Absent)));

            FakeCleaner cleaner = new FakeCleaner
            {
                Status = cleanupFailed
                    ? NvencRunCaptureCompleteRecoveryCleanupStatus.Failed
                    : NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned,
            };

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult =
                new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                        new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner))
                    .Execute(captureComplete);

            return new Harness(
                layout, openOutcome, recoveryOperation.LockIdentityEvidence, cleanupResult, owner,
                firstHandle, secondHandle);
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

        /// <summary>One recovered Run's cleanup result and the lease it holds.</summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunLockIdentityEvidence lockIdentityEvidence,
                NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                LockIdentityEvidence = lockIdentityEvidence;
                CleanupResult = cleanupResult;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunLockIdentityEvidence LockIdentityEvidence { get; }

            internal NvencRunCaptureCompleteRecoveryCleanupAttemptResult CleanupResult { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation Issue()
            {
                return NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation.Create(
                    CleanupResult, Owner);
            }
        }

        /// <summary>
        /// A lock handle that counts its own releases and can fail the first
        /// one. As a lease's first handle, the failing variant produces the
        /// ordinary API's partial release: the second handle is released, the
        /// disposal throws, and the lease is left no longer fully retained but
        /// still releasable.
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

        /// <summary>Returns the ordinary cleanup result the test asked for.</summary>
        private sealed class FakeCleaner : INvencRunCaptureCompleteRecoveryCleaner
        {
            internal NvencRunCaptureCompleteRecoveryCleanupStatus Status { get; set; } =
                NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned;

            public NvencRunCaptureCompleteRecoveryCleanupAttemptResult Clean(
                NvencRunCaptureCompleteRecoveryCleanupOperation operation)
            {
                return Status == NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned
                    ? NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(this, operation)
                    : NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Failed(this, operation);
            }
        }

        /// <summary>Mints the ordinary commit receipt and touches no file.</summary>
        private sealed class FakeCommitter : INvencRunCaptureIndexRecoveryCommitter
        {
            public NvencRunCaptureIndexRecoveryCommitReceipt Commit(
                NvencRunCaptureIndexRecoveryCommitOperation operation)
            {
                return NvencRunCaptureIndexRecoveryCommitReceipt.Committed(this, operation);
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
