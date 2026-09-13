using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the stopping publication recovery release execution
    /// boundary: the shared admission for a first attempt and a retry, exactly
    /// one release per call, the receipt only a completed release can mint, and
    /// what the coordinator refuses.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release. Forged receipts use the receipt's own factory
    /// wherever the shape allows it, and its private constructor only for the
    /// unreleased shape the factory refuses - the same limited technique the
    /// existing fixtures use, never a field rewrite. Each forged receipt is
    /// first asserted to name what it names.
    /// </remarks>
    public class NvencRunPublicationRecoveryStopOwnershipReleaseExecutionContractTests
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
        public void Coordinator_NullReleaser_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
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
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = h.IssueCollision();

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
                NvencRunPublicationRecoveryStopOwnershipReleaseAdmission.IsAdmissible(null),
                Is.False);

            Harness fresh = MakeHarness();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation first = fresh.IssueCollision();

            Assert.That(
                NvencRunPublicationRecoveryStopOwnershipReleaseAdmission
                    .IsFirstAttemptAdmissible(first),
                Is.True);
            Assert.That(
                NvencRunPublicationRecoveryStopOwnershipReleaseAdmission.IsRetryAdmissible(first),
                Is.False);

            // A partially released lease is a retry and no longer a first
            // attempt, because its admission validity is necessarily gone.
            Harness partial = MakeHarness(throwingFirstRelease: true);
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation retry =
                partial.IssueDeferred();
            Assert.Throws<AggregateException>(() => partial.Owner.Dispose());

            Assert.That(
                NvencRunPublicationRecoveryStopOwnershipReleaseAdmission
                    .IsFirstAttemptAdmissible(retry),
                Is.False);
            Assert.That(
                NvencRunPublicationRecoveryStopOwnershipReleaseAdmission.IsRetryAdmissible(retry),
                Is.True);

            // A completed release is neither.
            partial.Owner.Dispose();

            Assert.That(
                NvencRunPublicationRecoveryStopOwnershipReleaseAdmission.IsAdmissible(retry),
                Is.False);
        }

        // ---- Both stopping shapes release the same way ----

        [Test]
        public void Execute_Collision_ReleasesOnceAndAttestsIt()
        {
            AssertFirstAttemptReleases(
                h => h.IssueCollision(),
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void Execute_DeferredVerification_ReleasesOnceAndAttestsIt()
        {
            AssertFirstAttemptReleases(
                h => h.IssueDeferred(), NvencRunPublicationRecoveryDisposition.Deferred);
        }

        [Test]
        public void Receipt_ForwardsTheOperationGraph()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = h.IssueCollision();

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                MakeCoordinator(releaser).Execute(operation);

            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(ReferenceEquals(receipt.Decision, operation.Decision), Is.True);
            Assert.That(ReferenceEquals(receipt.Snapshot, operation.Snapshot), Is.True);
            Assert.That(ReferenceEquals(receipt.OwnershipLease, h.Owner), Is.True);
            Assert.That(ReferenceEquals(receipt.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(
                    receipt.LockIdentityEvidence, h.LockIdentityEvidence),
                Is.True);
            Assert.That(receipt.Disposition, Is.EqualTo(operation.Disposition));
            Assert.That(ReferenceEquals(receipt.RootLayout, h.Layout), Is.True);
            Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(ReferenceEquals(
                    receipt.RunInitializationId, operation.RunInitializationId),
                Is.True);
        }

        // ---- Partial release and retry ----

        [Test]
        public void Execute_PartialRelease_PropagatesThenTheSameOperationSucceedsOnce()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = h.IssueCollision();
            NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator coordinator =
                MakeCoordinator(releaser);

            AggregateException failure = Assert.Throws<AggregateException>(
                () => coordinator.Execute(operation));

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(releaser.LastReceipt, Is.Null);

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(operation.IsBindingIntact, Is.True);
            Assert.That(operation.CanRelease, Is.True);
            Assert.That(operation.IsValid, Is.False);

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                coordinator.Execute(operation);

            Assert.That(releaser.CallCount, Is.EqualTo(2));
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
                () => MakeCoordinator(releaser).Execute(h.IssueCollision()));

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
                () => MakeCoordinator(releaser).Execute(h.IssueCollision()));
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
                    NvencRunPublicationRecoveryStopOwnershipReleaseReceipt forged =
                        NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
                            other, operation);

                    Assert.That(ReferenceEquals(forged.Operation, operation), Is.True);
                    Assert.That(ReferenceEquals(forged.Releaser, other), Is.True);
                    Assert.That(forged.IsValid, Is.True);
                    return forged;
                },
            };

            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(releaser).Execute(h.IssueCollision()));
            Assert.That(releaser.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ReceiptOfAnotherOperation_Rejected()
        {
            Harness h = MakeHarness();
            Harness other = MakeHarness();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation otherOperation =
                other.IssueCollision();
            other.Owner.Dispose();

            FakeReleaser releaser = new FakeReleaser
            {
                Forge = (self, operation) =>
                {
                    NvencRunPublicationRecoveryStopOwnershipReleaseReceipt forged =
                        NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
                            self, otherOperation);

                    Assert.That(ReferenceEquals(forged.Releaser, self), Is.True);
                    Assert.That(ReferenceEquals(forged.Operation, otherOperation), Is.True);
                    Assert.That(ReferenceEquals(forged.Operation, operation), Is.False);
                    Assert.That(forged.IsValid, Is.True);
                    return forged;
                },
            };

            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(releaser).Execute(h.IssueCollision()));
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
                    NvencRunPublicationRecoveryStopOwnershipReleaseReceipt forged =
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
                () => MakeCoordinator(releaser).Execute(h.IssueCollision()));
            Assert.That(releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Receipt_FactoryRejectsNullOrAnUnreleasedLease()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = h.IssueCollision();

            Assert.Throws<ArgumentNullException>(
                () => NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
                    null, operation));
            Assert.Throws<ArgumentNullException>(
                () => NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
                    releaser, null));

            // The lease is still held, so there is nothing to attest.
            Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
                    releaser, operation));
        }

        // ---- Purity and shape ----

        [Test]
        public void Execute_ChangesNothingButTheLease()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = h.IssueCollision();
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = operation.Snapshot;

            MakeCoordinator(releaser).Execute(operation);

            Assert.That(ReferenceEquals(operation.Snapshot, snapshot), Is.True);
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Canonical));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);
            Assert.That(operation.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
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
            Type receipt = typeof(NvencRunPublicationRecoveryStopOwnershipReleaseReceipt);
            Assert.That(receipt.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(receipt), Is.False);
            AssertReadonlyFields(
                receipt,
                typeof(INvencRunPublicationRecoveryStopOwnershipReleaser),
                typeof(NvencRunPublicationRecoveryStopOwnershipReleaseOperation));
            Assert.That(
                receipt.GetConstructors(BindingFlags.Instance | BindingFlags.Public), Is.Empty);

            Type coordinator =
                typeof(NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator);
            Assert.That(coordinator.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(coordinator), Is.False);
            AssertReadonlyFields(
                coordinator, typeof(INvencRunPublicationRecoveryStopOwnershipReleaser));
        }

        // ---- Fixture helpers ----

        private void AssertFirstAttemptReleases(
            Func<Harness, NvencRunPublicationRecoveryStopOwnershipReleaseOperation> issue,
            NvencRunPublicationRecoveryDisposition expected)
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = issue(h);

            Assert.That(operation.Disposition, Is.EqualTo(expected));

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
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
            Assert.That(receipt.Disposition, Is.EqualTo(expected));
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
        private static NvencRunPublicationRecoveryStopOwnershipReleaseReceipt MakeReceipt(
            INvencRunPublicationRecoveryStopOwnershipReleaser releaser,
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
        {
            ConstructorInfo ctor =
                typeof(NvencRunPublicationRecoveryStopOwnershipReleaseReceipt).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(INvencRunPublicationRecoveryStopOwnershipReleaser),
                        typeof(NvencRunPublicationRecoveryStopOwnershipReleaseOperation),
                    },
                    null);

            Assert.That(ctor, Is.Not.Null, "receipt constructor not found.");

            return (NvencRunPublicationRecoveryStopOwnershipReleaseReceipt)ctor.Invoke(
                new object[] { releaser, operation });
        }

        private static NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator
            MakeCoordinator(FakeReleaser releaser)
        {
            return new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
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

            return new Harness(
                layout,
                openOutcome,
                new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout),
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

        /// <summary>One Run holding its lease, able to stop in either shape.</summary>
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
            internal NvencRunPublicationRecoveryStopOwnershipReleaseOperation IssueCollision()
            {
                return NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    NvencRunPublicationRecoveryClassifier.Classify(
                        new NvencRunPublicationRecoveryInspectionSnapshot(
                            _inspection,
                            CaptureRunPublicationDocumentObservationStatus.Canonical,
                            MakePlan(_inspection),
                            true,
                            default)),
                    Owner);
            }

            /// <summary>A canonical plan whose chunk could not be verified.</summary>
            internal NvencRunPublicationRecoveryStopOwnershipReleaseOperation IssueDeferred()
            {
                CapturePublicationPlan plan = MakePlan(_inspection);

                return NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    NvencRunPublicationRecoveryClassifier.Classify(
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
                                0))),
                    Owner);
            }
        }

        /// <summary>
        /// Releases the operation's own lease once per call and mints the
        /// ordinary receipt, unless the test asked for something else.
        /// </summary>
        private sealed class FakeReleaser : INvencRunPublicationRecoveryStopOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal bool SkipRelease { get; set; }

            internal Func<
                FakeReleaser,
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation,
                NvencRunPublicationRecoveryStopOwnershipReleaseReceipt> Forge
            { get; set; }

            internal NvencRunPublicationRecoveryStopOwnershipReleaseOperation LastOperation
            {
                get;
                private set;
            }

            internal NvencRunPublicationRecoveryStopOwnershipReleaseReceipt LastReceipt
            {
                get;
                private set;
            }

            public NvencRunPublicationRecoveryStopOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
            {
                CallCount++;
                LastOperation = operation;

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
                    : NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
                        this, operation);
                return LastReceipt;
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
