using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the orphan cleanup release execution boundary: the
    /// shared admission for a first attempt and a retry, exactly one release
    /// per call, the receipt only a completed release can mint, and what the
    /// coordinator refuses.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release. Forged receipts use the receipt's own factory
    /// wherever the shape allows it, and its private constructor only for the
    /// unreleased shape the factory refuses - the same limited technique the
    /// existing fixtures use, never a field rewrite. Each forged receipt is
    /// first asserted to name what it names. The operation's own three
    /// predicates are fixed by its own fixture and are not restated here.
    /// </remarks>
    public class NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionContractTests
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

        // ---- Admission ----

        [Test]
        public void Coordinator_NullReleaser_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () =>
                    new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                        null));
            Assert.That(ex.ParamName, Is.EqualTo("releaser"));
        }

        [Test]
        public void Execute_NullOperation_RejectedWithoutReleaserContact()
        {
            FakeReleaser releaser = new FakeReleaser();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => MakeCoordinator(releaser).Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(releaser.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_AlreadyFullyReleasedOperation_RejectedWithoutReleaserContact()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                h.IssueCleaned();

            h.Owner.Dispose();
            Assert.That(h.Owner.IsReleaseComplete, Is.True);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => MakeCoordinator(releaser).Execute(operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(releaser.CallCount, Is.EqualTo(0));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
        }

        [Test]
        public void Admission_AcceptsOnlyAFirstAttemptOrARetry()
        {
            Assert.That(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission.IsAdmissible(null),
                Is.False);

            Harness fresh = MakeHarness();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation first =
                fresh.IssueCleaned();

            Assert.That(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission
                    .IsFirstAttemptAdmissible(first),
                Is.True);
            Assert.That(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission.IsRetryAdmissible(
                    first),
                Is.False);

            // A partially released lease is a retry and no longer a first
            // attempt, because its admission validity is necessarily gone.
            Harness partial = MakeHarness(throwingFirstRelease: true);
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation retry =
                partial.IssueFailed();
            Assert.Throws<AggregateException>(() => partial.Owner.Dispose());

            Assert.That(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission
                    .IsFirstAttemptAdmissible(retry),
                Is.False);
            Assert.That(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission.IsRetryAdmissible(
                    retry),
                Is.True);

            // A completed release is neither.
            partial.Owner.Dispose();

            Assert.That(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission.IsAdmissible(retry),
                Is.False);
        }

        // ---- Both cleanup shapes release the same way ----

        [Test]
        public void Execute_AfterACleanedCleanup_ReleasesOnceAndAttestsIt()
        {
            AssertFirstAttemptReleases(
                h => h.IssueCleaned(),
                NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned);
        }

        [Test]
        public void Execute_AfterAFailedCleanup_ReleasesOnceAndAttestsIt()
        {
            AssertFirstAttemptReleases(
                h => h.IssueFailed(),
                NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed);
        }

        [Test]
        public void Receipt_ForwardsTheOperationGraph()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                h.IssueCleaned();

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                MakeCoordinator(releaser).Execute(operation);

            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(ReferenceEquals(receipt.CleanupOperation, h.CleanupOperation), Is.True);
            // The result's own references are forwarded rather than rebuilt,
            // and its validity lapses with the released lock - which the
            // receipt never rests on.
            Assert.That(ReferenceEquals(receipt.CleanupResult.Cleaner, h.Cleaner), Is.True);
            Assert.That(
                ReferenceEquals(receipt.CleanupResult.CleanupOperation, h.CleanupOperation),
                Is.True);
            Assert.That(receipt.CleanupResult.IsValid, Is.False);
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(ReferenceEquals(receipt.OwnershipLease, h.Owner), Is.True);
            Assert.That(ReferenceEquals(receipt.Decision, h.Decision), Is.True);
            Assert.That(ReferenceEquals(receipt.Snapshot, h.Decision.Snapshot), Is.True);
            Assert.That(ReferenceEquals(receipt.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(
                ReferenceEquals(receipt.LockIdentityEvidence, h.LockIdentityEvidence), Is.True);
            Assert.That(ReferenceEquals(receipt.RootLayout, h.Layout), Is.True);
            Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(
                ReferenceEquals(receipt.RunInitializationId, operation.RunInitializationId),
                Is.True);
            Assert.That(receipt.CleanupStatus, Is.EqualTo(operation.CleanupStatus));
        }

        // ---- Partial release and retry ----

        [Test]
        public void Execute_PartialRelease_PropagatesThenTheSameOperationSucceedsOnce()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                h.IssueCleaned();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator coordinator =
                MakeCoordinator(releaser);

            AggregateException failure = Assert.Throws<AggregateException>(
                () => coordinator.Execute(operation));

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(releaser.LastReceipt, Is.Null);

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                coordinator.Execute(operation);

            Assert.That(releaser.CallCount, Is.EqualTo(2));

            // Only the handle that had not been released is disposed again.
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
        }

        [Test]
        public void Execute_ReleaserException_PropagatesSameReferenceAndIsNotRetried()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("release failed");
            FakeReleaser releaser = new FakeReleaser { ExceptionToThrow = failure };

            IOException thrown = Assert.Throws<IOException>(
                () => MakeCoordinator(releaser).Execute(h.IssueCleaned()));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
        }

        // ---- Corrupt receipts ----

        [Test]
        public void Execute_NullReceipt_Rejected()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser
            {
                SkipRelease = true,
                Forge = (self, operation) => null,
            };

            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(releaser).Execute(h.IssueCleaned()));
            Assert.That(releaser.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ReceiptOfAnotherReleaser_Rejected()
        {
            Harness h = MakeHarness();
            FakeReleaser other = new FakeReleaser();
            FakeReleaser releaser = new FakeReleaser
            {
                Forge = (self, operation) =>
                {
                    NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt forged =
                        NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                            other, operation);

                    Assert.That(ReferenceEquals(forged.Operation, operation), Is.True);
                    Assert.That(ReferenceEquals(forged.Releaser, other), Is.True);
                    Assert.That(forged.IsValid, Is.True);
                    return forged;
                },
            };

            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(releaser).Execute(h.IssueCleaned()));
            Assert.That(releaser.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ReceiptOfAnotherOperation_Rejected()
        {
            Harness h = MakeHarness();
            Harness other = MakeHarness();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation otherOperation =
                other.IssueCleaned();
            other.Owner.Dispose();

            FakeReleaser releaser = new FakeReleaser
            {
                Forge = (self, operation) =>
                {
                    NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt forged =
                        NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                            self, otherOperation);

                    Assert.That(ReferenceEquals(forged.Releaser, self), Is.True);
                    Assert.That(ReferenceEquals(forged.Operation, otherOperation), Is.True);
                    Assert.That(ReferenceEquals(forged.Operation, operation), Is.False);
                    Assert.That(forged.IsValid, Is.True);
                    return forged;
                },
            };

            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(releaser).Execute(h.IssueCleaned()));
            Assert.That(releaser.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ReceiptWhoseLeaseWasNeverReleased_Rejected()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser
            {
                SkipRelease = true,
                Forge = (self, operation) =>
                {
                    NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt forged =
                        MakeReceipt(self, operation);

                    // It names the right releaser and operation; only the
                    // release itself never happened.
                    Assert.That(ReferenceEquals(forged.Releaser, self), Is.True);
                    Assert.That(ReferenceEquals(forged.Operation, operation), Is.True);
                    Assert.That(operation.OwnershipLease.IsReleaseComplete, Is.False);
                    Assert.That(forged.IsValid, Is.False);
                    return forged;
                },
            };

            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(releaser).Execute(h.IssueCleaned()));
            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Receipt_FactoryRejectsNullOrAnUnreleasedLease()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                h.IssueCleaned();

            Assert.Throws<ArgumentNullException>(
                () => NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                    null, operation));
            Assert.Throws<ArgumentNullException>(
                () => NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                    releaser, null));

            // The lease is still held, so there is nothing to attest.
            Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                    releaser, operation));
        }

        // ---- Purity and shape ----

        [Test]
        public void Execute_ChangesNothingButTheLease()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                h.IssueFailed();
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = operation.Snapshot;

            MakeCoordinator(releaser).Execute(operation);

            Assert.That(ReferenceEquals(operation.Snapshot, snapshot), Is.True);
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Absent));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);
            Assert.That(operation.CleanupStatus,
                Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed));
            Assert.That(operation.CleanupResult.Receipt, Is.Null);
            Assert.That(ReferenceEquals(operation.CleanupOperation, h.CleanupOperation), Is.True);
            Assert.That(ReferenceEquals(operation.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(operation.OpenOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(ReferenceEquals(operation.RootLayout, h.Layout), Is.True);

            // The release is process-local: nothing was created on disk.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Shapes_ReceiptHoldsTwoReferencesAndTheCoordinatorOne()
        {
            Type receipt = typeof(NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt);
            Assert.That(receipt.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(receipt), Is.False);
            AssertReadonlyFields(
                receipt,
                typeof(INvencRunPublicationRecoveryIncompleteOwnershipReleaser),
                typeof(NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation));
            Assert.That(
                receipt.GetConstructors(BindingFlags.Instance | BindingFlags.Public), Is.Empty);

            Type coordinator =
                typeof(NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator);
            Assert.That(coordinator.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(coordinator), Is.False);
            AssertReadonlyFields(
                coordinator, typeof(INvencRunPublicationRecoveryIncompleteOwnershipReleaser));
        }

        // ---- Fixture helpers ----

        private void AssertFirstAttemptReleases(
            Func<Harness, NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation> issue,
            NvencRunPublicationRecoveryIncompleteCleanupStatus expected)
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation = issue(h);

            Assert.That(operation.CleanupStatus, Is.EqualTo(expected));

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                MakeCoordinator(releaser).Execute(operation);

            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(ReferenceEquals(releaser.LastOperation, operation), Is.True);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);

            // The operation's admission validity ends with the release; its
            // binding does not, and the receipt rests on the binding.
            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.IsBindingIntact, Is.True);
            Assert.That(operation.CanRelease, Is.False);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
            Assert.That(receipt.CleanupStatus, Is.EqualTo(expected));
        }

        private static void AssertReadonlyFields(Type type, params Type[] expected)
        {
            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "every held reference must be readonly.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(expected), type.Name);
        }

        /// <summary>
        /// Builds a receipt through its private constructor, for the one shape
        /// the factory refuses: a lease that was never released.
        /// </summary>
        private static NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt MakeReceipt(
            INvencRunPublicationRecoveryIncompleteOwnershipReleaser releaser,
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
        {
            ConstructorInfo ctor =
                typeof(NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt)
                    .GetConstructor(
                        BindingFlags.Instance | BindingFlags.NonPublic,
                        null,
                        new[]
                        {
                            typeof(INvencRunPublicationRecoveryIncompleteOwnershipReleaser),
                            typeof(
                                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation),
                        },
                        null);

            Assert.That(ctor, Is.Not.Null, "receipt constructor not found.");

            return (NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt)ctor.Invoke(
                new object[] { releaser, operation });
        }

        private static NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
            MakeCoordinator(FakeReleaser releaser)
        {
            return new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                releaser);
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

        /// <summary>
        /// A distinct layout per call, so another Run's operation is genuinely
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
        /// One incomplete Run holding its lease, able to issue the release
        /// operation from either terminal cleanup shape.
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

            internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation IssueCleaned()
            {
                return NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                        Cleaner, CleanupOperation),
                    Owner);
            }

            internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation IssueFailed()
            {
                return NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                        Cleaner, CleanupOperation),
                    Owner);
            }
        }

        /// <summary>
        /// A releaser that disposes the exact lease its operation carries once
        /// per call, and can throw, skip the release, or forge its receipt.
        /// </summary>
        private sealed class FakeReleaser : INvencRunPublicationRecoveryIncompleteOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal bool SkipRelease { get; set; }

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

            internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt LastReceipt
            {
                get;
                private set;
            }

            public NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
            {
                CallCount++;
                LastOperation = operation;
                LastReceipt = null;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (!SkipRelease)
                {
                    // The lease's own disposal, exactly once per call; a
                    // partial failure propagates from here.
                    operation.OwnershipLease.Dispose();
                }

                LastReceipt = Forge != null
                    ? Forge(this, operation)
                    : NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                        this, operation);
                return LastReceipt;
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
