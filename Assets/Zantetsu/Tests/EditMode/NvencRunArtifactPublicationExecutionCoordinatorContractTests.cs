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
    /// Contract tests for the Phase 0.11 Fresh NVENC Run artifact publication
    /// synchronous boundary: the publisher interface, the immutable receipt,
    /// the readonly attempt result, and the single-call execution coordinator.
    /// Uses the committed Run pipeline to mint a valid publication operation;
    /// no real GPU, NVENC, filesystem, sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunArtifactPublicationExecutionCoordinatorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Execute ----

        [Test]
        public void Constructor_NullPublisher_Throws()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunArtifactPublicationExecutionCoordinator(null));
            Assert.That(ex.ParamName, Is.EqualTo("publisher"));
        }

        [Test]
        public void Execute_NullOrInvalidOperation_PublisherNotContacted()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                FakePublisher publisher = new FakePublisher();
                NvencRunArtifactPublicationExecutionCoordinator coordinator =
                    new NvencRunArtifactPublicationExecutionCoordinator(publisher);

                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(
                    () => coordinator.Execute(null));
                Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
                Assert.That(publisher.CallCount, Is.EqualTo(0));

                // Break the exact correlation so the operation is invalid:
                // rejected before the publisher is contacted.
                SetField(h.RunCoordinator, "_disposition", NvencRunEvidenceDisposition.None);
                Assert.That(operation.IsValid, Is.False);

                ArgumentException invalidEx = Assert.Throws<ArgumentException>(
                    () => coordinator.Execute(operation));
                Assert.That(invalidEx.ParamName, Is.EqualTo("operation"));
                Assert.That(publisher.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Execute_Published_ExactPublisherOperationReceiptCorrelation()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                FakePublisher publisher = new FakePublisher();
                NvencRunArtifactPublicationAttemptResult result =
                    new NvencRunArtifactPublicationExecutionCoordinator(publisher).Execute(operation);

                Assert.That(publisher.CallCount, Is.EqualTo(1));
                Assert.That(result.IsPublished, Is.True);
                Assert.That(result.IsFailed, Is.False);
                Assert.That(result.IsNone, Is.False);
                Assert.That(result.Status, Is.EqualTo(NvencRunArtifactPublicationStatus.Published));
                Assert.That(ReferenceEquals(result.Publisher, publisher), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsIssuedFor(publisher, operation), Is.True);
                Assert.That(ReferenceEquals(result.Receipt.Publisher, publisher), Is.True);
                Assert.That(ReferenceEquals(result.Receipt.Operation, operation), Is.True);
            }
        }

        [Test]
        public void Execute_Failed_NoReceipt()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                FakePublisher publisher = new FakePublisher
                {
                    Status = NvencRunArtifactPublicationStatus.Failed,
                };
                NvencRunArtifactPublicationAttemptResult result =
                    new NvencRunArtifactPublicationExecutionCoordinator(publisher).Execute(operation);

                Assert.That(publisher.CallCount, Is.EqualTo(1));
                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.IsPublished, Is.False);
                Assert.That(result.IsNone, Is.False);
                Assert.That(result.Status, Is.EqualTo(NvencRunArtifactPublicationStatus.Failed));
                Assert.That(result.Receipt, Is.Null);
                Assert.That(ReferenceEquals(result.Publisher, publisher), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
            }
        }

        [Test]
        public void Execute_PublisherException_PropagatesSameInstance_NoRetry()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                InvalidOperationException boom = new InvalidOperationException("boom");
                FakePublisher publisher = new FakePublisher { ExceptionToThrow = boom };
                NvencRunArtifactPublicationExecutionCoordinator coordinator =
                    new NvencRunArtifactPublicationExecutionCoordinator(publisher);

                InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                    () => coordinator.Execute(operation));
                Assert.That(ReferenceEquals(thrown, boom), Is.True);
                Assert.That(publisher.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Execute_DefaultResult_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                FakePublisher defaultPublisher = new FakePublisher
                {
                    UseOverride = true,
                    OverrideResult = default,
                };
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunArtifactPublicationExecutionCoordinator(defaultPublisher).Execute(operation));
                Assert.That(defaultPublisher.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Execute_ForeignPublisherOrOperationResult_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                // A foreign publisher's attempt result.
                FakePublisher foreignPublisher = new FakePublisher();
                NvencRunArtifactPublicationAttemptResult foreignAttempt =
                    NvencRunArtifactPublicationAttemptResult.Published(foreignPublisher, operation);
                FakePublisher lyingPublisher = new FakePublisher
                {
                    UseOverride = true,
                    OverrideResult = foreignAttempt,
                };
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunArtifactPublicationExecutionCoordinator(lyingPublisher).Execute(operation));
                Assert.That(lyingPublisher.CallCount, Is.EqualTo(1));

                // A different operation's attempt result.
                using (Harness h2 = Harness.Create())
                {
                    NvencRunArtifactPublicationOperation otherOperation = PreparePublication(h2);
                    NvencRunArtifactPublicationAttemptResult otherAttempt =
                        NvencRunArtifactPublicationAttemptResult.Published(new FakePublisher(), otherOperation);
                    FakePublisher lyingPublisher2 = new FakePublisher
                    {
                        UseOverride = true,
                        OverrideResult = otherAttempt,
                    };
                    Assert.Throws<InvalidOperationException>(() =>
                        new NvencRunArtifactPublicationExecutionCoordinator(lyingPublisher2).Execute(operation));
                    Assert.That(lyingPublisher2.CallCount, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void Execute_PublishedWithoutReceipt_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                FakePublisher publisher = new FakePublisher();
                publisher.UseOverride = true;
                publisher.OverrideResult = MakeAttempt(
                    publisher, operation, null, NvencRunArtifactPublicationStatus.Published);
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunArtifactPublicationExecutionCoordinator(publisher).Execute(operation));
            }
        }

        [Test]
        public void Execute_PublishedWithForeignReceipt_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                FakePublisher foreignPublisher = new FakePublisher();
                NvencRunArtifactPublicationReceipt foreignReceipt =
                    NvencRunArtifactPublicationReceipt.Create(foreignPublisher, operation);
                FakePublisher publisher = new FakePublisher();
                publisher.UseOverride = true;
                publisher.OverrideResult = MakeAttempt(
                    publisher, operation, foreignReceipt, NvencRunArtifactPublicationStatus.Published);
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunArtifactPublicationExecutionCoordinator(publisher).Execute(operation));
            }
        }

        [Test]
        public void Execute_FailedWithReceipt_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                FakePublisher publisher = new FakePublisher();
                NvencRunArtifactPublicationReceipt receipt =
                    NvencRunArtifactPublicationReceipt.Create(publisher, operation);
                publisher.UseOverride = true;
                publisher.OverrideResult = MakeAttempt(
                    publisher, operation, receipt, NvencRunArtifactPublicationStatus.Failed);
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunArtifactPublicationExecutionCoordinator(publisher).Execute(operation));
            }
        }

        [Test]
        public void Execute_UndefinedStatus_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                FakePublisher publisher = new FakePublisher();
                publisher.UseOverride = true;
                publisher.OverrideResult = MakeAttempt(
                    publisher, operation, null, (NvencRunArtifactPublicationStatus)99);
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunArtifactPublicationExecutionCoordinator(publisher).Execute(operation));
            }
        }

        [Test]
        public void Execute_DoesNotChangeDispositionRegistryLeaseState()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.SessionIssue.IsValid, Is.True);

                NvencRunArtifactPublicationAttemptResult result =
                    new NvencRunArtifactPublicationExecutionCoordinator(new FakePublisher()).Execute(operation);

                Assert.That(result.IsPublished, Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.SessionIssue.IsValid, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void Result_ForwardsOperationGraph_ReceiptHoldsPublisherAndOperation()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PreparePublication(h);

                FakePublisher publisher = new FakePublisher();
                NvencRunArtifactPublicationAttemptResult result =
                    new NvencRunArtifactPublicationExecutionCoordinator(publisher).Execute(operation);

                // The attempt result and the receipt hold only the exact
                // publisher and the exact operation; descriptor, plan,
                // finalization result, paths, hash, and run identity are read
                // off that operation.
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                Assert.That(ReferenceEquals(result.Receipt.Operation, operation), Is.True);

                // Result forwarding: exact reference for the held graph, value
                // for the scalar and string surface.
                Assert.That(result.PlanCommitResult, Is.SameAs(operation.PlanCommitResult));
                Assert.That(result.Plan, Is.SameAs(operation.Plan));
                Assert.That(result.FinalizationResult, Is.SameAs(operation.FinalizationResult));
                Assert.That(result.Descriptor, Is.SameAs(operation.Descriptor));
                Assert.That(result.FrameRelation, Is.SameAs(operation.FrameRelation));
                Assert.That(result.RootLayout, Is.SameAs(operation.RootLayout));
                Assert.That(result.TestRunId, Is.EqualTo(operation.TestRunId));
                Assert.That(result.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
                Assert.That(result.StagingRelativePath, Is.EqualTo(operation.StagingRelativePath));
                Assert.That(result.FinalRelativePath, Is.EqualTo(operation.FinalRelativePath));
                Assert.That(result.ExpectedByteLength, Is.EqualTo(operation.ExpectedByteLength));
                Assert.That(result.ExpectedContentHash, Is.EqualTo(operation.ExpectedContentHash));

                // The receipt names the publisher and the operation, nothing more.
                Assert.That(result.Receipt.Publisher, Is.SameAs(publisher));
                Assert.That(result.Receipt.IsIssuedFor(publisher, operation), Is.True);
            }
        }

        // ---- Shape ----

        [Test]
        public void PublisherInterface_SingleMethodSignature()
        {
            Type type = typeof(INvencRunArtifactPublisher);

            Assert.That(type.IsInterface, Is.True);
            Assert.That(type.IsPublic, Is.False);

            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(methods, Has.Length.EqualTo(1));
            Assert.That(methods[0].Name, Is.EqualTo("Publish"));
            Assert.That(methods[0].ReturnType, Is.EqualTo(typeof(NvencRunArtifactPublicationAttemptResult)));

            ParameterInfo[] parameters = methods[0].GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(NvencRunArtifactPublicationOperation)));
        }

        [Test]
        public void Status_Enum_AppendOnly()
        {
            Assert.That((int)NvencRunArtifactPublicationStatus.None, Is.EqualTo(0));
            Assert.That((int)NvencRunArtifactPublicationStatus.Published, Is.EqualTo(1));
            Assert.That((int)NvencRunArtifactPublicationStatus.Failed, Is.EqualTo(2));
        }

        [Test]
        public void Receipt_TwoReadonlyFields_SealedInternal_NotDisposable_NoPublicCtor()
        {
            Type type = typeof(NvencRunArtifactPublicationReceipt);

            Assert.That(type.IsClass, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(INvencRunArtifactPublisher),
                    typeof(NvencRunArtifactPublicationOperation),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void AttemptResult_ReadonlyStruct_FourFields_NoPublicCtor()
        {
            Type type = typeof(NvencRunArtifactPublicationAttemptResult);

            Assert.That(type.IsValueType, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(4));
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(INvencRunArtifactPublisher),
                    typeof(NvencRunArtifactPublicationOperation),
                    typeof(NvencRunArtifactPublicationReceipt),
                    typeof(NvencRunArtifactPublicationStatus),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Coordinator_SinglePublisherField_NotDisposable()
        {
            Type type = typeof(NvencRunArtifactPublicationExecutionCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(1));
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[] { typeof(INvencRunArtifactPublisher) }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Sources_NoConcreteIoThreadingNativeDependency()
        {
            string directory = RuntimeDirectory();
            string source =
                File.ReadAllText(Path.Combine(directory, "INvencRunArtifactPublisher.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunArtifactPublicationStatus.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunArtifactPublicationReceipt.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunArtifactPublicationAttemptResult.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunArtifactPublicationExecutionCoordinator.cs"));

            // Only concrete dependency indicators; harmless identifiers and
            // comments are intentionally not scanned.
            string[] forbidden =
            {
                "System.IO", "System.Threading", "DllImport",
                "Microsoft.Win32.SafeHandles", "System.Security.Cryptography",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "publication source must not depend on: " + word);
            }
        }

        // ---- Helpers ----

        private static NvencRunArtifactPublicationOperation PreparePublication(Harness h)
        {
            CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);
            Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                out NvencRunArtifactPublicationOperation operation), Is.True);
            return operation;
        }

        private static NvencRunArtifactPublicationAttemptResult MakeAttempt(
            INvencRunArtifactPublisher publisher,
            NvencRunArtifactPublicationOperation operation,
            NvencRunArtifactPublicationReceipt receipt,
            NvencRunArtifactPublicationStatus status)
        {
            ConstructorInfo ctor = typeof(NvencRunArtifactPublicationAttemptResult).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(INvencRunArtifactPublisher),
                    typeof(NvencRunArtifactPublicationOperation),
                    typeof(NvencRunArtifactPublicationReceipt),
                    typeof(NvencRunArtifactPublicationStatus),
                },
                null);
            Assert.That(ctor, Is.Not.Null, "attempt result constructor not found.");
            return (NvencRunArtifactPublicationAttemptResult)ctor.Invoke(
                new object[] { publisher, operation, receipt, status });
        }

        /// <summary>
        /// Collects the requested Run chunk terminal by confirming the real
        /// condition inside a watchdog. A settle observed after the request may
        /// be a raise that was already in flight when the request was accepted,
        /// so it is used only as a wake hint; see TerminalConvergence.
        /// </summary>
        private static NvencRunChunkTerminalOutcome CollectTerminal(Harness h, string message)
        {
            return TerminalConvergence.Collect(
                h.RunCoordinator.TryCollectTerminal, h.Worker, h.SettledEvent, WatchdogTimeoutMs, message);
        }

        private static void WaitSettled(ManualResetEventSlim settled, string message)
        {
            Assert.That(settled.Wait(WatchdogTimeoutMs), Is.True, message);
        }

        private static void StopFinalizedBackend(Harness h, bool disposeSubmitWorker = true)
        {
            h.AcceptAndAppendChunk(1, 64, Seed);
            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
            h.SubmitDrained = true;

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            CollectTerminal(h, "worker did not converge the finalize request");

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not complete the teardown");
            h.WaitForPhysicalStop("worker did not physically exit after the teardown");

            Assert.That(h.Worker.TeardownCompleted, Is.True);
            Assert.That(h.Worker.IsStopped, Is.True);

            if (disposeSubmitWorker)
            {
                h.SubmitWorker.Dispose();
            }
        }

        private static CaptureFrameCompletion MakeCompletion(
            long captureFrameId,
            CaptureFrameCompletionStatus status,
            int producedArtifactCount = 0,
            long testRunId = 1)
        {
            CaptureFrameWorkToken token = new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, testRunId, captureFrameId);
            ExceptionDispatchInfo failure = status == CaptureFrameCompletionStatus.Failed
                ? ExceptionDispatchInfo.Capture(new InvalidOperationException("completion failed"))
                : null;
            return new CaptureFrameCompletion(token, captureFrameId, status, true, producedArtifactCount, failure);
        }

        private static CaptureRunInitializationSessionIssue MakeIssue()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true) { Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true) { Tag = "second" };
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);
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
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(layout, InitId);
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

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
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

        private static NvencRunPublicationPlanCommitOperation FinalizeAndPrepareCommit(Harness h)
        {
            FinalizeOnly(h);
            Assert.That(
                h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                Is.True);
            return operation;
        }

        private static NvencRunPublicationPlanCommitExecutionResult CommitAndCollect(
            Harness h,
            NvencRunPublicationPlanCommitStatus status)
        {
            FinalizeAndPrepareCommit(h);
            h.Committer.Status = status;
            Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
            if (status == NvencRunPublicationPlanCommitStatus.Committed)
            {
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the committed plan terminal");
            }
            else
            {
                WaitForServiceStop(h, "service worker did not stop");
            }

            Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                out NvencRunPublicationPlanCommitExecutionResult result), Is.True);
            return result;
        }

        private static void WaitForServiceStop(Harness h, string message)
        {
            FieldInfo field = typeof(NvencRunPublicationService).GetField(
                "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
            Thread worker = (Thread)field?.GetValue(h.Service);
            if (worker != null)
            {
                Assert.That(worker.Join(WatchdogTimeoutMs), Is.True, message);
            }

            Assert.That(h.Service.IsStopped, Is.True, message);
        }

        private static void WaitForServiceState(
            Harness h,
            NvencRunPublicationServiceState expected,
            string message)
        {
            SpinWait.SpinUntil(() => h.Service.State == expected, WatchdogTimeoutMs);
            Assert.That(h.Service.State, Is.EqualTo(expected), message);
        }

        // ---- Fakes ----

        private sealed class FakePublisher : INvencRunArtifactPublisher
        {
            private int _callCount;
            internal NvencRunArtifactPublicationStatus Status = NvencRunArtifactPublicationStatus.Published;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunArtifactPublicationAttemptResult OverrideResult;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunArtifactPublicationAttemptResult Publish(
                NvencRunArtifactPublicationOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (UseOverride)
                {
                    return OverrideResult;
                }

                if (Status == NvencRunArtifactPublicationStatus.Failed)
                {
                    return NvencRunArtifactPublicationAttemptResult.Failed(this, operation);
                }

                return NvencRunArtifactPublicationAttemptResult.Published(this, operation);
            }
        }

        private sealed class FakeCommitter : INvencRunPublicationPlanCommitter
        {
            private int _callCount;
            internal NvencRunPublicationPlanCommitStatus Status = NvencRunPublicationPlanCommitStatus.Committed;
            internal Exception ExceptionToThrow;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunPublicationPlanCommitAttemptResult Commit(
                NvencRunPublicationPlanCommitOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

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
            private int _finalizeCount;
            internal Exception ExceptionToThrow;
            internal bool BuildReceipt = true;

            internal int CallCount => Volatile.Read(ref _finalizeCount);

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                Interlocked.Increment(ref _finalizeCount);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (!BuildReceipt)
                {
                    return null;
                }

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
            internal bool Result = true;
            internal int ResultLength = 1024;

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                validLength = ResultLength;
                return Result;
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
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                Interlocked.Increment(ref _callCount);
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

        private sealed class FakeIndexCommitter : INvencRunCaptureIndexCommitter
        {
            private int _callCount;
            internal NvencRunCaptureIndexCommitStatus Status = NvencRunCaptureIndexCommitStatus.Committed;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunCaptureIndexCommitAttemptResult OverrideResult;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureIndexCommitAttemptResult Commit(
                NvencRunCaptureIndexCommitOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                Entered?.Set();
                Release?.Wait(WatchdogTimeoutMs);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (UseOverride)
                {
                    return OverrideResult;
                }

                if (Status == NvencRunCaptureIndexCommitStatus.Failed)
                {
                    return NvencRunCaptureIndexCommitAttemptResult.Failed(this, operation);
                }

                return NvencRunCaptureIndexCommitAttemptResult.Committed(this, operation);
            }
        }

        private sealed class FakeRunCompleter : INvencRunCaptureCompleter
        {
            private int _callCount;
            internal NvencRunCaptureCompleteStatus Status = NvencRunCaptureCompleteStatus.Completed;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunCaptureCompleteAttemptResult OverrideResult;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureCompleteAttemptResult Complete(
                NvencRunCaptureCompleteOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                Entered?.Set();
                Release?.Wait(WatchdogTimeoutMs);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (UseOverride)
                {
                    return OverrideResult;
                }

                if (Status == NvencRunCaptureCompleteStatus.Failed)
                {
                    return NvencRunCaptureCompleteAttemptResult.Failed(this, operation);
                }

                return NvencRunCaptureCompleteAttemptResult.Completed(this, operation);
            }
        }

        /// <summary>
        /// Identity-only CaptureComplete cleaner: this fixture never runs a
        /// cleanup, but the Run Coordinator requires the exact cleanup
        /// Execution Coordinator its results must come from.
        /// </summary>
        private sealed class FakeCleanupCleaner : INvencRunCaptureCompleteCleaner
        {
            public NvencRunCaptureCompleteCleanupAttemptResult Clean(
                NvencRunCaptureCompleteCleanupOperation operation)
            {
                return NvencRunCaptureCompleteCleanupAttemptResult.Cleaned(this, operation);
            }
        }

        /// <summary>
        /// Identity-only Session Ownership Lease releaser: this fixture never
        /// releases a lease, but the Run Coordinator requires the exact release
        /// Execution Coordinator its receipts must come from.
        /// </summary>
        private sealed class FakeSessionOwnershipReleaser : INvencRunSessionOwnershipReleaser
        {
            public NvencRunSessionOwnershipReleaseReceipt Release(
                NvencRunSessionOwnershipReleaseOperation operation)
            {
                if (operation == null)
                {
                    throw new ArgumentNullException(nameof(operation));
                }

                if (!NvencRunSessionOwnershipReleaseAdmission.IsAdmissible(operation))
                {
                    throw new ArgumentException(
                        "The operation cannot start a release attempt.", nameof(operation));
                }

                operation.OwnershipLease.Dispose();
                return NvencRunSessionOwnershipReleaseReceipt.Create(this, operation);
            }
        }

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeWriter Writer = new FakeWriter();
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
            internal FakePublisher Publisher;
            internal FakeIndexCommitter IndexCommitter;
            internal FakeRunCompleter RunCompleter;
            internal NvencRunPublicationService Service;
            internal FakeCleanupCleaner CleanupCleaner;
            internal NvencRunCaptureCompleteCleanupExecutionCoordinator CleanupExecution;
            internal FakeSessionOwnershipReleaser Releaser;
            internal NvencRunSessionOwnershipReleaseExecutionCoordinator ReleaseExecution;

            internal CaptureRunInitializationSessionIssue SessionIssue;
            internal TraceLogger TraceLogger;
            internal TraceFlightRecorder TraceRecorder;
            internal CaptureFrameFreezeTerminalCoordinator FreezeTerminalCoordinator;
            internal CaptureFrameDraftRegistry DraftRegistry;
            internal CaptureFrameDraftTerminalIntentQueue DraftQueue;
            internal NvencTraceFreezeCoordinator TraceFreeze;

            internal FakeWriter Finalizer => Writer;

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
                Publisher = new FakePublisher();
                NvencRunArtifactPublicationExecutionCoordinator artifactCoordinator =
                    new NvencRunArtifactPublicationExecutionCoordinator(Publisher);
                IndexCommitter = new FakeIndexCommitter();
                NvencRunCaptureIndexCommitExecutionCoordinator captureIndexCoordinator =
                    new NvencRunCaptureIndexCommitExecutionCoordinator(IndexCommitter);
                RunCompleter = new FakeRunCompleter();
                NvencRunCaptureCompleteExecutionCoordinator captureCompleteCoordinator =
                    new NvencRunCaptureCompleteExecutionCoordinator(RunCompleter);
                Service = new NvencRunPublicationService(
                    State, commitCoordinator, artifactCoordinator, captureIndexCoordinator,
                    captureCompleteCoordinator);

                CleanupCleaner = new FakeCleanupCleaner();
                CleanupExecution =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(CleanupCleaner);

                Releaser = new FakeSessionOwnershipReleaser();
                ReleaseExecution =
                    new NvencRunSessionOwnershipReleaseExecutionCoordinator(Releaser);

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin, SessionIssue, TraceFreeze, Service, CleanupExecution, ReleaseExecution);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            internal static Harness Create()
            {
                Harness h = new Harness();

                // Deterministically park the worker once before returning.
                h.SettledEvent.Reset();
                h.Worker.Notify();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True, "worker did not settle initially");

                return h;
            }

            internal void AcceptAndAppendChunk(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                AppendChunk(frameId, length, seed);
            }

            internal void AppendChunk(long frameId, int length, byte seed)
            {
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

                // Stop the Publication Plan Commit Service worker if it is
                // still parked (a test that never submitted a commit).
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
