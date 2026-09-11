using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the stopped publication recovery's release retention
    /// boundary: one operation issued once, a receipt retained only after a
    /// returned execution, and a partial release finished later through that
    /// same operation.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release. The unusable-receipt cases really release the
    /// lease first and only then corrupt the return value, and each asserts
    /// that before the coordinator is given the chance to refuse it, so no
    /// refusal can pass for the wrong reason.
    /// </remarks>
    public class NvencRunPublicationRecoveryStopOwnershipReleaseCoordinatorContractTests
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

        // ---- Construction ----

        [Test]
        public void Constructor_NullExecutionCoordinator_Rejected()
        {
            Harness h = MakeHarness();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                    h.Collision(), h.Owner, null));

            Assert.That(ex.ParamName, Is.EqualTo("releaseExecution"));
        }

        [Test]
        public void Constructor_WhatTheOperationFactoryRefuses_IsRefusedHereToo()
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser();

            // A null decision, the two dispositions this stop path does not
            // own, a null lease, and another Run's lease.
            Assert.Throws<ArgumentException>(
                () => new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                    null, h.Owner, MakeExecution(releaser)));
            Assert.Throws<ArgumentException>(
                () => new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                    h.Incomplete(), h.Owner, MakeExecution(releaser)));
            Assert.Throws<ArgumentException>(
                () => new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                    h.Recoverable(), h.Owner, MakeExecution(releaser)));
            Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                    h.Collision(), null, MakeExecution(releaser)));

            Harness other = MakeHarness();
            Assert.Throws<ArgumentException>(
                () => new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                    h.Collision(), other.Owner, MakeExecution(releaser)));

            Assert.That(releaser.CallCount, Is.EqualTo(0));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Constructor_FromEitherStoppingShape_FixesOneOperationWithoutTouchingTheLease()
        {
            foreach (bool deferred in new[] { false, true })
            {
                Harness h = MakeHarness();
                FakeReleaser releaser = new FakeReleaser();

                NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator coordinator =
                    new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                        deferred ? h.Deferred() : h.Collision(),
                        h.Owner,
                        MakeExecution(releaser));

                Assert.That(coordinator.Operation, Is.Not.Null);
                Assert.That(coordinator.Operation.Disposition, Is.EqualTo(
                    deferred
                        ? NvencRunPublicationRecoveryDisposition.Deferred
                        : NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
                Assert.That(coordinator.Operation.IsValid, Is.True);
                Assert.That(ReferenceEquals(coordinator.Operation, coordinator.Operation), Is.True);
                Assert.That(coordinator.Receipt, Is.Null);
                Assert.That(coordinator.IsReleased, Is.False);

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
            NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, h.Collision(), releaser);

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt = coordinator.Release();

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
            NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, h.Deferred(), releaser);

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt first = coordinator.Release();
            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt second = coordinator.Release();

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
            NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, h.Collision(), releaser);
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation =
                coordinator.Operation;

            AggregateException failure = Assert.Throws<AggregateException>(
                () => coordinator.Release());

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(coordinator.Receipt, Is.Null);
            Assert.That(coordinator.IsReleased, Is.False);
            Assert.That(ReferenceEquals(coordinator.Operation, operation), Is.True);
            Assert.That(operation.CanRelease, Is.True);

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt = coordinator.Release();

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
            NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, h.Collision(), releaser);
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation =
                coordinator.Operation;

            IOException thrown = Assert.Throws<IOException>(() => coordinator.Release());

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(coordinator.Receipt, Is.Null);
            Assert.That(coordinator.IsReleased, Is.False);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(operation.IsValid, Is.True);

            releaser.ExceptionToThrow = null;

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt = coordinator.Release();

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
                Assert.That(operation.CanRelease, Is.False);
                return null;
            });
        }

        [Test]
        public void Release_ReleasedLeaseWithAForeignReceipt_NeitherRetainsNorInfersSuccess()
        {
            FakeReleaser other = new FakeReleaser();

            AssertUnusableReceiptStops((self, operation) =>
            {
                NvencRunPublicationRecoveryStopOwnershipReleaseReceipt forged =
                    NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
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
            NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, h.Collision(), releaser);
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation =
                coordinator.Operation;
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = operation.Snapshot;

            coordinator.Release();

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
        public void Coordinator_HoldsItsDependencyItsOperationAndTheRetainedReceiptOnly()
        {
            Type type = typeof(NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator);

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
                    == typeof(NvencRunPublicationRecoveryStopOwnershipReleaseReceipt);
                Assert.That(
                    field.IsInitOnly,
                    Is.EqualTo(!isRetainedReceipt),
                    "only the retained receipt may be assignable.");
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator),
                typeof(NvencRunPublicationRecoveryStopOwnershipReleaseOperation),
                typeof(NvencRunPublicationRecoveryStopOwnershipReleaseReceipt),
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
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation,
                NvencRunPublicationRecoveryStopOwnershipReleaseReceipt> forge)
        {
            Harness h = MakeHarness();
            FakeReleaser releaser = new FakeReleaser { Forge = forge };
            NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator coordinator =
                MakeCoordinator(h, h.Collision(), releaser);

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

        private static NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator
            MakeExecution(FakeReleaser releaser)
        {
            return new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
                releaser);
        }

        private static NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator MakeCoordinator(
            Harness h,
            NvencRunPublicationRecoveryDecision decision,
            FakeReleaser releaser)
        {
            return new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                decision, h.Owner, MakeExecution(releaser));
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
        /// Releases the operation's own lease once per call and mints the
        /// ordinary receipt, unless the test asked for something else.
        /// </summary>
        private sealed class FakeReleaser : INvencRunPublicationRecoveryStopOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

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

            public NvencRunPublicationRecoveryStopOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
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
                    : NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
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
