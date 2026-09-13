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
    /// retention boundary: one operation issued once, a receipt retained only
    /// after a returned execution, and a partial release finished later
    /// through that same operation.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release. The unusable-receipt case really releases the
    /// lease first and only then corrupts the return value, so the test says
    /// what was true and what was not.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinatorContractTests
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

        // ---- Construction ----

        [Test]
        public void Constructor_NullExecutionCoordinator_Rejected()
        {
            Harness h = MakeHarness();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator(
                    h.CleanupResult, h.Owner, null));

            Assert.That(ex.ParamName, Is.EqualTo("releaseExecution"));
        }

        [Test]
        public void Constructor_DefaultResultOrForeignLease_RejectedByTheOperationFactory()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();

            Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator(
                    default, h.Owner, MakeExecution(releaser)));
            Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator(
                    h.CleanupResult, null, MakeExecution(releaser)));

            // Another Run's lease.
            Harness other = MakeHarness();
            Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator(
                    h.CleanupResult, other.Owner, MakeExecution(releaser)));

            Assert.That(releaser.CallCount, Is.EqualTo(0));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Constructor_FromEitherCleanupShape_FixesOneOperationWithoutTouchingTheLease()
        {
            foreach (bool cleanupFailed in new[] { false, true })
            {
                Harness h = MakeHarness(cleanupFailed: cleanupFailed);
                FakeReleaser releaser = new FakeReleaser();

                NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator coordinator =
                    new NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator(
                        h.CleanupResult, h.Owner, MakeExecution(releaser));

                Assert.That(coordinator.Operation, Is.Not.Null);
                Assert.That(coordinator.Operation.CleanupStatus, Is.EqualTo(
                    cleanupFailed
                        ? NvencRunCaptureCompleteRecoveryCleanupStatus.Failed
                        : NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned));
                Assert.That(coordinator.Operation.IsValid, Is.True);
                Assert.That(ReferenceEquals(coordinator.Operation, coordinator.Operation), Is.True);
                Assert.That(coordinator.Receipt, Is.Null);
                Assert.That(coordinator.IsReleased, Is.False);

                // The lease is untouched by construction.
                Assert.That(releaser.CallCount, Is.EqualTo(0));
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
                Assert.That(h.Owner.IsCreated, Is.True);
            }
        }

        // ---- A first release, and a second call ----

        [Test]
        public void Release_FirstAttempt_ReleasesOnceAndRetainsTheReceipt()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, releaser);

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt = coordinator.Release();

            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);

            Assert.That(ReferenceEquals(coordinator.Receipt, receipt), Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, coordinator.Operation), Is.True);
            Assert.That(coordinator.IsReleased, Is.True);
        }

        [Test]
        public void Release_Twice_ReturnsTheSameReceiptWithoutTouchingAnythingAgain()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, releaser);

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt first = coordinator.Release();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt second = coordinator.Release();

            Assert.That(ReferenceEquals(second, first), Is.True);
            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
        }

        // ---- Retryable failures ----

        [Test]
        public void Release_PartialRelease_RetainsNothingThenFinishesWithTheSameOperation()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            FakeReleaser releaser = new FakeReleaser();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, releaser);
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation =
                coordinator.Operation;

            AggregateException failure = Assert.Throws<AggregateException>(
                () => coordinator.Release());

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(coordinator.Receipt, Is.Null);
            Assert.That(coordinator.IsReleased, Is.False);
            Assert.That(ReferenceEquals(coordinator.Operation, operation), Is.True);
            Assert.That(operation.CanRelease, Is.True);

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt = coordinator.Release();

            Assert.That(releaser.CallCount, Is.EqualTo(2));
            Assert.That(ReferenceEquals(releaser.LastOperation, operation), Is.True);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
            Assert.That(coordinator.IsReleased, Is.True);
        }

        [Test]
        public void Release_ReleaserFailureBeforeTheLeaseIsTouched_LeavesTheOperationReusable()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("release failed");
            FakeReleaser releaser = new FakeReleaser { ExceptionToThrow = failure };
            NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, releaser);
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation =
                coordinator.Operation;

            IOException thrown = Assert.Throws<IOException>(() => coordinator.Release());

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(coordinator.Receipt, Is.Null);
            Assert.That(coordinator.IsReleased, Is.False);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(operation.IsValid, Is.True);

            // The same operation is used again on the next call.
            releaser.ExceptionToThrow = null;

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt = coordinator.Release();

            Assert.That(releaser.CallCount, Is.EqualTo(2));
            Assert.That(ReferenceEquals(releaser.LastOperation, operation), Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
        }

        // ---- A finished release with an unusable receipt ----

        [Test]
        public void Release_ReleasedLeaseWithANullReceipt_NeitherRetainsNorInfersSuccess()
        {
            AssertUnusableReceiptStops((self, operation) =>
            {
                // The lock really is gone; only the return value is missing.
                Assert.That(operation.OwnershipLease.IsReleaseComplete, Is.True);
                return null;
            });
        }

        [Test]
        public void Release_ReleasedLeaseWithAForeignReceipt_NeitherRetainsNorInfersSuccess()
        {
            FakeReleaser other = new FakeReleaser();

            AssertUnusableReceiptStops((self, operation) =>
            {
                NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt forged =
                    NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt.Released(
                        other, operation);

                // A genuinely completed release, attested for this exact
                // operation - by someone else.
                Assert.That(operation.OwnershipLease.IsReleaseComplete, Is.True);
                Assert.That(ReferenceEquals(forged.Operation, operation), Is.True);
                Assert.That(ReferenceEquals(forged.Releaser, other), Is.True);
                Assert.That(forged.IsValid, Is.True);
                return forged;
            });
        }

        // ---- Purity and shape ----

        [Test]
        public void Release_ChangesNothingButTheLease()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, releaser);
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation =
                coordinator.Operation;

            coordinator.Release();

            Assert.That(operation.CleanupResult.Status, Is.EqualTo(h.CleanupResult.Status));
            Assert.That(ReferenceEquals(
                    operation.CleanupOperation, h.CleanupResult.Operation),
                Is.True);
            Assert.That(ReferenceEquals(operation.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(operation.OpenOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(ReferenceEquals(
                    operation.LockIdentityEvidence, h.LockIdentityEvidence),
                Is.True);
            Assert.That(operation.LockIdentityEvidence.IsBoundTo(h.Owner), Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, h.Layout), Is.True);

            // The release is process-local: nothing was created on disk.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Coordinator_HoldsItsDependencyItsOperationAndTheRetainedReceiptOnly()
        {
            Type type = typeof(NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Length, Is.EqualTo(3));

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                types.Add(field.FieldType);

                bool isRetainedReceipt = field.FieldType
                    == typeof(NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt);
                Assert.That(
                    field.IsInitOnly,
                    Is.EqualTo(!isRetainedReceipt),
                    "only the retained receipt may be assignable.");
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator),
                typeof(NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation),
                typeof(NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt),
            }));
        }

        // ---- Fixture helpers ----

        /// <summary>
        /// A release that finishes the lock but returns something the execution
        /// coordinator refuses: nothing is retained, and the next call stops at
        /// admission without going near the releaser again.
        /// </summary>
        private void AssertUnusableReceiptStops(
            Func<
                FakeReleaser,
                NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation,
                NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt> forge)
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser { Forge = forge };
            NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, releaser);

            Assert.Throws<InvalidOperationException>(() => coordinator.Release());

            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(coordinator.Receipt, Is.Null);
            Assert.That(coordinator.IsReleased, Is.False);

            // The lease's own state is not evidence of a successful release.
            Assert.Throws<InvalidOperationException>(() => coordinator.Release());

            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(coordinator.Receipt, Is.Null);
            Assert.That(coordinator.IsReleased, Is.False);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
        }

        private static NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator
            MakeExecution(FakeReleaser releaser)
        {
            return new NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator(
                releaser);
        }

        private static NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator MakeCoordinator(
            Harness h, FakeReleaser releaser)
        {
            return new NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator(
                h.CleanupResult, h.Owner, MakeExecution(releaser));
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

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult =
                new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                        new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(
                            new FakeCleaner
                            {
                                Status = cleanupFailed
                                    ? NvencRunCaptureCompleteRecoveryCleanupStatus.Failed
                                    : NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned,
                            }))
                    .Execute(captureComplete);

            return new Harness(
                layout,
                openOutcome,
                recoveryOperation.LockIdentityEvidence,
                cleanupResult,
                owner,
                firstHandle,
                secondHandle);
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
        }

        /// <summary>
        /// Releases the operation's own lease once per call and mints the
        /// ordinary receipt, unless the test asked for something else.
        /// </summary>
        private sealed class FakeReleaser : INvencRunCaptureCompleteRecoveryOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal Func<
                FakeReleaser,
                NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation,
                NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt> Forge
            { get; set; }

            internal NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation LastOperation
            {
                get;
                private set;
            }

            public NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt Release(
                NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    // Before the lease is touched.
                    throw ExceptionToThrow;
                }

                operation.OwnershipLease.Dispose();

                return Forge != null
                    ? Forge(this, operation)
                    : NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt.Released(
                        this, operation);
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
