using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the orphan cleanup release retention coordinator: one
    /// operation issued once, the retained receipt as the only success latch,
    /// and a retry after a partial release through that same operation.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release, never from rewriting private state. The
    /// execution coordinator's own refusals of corrupt receipts are fixed by
    /// its fixture; what matters here is only that nothing is retained when one
    /// of them happens.
    /// </remarks>
    public class NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinatorContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

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
        }

        // ---- Construction ----

        [Test]
        public void Constructor_NullExecutionCoordinator_Rejected()
        {
            Harness h = MakeHarness();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
                    null, h.Cleaned(), h.Owner));
            Assert.That(ex.ParamName, Is.EqualTo("releaseExecution"));
        }

        [Test]
        public void Constructor_InadmissibleOperationArguments_RejectedWithoutReleaserContact()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();

            // A default cleanup result.
            Assert.That(
                Assert.Throws<ArgumentException>(
                    () => new NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
                        MakeExecution(releaser), default, h.Owner)).ParamName,
                Is.EqualTo("cleanupResult"));

            // No lease at all.
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
                        MakeExecution(releaser), h.Cleaned(), null)).ParamName,
                Is.EqualTo("ownershipLease"));

            // Another Run's lease.
            Harness other = MakeHarness();
            Assert.That(
                Assert.Throws<ArgumentException>(
                    () => new NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
                        MakeExecution(releaser), h.Cleaned(), other.Owner)).ParamName,
                Is.EqualTo("ownershipLease"));

            Assert.That(releaser.CallCount, Is.EqualTo(0));
            AssertLeaseUntouched(h);
            AssertLeaseUntouched(other);
        }

        [Test]
        public void Constructor_FromEitherCleanupShape_IssuesTheOperationOnceWithoutTouchingTheLease()
        {
            foreach (bool failed in new[] { false, true })
            {
                Harness h = MakeHarness();
                FakeReleaser releaser = new FakeReleaser();

                NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator coordinator =
                    new NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
                        MakeExecution(releaser),
                        failed ? h.Failed() : h.Cleaned(),
                        h.Owner);

                Assert.That(coordinator.Operation, Is.Not.Null);
                Assert.That(coordinator.Operation.CleanupStatus, Is.EqualTo(
                    failed
                        ? NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed
                        : NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned));
                Assert.That(coordinator.Operation.IsValid, Is.True);
                Assert.That(coordinator.Receipt, Is.Null);
                Assert.That(coordinator.IsReleased, Is.False);

                // It was issued from this Run's own cleanup graph, and no
                // release has been attempted yet.
                Assert.That(
                    ReferenceEquals(coordinator.Operation.CleanupOperation, h.CleanupOperation),
                    Is.True);
                Assert.That(
                    ReferenceEquals(coordinator.Operation.OwnershipLease, h.Owner), Is.True);
                Assert.That(releaser.CallCount, Is.EqualTo(0));
                AssertLeaseUntouched(h);
            }
        }

        // ---- The ordinary release ----

        [Test]
        public void Release_ReleasesOnceAndRetainsThatReceipt()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, releaser);

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                coordinator.Release();

            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);

            Assert.That(receipt.IsIssuedFor(releaser, coordinator.Operation), Is.True);
            Assert.That(ReferenceEquals(coordinator.Receipt, receipt), Is.True);
            Assert.That(coordinator.IsReleased, Is.True);
        }

        [Test]
        public void Release_CalledAgain_ReturnsTheSameReceiptWithoutReleasingAgain()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, releaser);

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt first =
                coordinator.Release();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt second =
                coordinator.Release();

            Assert.That(ReferenceEquals(second, first), Is.True);
            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(coordinator.IsReleased, Is.True);
        }

        // ---- Retry after a partial release ----

        [Test]
        public void Release_AfterAPartialRelease_RetriesThroughTheSameOperation()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, releaser);
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                coordinator.Operation;

            AggregateException failure = Assert.Throws<AggregateException>(
                () => coordinator.Release());

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(coordinator.Receipt, Is.Null);
            Assert.That(coordinator.IsReleased, Is.False);
            Assert.That(ReferenceEquals(coordinator.Operation, operation), Is.True);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                coordinator.Release();

            Assert.That(releaser.CallCount, Is.EqualTo(2));

            // Only the handle that had not been released is disposed again.
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(ReferenceEquals(coordinator.Receipt, receipt), Is.True);
            Assert.That(coordinator.IsReleased, Is.True);
        }

        [Test]
        public void Release_WhenTheReleaserFailsBeforeTheLease_KeepsTheFirstAttemptAvailable()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("release refused");
            FakeReleaser releaser = new FakeReleaser { ExceptionToThrow = failure };
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, releaser);
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                coordinator.Operation;

            IOException thrown = Assert.Throws<IOException>(() => coordinator.Release());

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(coordinator.Receipt, Is.Null);
            Assert.That(coordinator.IsReleased, Is.False);
            AssertLeaseUntouched(h);
            Assert.That(operation.IsValid, Is.True, "still a first attempt.");

            // The next call reuses that same operation.
            releaser.ExceptionToThrow = null;
            coordinator.Release();

            Assert.That(releaser.CallCount, Is.EqualTo(2));
            Assert.That(ReferenceEquals(releaser.LastOperation, operation), Is.True);
            Assert.That(coordinator.IsReleased, Is.True);
        }

        // ---- A released lock is not a success on its own ----

        [Test]
        public void Release_WhenAnUnusableReceiptFollowsACompletedRelease_RetainsNothing()
        {
            foreach (bool foreign in new[] { false, true })
            {
                Harness h = MakeHarness();
                FakeReleaser foreignReleaser = new FakeReleaser();
                FakeReleaser releaser = new FakeReleaser
                {
                    Forge = (self, operation) => foreign
                        ? NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                            foreignReleaser, operation)
                        : null,
                };
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator coordinator =
                    MakeCoordinator(h, releaser);

                // The lease really is released by that call; the receipt is
                // simply unusable.
                Assert.Throws<InvalidOperationException>(() => coordinator.Release());

                Assert.That(h.Owner.IsReleaseComplete, Is.True, "foreign=" + foreign);
                Assert.That(coordinator.Receipt, Is.Null, "foreign=" + foreign);
                Assert.That(coordinator.IsReleased, Is.False, "foreign=" + foreign);

                // The next call stops at admission instead of inferring a
                // success from the released lock, and never reaches the
                // releaser again.
                Assert.Throws<InvalidOperationException>(() => coordinator.Release());
                Assert.That(releaser.CallCount, Is.EqualTo(1), "foreign=" + foreign);
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1), "foreign=" + foreign);
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1), "foreign=" + foreign);
            }
        }

        // ---- What this boundary must leave alone ----

        [Test]
        public void Release_ChangesNothingButTheLease()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator coordinator =
                new NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
                    MakeExecution(releaser), h.Failed(), h.Owner);
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                coordinator.Operation;
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = operation.Snapshot;

            coordinator.Release();

            Assert.That(operation.CleanupStatus,
                Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed));
            Assert.That(operation.CleanupResult.Receipt, Is.Null);
            Assert.That(ReferenceEquals(operation.CleanupResult.Cleaner, h.Cleaner), Is.True);
            Assert.That(
                ReferenceEquals(operation.CleanupOperation, h.CleanupOperation), Is.True);
            Assert.That(ReferenceEquals(operation.Decision, h.Decision), Is.True);
            Assert.That(ReferenceEquals(operation.Snapshot, snapshot), Is.True);
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Absent));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);
            Assert.That(ReferenceEquals(operation.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(
                ReferenceEquals(operation.LockIdentityEvidence, h.LockIdentityEvidence), Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, h.Layout), Is.True);

            // The release is process-local: nothing was created on disk.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Coordinator_HoldsThreeFieldsAndOnlyTheReceiptIsMutable()
        {
            Type type =
                typeof(NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Length, Is.EqualTo(3));

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                types.Add(field.FieldType);

                bool isReceipt = field.FieldType
                    == typeof(NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt);
                Assert.That(field.IsInitOnly, Is.EqualTo(!isReceipt),
                    "only the retained receipt may be assignable.");
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(
                    NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator),
                typeof(NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation),
                typeof(NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt),
            }));
        }

        // ---- Fixture helpers ----

        private static void AssertLeaseUntouched(Harness h)
        {
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Owner.CanRelease, Is.True);
            Assert.That(h.Owner.IsReleaseComplete, Is.False);
        }

        private static NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
            MakeExecution(FakeReleaser releaser)
        {
            return new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                releaser);
        }

        private static NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator
            MakeCoordinator(Harness h, FakeReleaser releaser)
        {
            return new NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
                MakeExecution(releaser), h.Cleaned(), h.Owner);
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

            NvencRunPublicationRecoveryDecision decision =
                NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout),
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        true,
                        default));

            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));

            return new Harness(
                layout,
                openOutcome,
                decision,
                NvencRunPublicationRecoveryIncompleteCleanupOperation.Create(decision, owner),
                owner,
                firstHandle,
                secondHandle);
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
        /// One incomplete Run holding its lease, able to produce either
        /// terminal cleanup result over the same graph.
        /// </summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                NvencRunPublicationRecoveryDecision decision,
                NvencRunPublicationRecoveryIncompleteCleanupOperation cleanupOperation,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                Decision = decision;
                CleanupOperation = cleanupOperation;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal NvencRunPublicationRecoveryDecision Decision { get; }

            internal NvencRunPublicationRecoveryIncompleteCleanupOperation CleanupOperation { get; }

            internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
                Decision.Snapshot.Operation.LockIdentityEvidence;

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal FakeCleaner Cleaner { get; } = new FakeCleaner();

            internal NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Cleaned()
            {
                return NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                    Cleaner, CleanupOperation);
            }

            internal NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Failed()
            {
                return NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                    Cleaner, CleanupOperation);
            }
        }

        /// <summary>
        /// A releaser that disposes the exact lease its operation carries once
        /// per call, and can throw before that or forge its receipt.
        /// </summary>
        private sealed class FakeReleaser : INvencRunPublicationRecoveryIncompleteOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal Func<
                FakeReleaser,
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation,
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt> Forge
            { get; set; }

            internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation LastOperation
            {
                get;
                private set;
            }

            public NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                // The lease's own disposal, exactly once per call; a partial
                // failure propagates from here.
                operation.OwnershipLease.Dispose();

                return Forge != null
                    ? Forge(this, operation)
                    : NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                        this, operation);
            }
        }

        /// <summary>
        /// A cleaner that never runs: it only names the attempt the ordinary
        /// result factories mint here.
        /// </summary>
        private sealed class FakeCleaner : INvencRunPublicationRecoveryIncompleteCleaner
        {
            public NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
            {
                throw new NotSupportedException("This fixture never runs a cleanup.");
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
