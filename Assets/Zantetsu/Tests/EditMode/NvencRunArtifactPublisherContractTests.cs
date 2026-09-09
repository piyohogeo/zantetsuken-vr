using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 production Fresh NVENC Run chunk
    /// artifact publisher: validation before any side effect, the single fixed
    /// verification buffer reservation, the at-most-one backend call, and the
    /// exact attempt result and receipt correlation. Uses the committed Run
    /// pipeline to mint a valid publication operation together with a
    /// deterministic tracking filesystem backend; no real GPU, NVENC,
    /// filesystem, sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunArtifactPublisherContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const int ChunkLength = 64;

        private const int SmallBufferLength = 8;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Validation before any side effect ----

        [Test]
        public void Constructor_NullStoreOrFileSystem_Throws()
        {
            CaptureArtifactVerificationBufferPool pool = MakePool();
            CaptureArtifactFileStore store = MakeStore(MakeLayout(), pool);

            ArgumentNullException storeEx = Assert.Throws<ArgumentNullException>(
                () => new NvencRunArtifactPublisher(null, new TrackingFileSystem()));
            Assert.That(storeEx.ParamName, Is.EqualTo("store"));

            ArgumentNullException fileSystemEx = Assert.Throws<ArgumentNullException>(
                () => new NvencRunArtifactPublisher(store, null));
            Assert.That(fileSystemEx.ParamName, Is.EqualTo("fileSystem"));
        }

        [Test]
        public void Publish_Null_ArgumentNullException_NoReservationNoFilesystemContact()
        {
            CaptureArtifactVerificationBufferPool pool = MakePool();
            TrackingFileSystem fileSystem = new TrackingFileSystem();
            NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                MakeStore(MakeLayout(), pool), fileSystem);

            long before = ProbeGeneration(pool);
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => publisher.Publish(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));

            AssertNoReservationSince(pool, before);
            Assert.That(fileSystem.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Publish_InvalidOperation_ArgumentException_NoReservationNoFilesystemContact()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                // Break the exact correlation so the operation is invalid.
                SetField(h.RunCoordinator, "_disposition", NvencRunEvidenceDisposition.None);
                Assert.That(operation.IsValid, Is.False);

                long before = ProbeGeneration(pool);
                ArgumentException ex = Assert.Throws<ArgumentException>(() => publisher.Publish(operation));
                Assert.That(ex.ParamName, Is.EqualTo("operation"));

                AssertNoReservationSince(pool, before);
                Assert.That(fileSystem.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Publish_ForeignRootLayout_ArgumentException_NoReservationNoFilesystemContact()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                // A content-identical but distinct root layout instance is a
                // foreign Run and must be rejected by exact reference.
                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(MakeLayout(), pool), fileSystem);

                Assert.That(ReferenceEquals(operation.RootLayout, MakeLayout()), Is.False);

                long before = ProbeGeneration(pool);
                ArgumentException ex = Assert.Throws<ArgumentException>(() => publisher.Publish(operation));
                Assert.That(ex.ParamName, Is.EqualTo("operation"));

                AssertNoReservationSince(pool, before);
                Assert.That(fileSystem.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Publish_UnsupportedFileSystem_ThrowsCapabilityError_NoReservation()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem { Supported = false };
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                long before = ProbeGeneration(pool);

                // Capability insufficiency is a configuration error, never a
                // Failed publication content outcome.
                Assert.Throws<CaptureArtifactNoFollowUnavailableException>(() => publisher.Publish(operation));

                AssertNoReservationSince(pool, before);
                Assert.That(fileSystem.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Publish_FixedChunkDescriptorShapeIsPinnedByTheIssuanceGraph()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);
                CaptureArtifactDescriptor descriptor = operation.Descriptor;

                // The publisher refuses any descriptor that is not the fixed
                // frame-sequence NvencH264IdrChunk version 1 at its fixed,
                // non-pending staging and final paths. The legitimate issuance
                // graph can only mint that exact shape - the finalization
                // receipt correlates every one of these values against the
                // finalization operation's fixed values - so this test pins the
                // shape the publisher's guards are total over rather than
                // fabricating impossible descriptors.
                Assert.That(descriptor.ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameSequence));
                Assert.That(descriptor.FormatId,
                    Is.EqualTo(NvencRunChunkArtifactDescriptorFactory.FormatId));
                Assert.That(descriptor.FormatVersion,
                    Is.EqualTo(NvencRunChunkArtifactDescriptorFactory.FormatVersion));
                Assert.That(descriptor.StagingRelativePath,
                    Is.EqualTo(NvencRunChunkArtifactDescriptorFactory.StagingRelativePath));
                Assert.That(descriptor.FinalRelativePath,
                    Is.EqualTo(NvencRunChunkArtifactDescriptorFactory.FinalRelativePath));
                Assert.That(descriptor.StagingRelativePath, Does.Not.Contain(".partial"));
                Assert.That(descriptor.FinalRelativePath, Does.Not.Contain(".partial"));
                Assert.That(descriptor.StagingRelativePath,
                    Is.Not.EqualTo(NvencRunChunkArtifactDescriptorFactory.PendingRelativePath));
                Assert.That(descriptor.ByteLength, Is.GreaterThan(0L));
                Assert.That(descriptor.ContentHash, Has.Length.EqualTo(64));
                Assert.That(descriptor.ContentHash, Is.EqualTo(descriptor.ContentHash.ToLowerInvariant()));
            }
        }

        // ---- Buffer reservation ----

        [Test]
        public void Publish_BufferUnavailable_Failed_NoFilesystemContact()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                // Hold the single fixed buffer so the reservation cannot be
                // taken; the filesystem must never be touched.
                CaptureArtifactVerificationBufferPool.Lease held = pool.TryRent();
                Assert.That(held, Is.Not.Null);

                try
                {
                    NvencRunArtifactPublicationAttemptResult result = publisher.Publish(operation);

                    Assert.That(result.IsFailed, Is.True);
                    Assert.That(result.IsPublished, Is.False);
                    Assert.That(result.Receipt, Is.Null);
                    Assert.That(ReferenceEquals(result.Publisher, publisher), Is.True);
                    Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                    Assert.That(fileSystem.CallCount, Is.EqualTo(0));
                }
                finally
                {
                    pool.Return(held);
                }

                Assert.That(pool.OutstandingRentCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Publish_UsesOnlyTheStoresSingleFixedBuffer()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                publisher.Publish(operation);

                // Exactly one reservation, and the buffer handed to the backend
                // is the pool's fixed buffer, never an array proportional to the
                // chunk length.
                Assert.That(fileSystem.CallCount, Is.EqualTo(1));
                Assert.That(fileSystem.LastBuffer, Is.Not.Null);
                Assert.That(fileSystem.LastBuffer.Length, Is.EqualTo(SmallBufferLength));
                Assert.That(fileSystem.LastBuffer.Length, Is.LessThan((int)operation.ExpectedByteLength));
                Assert.That(fileSystem.ObservedBuffers.Distinct().Count(), Is.EqualTo(1));
                Assert.That(pool.OutstandingRentCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Publish_ReturnsTheBufferOnSuccessFailureAndException()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                fileSystem.Result = true;
                publisher.Publish(operation);
                Assert.That(pool.OutstandingRentCount, Is.EqualTo(0));

                fileSystem.Result = false;
                publisher.Publish(operation);
                Assert.That(pool.OutstandingRentCount, Is.EqualTo(0));

                fileSystem.ExceptionToThrow = new InvalidOperationException("boom");
                Assert.Throws<InvalidOperationException>(() => publisher.Publish(operation));
                Assert.That(pool.OutstandingRentCount, Is.EqualTo(0));

                // The pool is still usable, so the leases were really returned.
                CaptureArtifactVerificationBufferPool.Lease lease = pool.TryRent();
                Assert.That(lease, Is.Not.Null);
                pool.Return(lease);
            }
        }

        // ---- Terminal outcomes ----

        [Test]
        public void Publish_BackendPlaced_PublishedWithExactReceipt()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem { Result = true };
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                NvencRunArtifactPublicationAttemptResult result = publisher.Publish(operation);

                Assert.That(fileSystem.CallCount, Is.EqualTo(1));
                Assert.That(result.IsPublished, Is.True);
                Assert.That(result.IsFailed, Is.False);
                Assert.That(result.IsNone, Is.False);
                Assert.That(result.Status, Is.EqualTo(NvencRunArtifactPublicationStatus.Published));
                Assert.That(ReferenceEquals(result.Publisher, publisher), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsIssuedFor(publisher, operation), Is.True);

                // The backend received the operation's exact root layout and
                // descriptor; nothing is copied or re-derived.
                Assert.That(ReferenceEquals(fileSystem.LastRootLayout, operation.RootLayout), Is.True);
                Assert.That(ReferenceEquals(fileSystem.LastDescriptor, operation.Descriptor), Is.True);
            }
        }

        [Test]
        public void Publish_BackendRefused_FailedWithoutReceipt()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem { Result = false };
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                NvencRunArtifactPublicationAttemptResult result = publisher.Publish(operation);

                Assert.That(fileSystem.CallCount, Is.EqualTo(1));
                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.IsPublished, Is.False);
                Assert.That(result.Status, Is.EqualTo(NvencRunArtifactPublicationStatus.Failed));
                Assert.That(result.Receipt, Is.Null);
                Assert.That(ReferenceEquals(result.Publisher, publisher), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
            }
        }

        [Test]
        public void Publish_BackendException_PropagatesSameInstance_NoRetry()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                InvalidOperationException boom = new InvalidOperationException("boom");
                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem { ExceptionToThrow = boom };
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                    () => publisher.Publish(operation));

                Assert.That(ReferenceEquals(thrown, boom), Is.True);
                Assert.That(fileSystem.CallCount, Is.EqualTo(1));
                Assert.That(pool.OutstandingRentCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Publish_OneCallContactsTheBackendAtMostOnce()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem { Result = false };
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                publisher.Publish(operation);
                Assert.That(fileSystem.CallCount, Is.EqualTo(1));

                // A second attempt is the caller's decision, never the
                // publisher's retry.
                publisher.Publish(operation);
                Assert.That(fileSystem.CallCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void Publish_LeavesRegistryDispositionPlanAndLeaseUnchanged()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                NvencRunLocalRegistrySlotState slotState = h.Slot.State;
                bool hasRegisteredEntry = h.Slot.HasRegisteredEntry;
                bool leaseCreated = h.SessionIssue.OwnershipLease.IsCreated;
                NvencRunPublicationServiceState serviceState = h.Service.State;
                NvencRunPublicationPlanCommitExecutionResult planCommitResult = operation.PlanCommitResult;

                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem { Result = true };
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                publisher.Publish(operation);
                AssertUnchanged();

                fileSystem.Result = false;
                publisher.Publish(operation);
                AssertUnchanged();

                void AssertUnchanged()
                {
                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                    Assert.That(h.Slot.State, Is.EqualTo(slotState));
                    Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                    Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.EqualTo(leaseCreated));
                    Assert.That(h.Service.State, Is.EqualTo(serviceState));
                    Assert.That(ReferenceEquals(operation.PlanCommitResult, planCommitResult), Is.True);
                    Assert.That(operation.IsValid, Is.True);
                }
            }
        }

        // ---- Shape ----

        [Test]
        public void Publisher_HoldsOnlyTheStoreAndTheFileSystem_NotDisposable()
        {
            Type type = typeof(NvencRunArtifactPublisher);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            Assert.That(fields, Has.Length.EqualTo(2));
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(CaptureArtifactFileStore),
                    typeof(INvencRunArtifactPublicationFileSystem),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Publish_DoesNotRetainTheOperationOrItsGraph()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                CaptureArtifactVerificationBufferPool pool = MakePool();
                TrackingFileSystem fileSystem = new TrackingFileSystem { Result = true };
                NvencRunArtifactPublisher publisher = new NvencRunArtifactPublisher(
                    MakeStore(operation.RootLayout, pool), fileSystem);

                publisher.Publish(operation);

                foreach (FieldInfo field in typeof(NvencRunArtifactPublisher).GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    object value = field.GetValue(publisher);
                    Assert.That(value, Is.Not.InstanceOf<NvencRunArtifactPublicationOperation>());
                    Assert.That(value, Is.Not.InstanceOf<NvencRunArtifactPublicationReceipt>());
                    Assert.That(value, Is.Not.InstanceOf<CaptureArtifactDescriptor>());
                    Assert.That(value, Is.Not.InstanceOf<CaptureArtifactPublishReservation>());
                    Assert.That(value, Is.Not.InstanceOf<byte[]>());
                }
            }
        }

        // ---- Helpers ----

        private static CaptureArtifactVerificationBufferPool MakePool()
        {
            // Deliberately far smaller than the chunk so a length-proportional
            // buffer would be visible.
            return new CaptureArtifactVerificationBufferPool(SmallBufferLength);
        }

        private static CaptureArtifactFileStore MakeStore(
            CaptureRunRootLayout layout,
            CaptureArtifactVerificationBufferPool pool)
        {
            return new CaptureArtifactFileStore(layout, pool, new SupportedNoFollowOpener());
        }

        /// <summary>
        /// Rents and immediately returns the single buffer, so the observed
        /// lease generation counts every rent the pool has served. A publisher
        /// that reserved between two probes shows up as a generation gap.
        /// </summary>
        private static long ProbeGeneration(CaptureArtifactVerificationBufferPool pool)
        {
            CaptureArtifactVerificationBufferPool.Lease lease = pool.TryRent();
            Assert.That(lease, Is.Not.Null);
            long generation = lease.Generation;
            pool.Return(lease);
            return generation;
        }

        private static void AssertNoReservationSince(
            CaptureArtifactVerificationBufferPool pool,
            long generationBefore)
        {
            Assert.That(ProbeGeneration(pool), Is.EqualTo(generationBefore + 1),
                "the publisher must not reserve the verification buffer on this path.");
            Assert.That(pool.OutstandingRentCount, Is.EqualTo(0));
        }

        private static NvencRunArtifactPublicationOperation PreparePublication(Harness h)
        {
            FinalizeOnly(h);
            Assert.That(
                h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out _),
                Is.True);
            h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;
            Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
            WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted,
                "service did not publish the committed plan terminal");
            Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(out _), Is.True);
            Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                out NvencRunArtifactPublicationOperation operation), Is.True);
            return operation;
        }

        private static void WaitForServiceState(
            Harness h,
            NvencRunPublicationServiceState expected,
            string message)
        {
            SpinWait.SpinUntil(() => h.Service.State == expected, WatchdogTimeoutMs);
            Assert.That(h.Service.State, Is.EqualTo(expected), message);
        }

        private static void WaitSettled(ManualResetEventSlim settled, string message)
        {
            Assert.That(settled.Wait(WatchdogTimeoutMs), Is.True, message);
        }

        private static void FinalizeOnly(Harness h)
        {
            StopFinalizedBackend(h);
            Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
            Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
            Assert.That(h.TraceRecorder.TryTrigger(), Is.True);
            ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
            FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);
            Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out _), Is.True);
        }

        private static void StopFinalizedBackend(Harness h)
        {
            h.AcceptAndAppendChunk(1, ChunkLength, Seed);
            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            Assert.That(h.RunCoordinator.TryReflectCompletion(
                MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
            h.SubmitDrained = true;

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
            Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not complete the teardown");
            h.WaitForPhysicalStop("worker did not physically exit after the teardown");

            Assert.That(h.Worker.TeardownCompleted, Is.True);
            Assert.That(h.Worker.IsStopped, Is.True);

            h.SubmitWorker.Dispose();
        }

        private static ForcedDropFrameIdSet MakeForcedDropSet(Harness h)
        {
            h.DraftQueue.BeginProducerDrain();
            h.DraftQueue.CloseAfterProducerJoin();
            TerminalIntentOwnershipSnapshot snapshot = h.DraftQueue.CreateOwnershipSnapshot(0);
            return h.DraftRegistry.ForceDropPendingForFreeze(h.DraftQueue, snapshot);
        }

        private static FreezeTerminalCheckpoint MakeCheckpoint(Harness h)
        {
            return new FreezeTerminalCheckpoint(
                1000, 1, 1, Thread.CurrentThread.ManagedThreadId, h.Context.TestRunId);
        }

        private static CaptureFrameCompletion MakeCompletion(
            long captureFrameId,
            CaptureFrameCompletionStatus status)
        {
            CaptureFrameWorkToken token = new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, captureFrameId);
            ExceptionDispatchInfo failure = status == CaptureFrameCompletionStatus.Failed
                ? ExceptionDispatchInfo.Capture(new InvalidOperationException("completion failed"))
                : null;
            return new CaptureFrameCompletion(token, captureFrameId, status, true, 0, failure);
        }

        private static CaptureRunInitializationSessionIssue MakeIssue()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true) { Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true) { Tag = "second" };
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            CaptureRunInitializationSessionOwnershipLease owner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);
        }

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationDocumentSet documents =
                CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator =
                new CaptureRunInitializationExecutionCoordinator(new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(batch);
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        // ---- Fakes ----

        private sealed class TrackingFileSystem : INvencRunArtifactPublicationFileSystem
        {
            private int _callCount;

            internal bool Supported = true;
            internal bool Result = true;
            internal Exception ExceptionToThrow;

            internal CaptureRunRootLayout LastRootLayout;
            internal CaptureArtifactDescriptor LastDescriptor;
            internal byte[] LastBuffer;

            internal readonly System.Collections.Generic.List<byte[]> ObservedBuffers =
                new System.Collections.Generic.List<byte[]>();

            internal int CallCount => Volatile.Read(ref _callCount);

            public bool IsSupported => Supported;

            public bool TryPublishFresh(
                CaptureRunRootLayout rootLayout,
                CaptureArtifactDescriptor descriptor,
                byte[] verificationBuffer)
            {
                Interlocked.Increment(ref _callCount);

                LastRootLayout = rootLayout;
                LastDescriptor = descriptor;
                LastBuffer = verificationBuffer;
                ObservedBuffers.Add(verificationBuffer);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return Result;
            }
        }

        private sealed class SupportedNoFollowOpener : ICaptureArtifactNoFollowOpener
        {
            public bool IsSupported => true;

            public CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath)
            {
                // The Fresh publisher never routes through the store's general
                // verification path, so reaching this opener is a contract
                // violation rather than a normal outcome.
                throw new NotSupportedException("The Fresh publisher must not use the store's opener.");
            }
        }

        private sealed class FakeCommitter : INvencRunPublicationPlanCommitter
        {
            private int _callCount;
            internal NvencRunPublicationPlanCommitStatus Status = NvencRunPublicationPlanCommitStatus.Committed;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunPublicationPlanCommitAttemptResult Commit(
                NvencRunPublicationPlanCommitOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (Status == NvencRunPublicationPlanCommitStatus.FailedBeforeRename)
                {
                    return NvencRunPublicationPlanCommitAttemptResult.FailedBeforeRename(this, operation);
                }

                if (Status == NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown)
                {
                    return NvencRunPublicationPlanCommitAttemptResult.CommitOutcomeUnknown(this, operation);
                }

                return NvencRunPublicationPlanCommitAttemptResult.Committed(this, operation);
            }
        }

        private sealed class FakePublisher : INvencRunArtifactPublisher
        {
            public NvencRunArtifactPublicationAttemptResult Publish(
                NvencRunArtifactPublicationOperation operation)
            {
                return NvencRunArtifactPublicationAttemptResult.Published(this, operation);
            }
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            public FakeHandle(string lockPath, bool isCreated)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public string Tag { get; set; }

            public void Dispose()
            {
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
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

        private sealed class FakeWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength, Hash64);
                return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
            }
        }

        private sealed class PatternSource : INvencOutputBitstreamSource
        {
            private readonly int _length;
            private readonly byte _seed;

            internal PatternSource(int length, byte seed)
            {
                _length = length;
                _seed = seed;
            }

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                for (int i = 0; i < _length; i++)
                {
                    destination[i] = (byte)(_seed + i);
                }

                validLength = _length;
                return true;
            }
        }

        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                validLength = 1024;
                return true;
            }
        }

        private sealed class FakeSourceReadCompletedSource : INvencSourceReadCompletedSource
        {
            public bool TryGetEvidence(in NvencSubmissionRecord record, out NvencSourceReadCompletedEvidence evidence)
            {
                evidence = default;
                return false;
            }
        }

        private sealed class FakeSubmitter : INvencEncodePictureSubmitter
        {
            public bool TrySubmit(in NvencEncodePictureSubmitOperation operation)
            {
                return true;
            }
        }

        private sealed class FakeTeardown : INvencOutputWorkerTeardown
        {
            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                return NvencOutputWorkerTeardownReceipt.Issue(this);
            }
        }

        private sealed class FakeMainThreadTeardown : INvencMainThreadTextureTeardown
        {
            internal NvencRunChunkContext BoundContext;

            public bool IsBoundTo(NvencRunChunkContext context)
            {
                return BoundContext != null && context != null && ReferenceEquals(BoundContext, context);
            }

            public NvencMainThreadTextureTeardownReceipt TearDown()
            {
                return new NvencMainThreadTextureTeardownReceipt(this, BoundContext);
            }
        }

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeWriter Writer;
            internal NvencRunChunkSink Sink;
            internal NvencRunChunkFinalizationCoordinator FinalizationCoordinator;
            internal NvencRunChunkContext Context;
            internal NvencRunLocalRegistrySlot Slot;

            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCredits;
            internal NvencFrameCompletionCreditPool FrameCompletionCredits;
            internal FakeOutputSource Source;
            internal NvencSubmittedOutputCollector Collector;
            internal NvencFailedBeforeSubmitReleaseCoordinator ReleaseCoordinator;
            internal NvencSubmittedOutputAbandonRecoveryCoordinator RecoveryCoordinator;
            internal NvencFrameCompletionBoundary Boundary;
            internal NvencFixedSpscQueue<NvencSubmitToOutputRecord> OutputQueue;
            internal NvencOrderedOutputProcessor Processor;
            internal NvencOrderedOutputWorkerService Worker;
            internal FakeTeardown Teardown;
            internal FakeMainThreadTeardown MainThreadTeardown;

            internal NvencGpuConversionSyncPool SubmitSyncSlots;
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmitSubmissionQueue;
            internal NvencSourceResourceReleaseCoordinator SubmitReleaseCoordinator;
            internal NvencOrderedSubmitProcessor SubmitProcessor;
            internal NvencOrderedSubmitWorkerService SubmitWorker;

            internal NvencCaptureRunCoordinator RunCoordinator;
            internal NvencCaptureBackendJoinCoordinator BackendJoin;
            internal ManualResetEventSlim SettledEvent;

            internal FakeCommitter Committer;
            internal NvencRunPublicationService Service;

            internal CaptureRunInitializationSessionIssue SessionIssue;
            internal TraceLogger TraceLogger;
            internal TraceFlightRecorder TraceRecorder;
            internal CaptureFrameFreezeTerminalCoordinator FreezeTerminalCoordinator;
            internal CaptureFrameDraftRegistry DraftRegistry;
            internal CaptureFrameDraftTerminalIntentQueue DraftQueue;
            internal NvencTraceFreezeCoordinator TraceFreeze;

            private readonly Action _settledHandler;

            internal bool SubmitDrained
            {
                set => SetField(SubmitWorker, "_drainCompleted", value);
            }

            internal Harness()
            {
                State = new NvencCaptureProcessState();

                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new FakeWriter();
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                FinalizationCoordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                SessionIssue = MakeIssue();
                Context = new NvencRunChunkContext(SessionIssue, Sink, FinalizationCoordinator, "chunk/0");
                Slot = new NvencRunLocalRegistrySlot(Context);

                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                Source = new FakeOutputSource();
                Collector = new NvencSubmittedOutputCollector(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer, Source);
                ReleaseCoordinator = new NvencFailedBeforeSubmitReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits);
                RecoveryCoordinator = new NvencSubmittedOutputAbandonRecoveryCoordinator(
                    State, Collector, SampleSlots, Buffer);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
                OutputQueue = new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
                Processor = new NvencOrderedOutputProcessor(
                    State, OutputQueue, Collector, Sink, ReleaseCoordinator, RecoveryCoordinator, Boundary);

                SubmitSyncSlots = new NvencGpuConversionSyncPool(State);
                SubmitSubmissionQueue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                SubmitReleaseCoordinator = new NvencSourceResourceReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitSyncSlots,
                    SubmitToOutputCredits, FrameCompletionCredits,
                    new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
                SubmitProcessor = new NvencOrderedSubmitProcessor(
                    State, SubmitSubmissionQueue, OutputQueue,
                    WorkSlots, SampleSlots, SubmitReleaseCoordinator, new FakeSubmitter());
                SubmitWorker = new NvencOrderedSubmitWorkerService(State, SubmitProcessor);

                Teardown = new FakeTeardown();
                Worker = new NvencOrderedOutputWorkerService(State, Processor, Context, SubmitWorker, Teardown);

                MainThreadTeardown = new FakeMainThreadTeardown { BoundContext = Context };
                BackendJoin = new NvencCaptureBackendJoinCoordinator(
                    State, SubmitWorker, Worker, Context,
                    WorkSlots, SampleSlots, SubmitSyncSlots, SubmitToOutputCredits, FrameCompletionCredits,
                    Buffer, Processor, MainThreadTeardown);

                TraceLogger = new TraceLogger(16, Context.TestRunId);
                TraceRecorder = new TraceFlightRecorder(TraceLogger, 16, 2);
                TraceRunContext traceRunContext = new TraceRunContext(
                    Context.TestRunId, 1000, "build-1", "6000.3.22f1", Hash64, "scene-1", 12345, 0.02, 3, "High", 1,
                    new Vector3(0f, -4.9f, 0f));
                CaptureDraftRunContext draftRun = new CaptureDraftRunContext(traceRunContext, 100, 5);
                CaptureTraceProfile traceProfile = new CaptureTraceProfile(5, 4096, 2, 4);
                DraftRegistry = new CaptureFrameDraftRegistry(draftRun, traceProfile);
                DraftQueue = new CaptureFrameDraftTerminalIntentQueue(DraftRegistry, traceProfile);
                FreezeTerminalTraceBufferBuilder freezeBuilder = new FreezeTerminalTraceBufferBuilder(DraftRegistry);
                FreezeTerminalCoordinator = new CaptureFrameFreezeTerminalCoordinator(TraceRecorder, freezeBuilder);
                TraceFreeze = new NvencTraceFreezeCoordinator(
                    TraceLogger, TraceRecorder, FreezeTerminalCoordinator, Context, SessionIssue);

                Committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionCoordinator commitCoordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(Committer);
                NvencRunArtifactPublicationExecutionCoordinator artifactCoordinator =
                    new NvencRunArtifactPublicationExecutionCoordinator(new FakePublisher());
                Service = new NvencRunPublicationService(State, commitCoordinator, artifactCoordinator);

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin,
                    SessionIssue, TraceFreeze, Service);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            internal static Harness Create()
            {
                Harness h = new Harness();

                h.SettledEvent.Reset();
                h.Worker.Notify();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True, "worker did not settle initially");

                return h;
            }

            internal void AcceptAndAppendChunk(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                CaptureFrameWorkToken token = MakeToken(frameId);
                Assert.That(Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
                Assert.That(Buffer.TryCopyCompletedOutput(write, default, new PatternSource(length, seed), out _),
                    Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
                Assert.That(Buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease lease), Is.True);
                Assert.That(Sink.TryAppend(token, lease, out _), Is.True);
            }

            internal void WaitForPhysicalStop(string message)
            {
                FieldInfo field = typeof(NvencOrderedOutputWorkerService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread workerThread = (Thread)field?.GetValue(Worker);
                if (workerThread != null)
                {
                    Assert.That(workerThread.Join(WatchdogTimeoutMs), Is.True, message);
                }
            }

            public void Dispose()
            {
                if (!Worker.IsStopped)
                {
                    State.TryPoison();
                    Worker.Notify();
                }

                WaitForPhysicalStop("worker thread did not physically exit during teardown");

                Worker.Dispose();
                Worker.Settled -= _settledHandler;
                SettledEvent.Dispose();

                if (!Service.IsStopped)
                {
                    if (!Service.TryStopWithoutRequest())
                    {
                        State.TryPoison();
                        Service.Notify();
                    }
                }

                FieldInfo serviceField = typeof(NvencRunPublicationService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread serviceThread = (Thread)serviceField?.GetValue(Service);
                if (serviceThread != null)
                {
                    Assert.That(serviceThread.Join(WatchdogTimeoutMs), Is.True,
                        "publication service worker did not physically exit during teardown");
                }

                Service.Dispose();
            }
        }
    }
}
