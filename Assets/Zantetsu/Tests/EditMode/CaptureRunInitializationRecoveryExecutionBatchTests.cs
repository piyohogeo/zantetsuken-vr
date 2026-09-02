using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationRecoveryExecutionBatchTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerKind InitKind => CaptureRunMarkerKind.Initialization;

        private static CaptureRunMarkerKind ReadyKind => CaptureRunMarkerKind.Ready;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _owners.Count - 1; i >= 0; i--)
            {
                _owners[i].Dispose();
            }

            _owners.Clear();
        }

        // ---- Helpers ----

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "D:\\final" : "/final",
                testRunId);
        }

        private static CaptureRunMarkerBinding MakeBinding(CaptureRunRootLayout layout)
        {
            return CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);
        }

        private static CaptureRunInitializationRootObservation MakeObservation(
            CaptureRunRootRole role,
            bool rootExists,
            CaptureRunMarkerObservationStatus initStatus,
            CaptureRunInitializationMarker initMarker,
            CaptureRunMarkerObservationStatus readyStatus,
            CaptureRunReadyMarker readyMarker,
            bool hasNonMarker = false,
            bool hasUnknown = false,
            bool hasInitTmp = false,
            bool hasReadyTmp = false)
        {
            return new CaptureRunInitializationRootObservation(
                role,
                rootExists,
                hasInitTmp,
                initStatus,
                initMarker,
                hasReadyTmp,
                readyStatus,
                readyMarker,
                hasNonMarker,
                hasUnknown,
                false);
        }

        private static CaptureRunInitializationRootObservation MakeAbsent(CaptureRunRootRole role)
        {
            return MakeObservation(role, false, Absent, null, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeCanonicalInit(
            CaptureRunRootRole role,
            CaptureRunInitializationMarker marker)
        {
            return MakeObservation(role, true, Canonical, marker, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(
            CaptureRunRootRole role,
            CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private CaptureRunInitializationSessionOwnershipLease MakeOwner(
            CaptureRunRootLayout layout,
            List<string> disposeLog)
        {
            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            return owner;
        }

        private CaptureRunInitializationRecoveryInspectionSnapshot MakeSnapshot(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout = null,
            List<string> disposeLog = null)
        {
            return MakeSnapshot(staging, final, layout, disposeLog, out _);
        }

        private CaptureRunInitializationRecoveryInspectionSnapshot MakeSnapshot(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeSnapshot(staging, final, layout, null, out owner);
        }

        private CaptureRunInitializationRecoveryInspectionSnapshot MakeSnapshot(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout,
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            layout = layout ?? MakeLayout();
            owner = MakeOwner(layout, disposeLog);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            return new CaptureRunInitializationRecoveryInspectionSnapshot(new FakeInspector(), operation, staging, final);
        }

        private CaptureRunInitializationRecoveryActionPlan BuildPlan(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout = null)
        {
            return CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(MakeSnapshot(staging, final, layout)));
        }

        private static CaptureRunInitializationRecoveryExecutionBatch BuildBatch(
            CaptureRunInitializationRecoveryActionPlan plan)
        {
            return CaptureRunInitializationRecoveryExecutionBatchBuilder.Build(plan);
        }

        private static CaptureRunMarkerPathSet ForgePathSet(CaptureRunMarkerPathSet source, string fieldName, string corruptedValue)
        {
            CaptureRunMarkerPathSet forged = (CaptureRunMarkerPathSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerPathSet));
            SetField(forged, "_rootLayout", source.RootLayout);
            SetField(forged, "_stagingInitializationTemporaryPath", source.StagingInitializationTemporaryPath);
            SetField(forged, "_stagingInitializationPath", source.StagingInitializationPath);
            SetField(forged, "_stagingReadyTemporaryPath", source.StagingReadyTemporaryPath);
            SetField(forged, "_stagingReadyPath", source.StagingReadyPath);
            SetField(forged, "_finalInitializationTemporaryPath", source.FinalInitializationTemporaryPath);
            SetField(forged, "_finalInitializationPath", source.FinalInitializationPath);
            SetField(forged, "_finalReadyTemporaryPath", source.FinalReadyTemporaryPath);
            SetField(forged, "_finalReadyPath", source.FinalReadyPath);
            SetField(forged, fieldName, corruptedValue);
            return forged;
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationRecoveryExecutionBatchTests).Assembly.Location);
            while (dir != null)
            {
                string candidate = Path.Combine(dir, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null)
                {
                    break;
                }

                dir = parent.FullName;
            }

            Assert.Fail("Source file not found: " + relativePath);
            return null;
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            private readonly List<string> _disposeLog;

            public FakeHandle(string lockPath, bool isCreated = true, List<string> disposeLog = null)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
                _disposeLog = disposeLog;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public void Dispose()
            {
                _disposeLog?.Add(LockPath);
            }
        }

        private sealed class FakeInspector : ICaptureRunInitializationRecoveryInspector
        {
            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                throw new NotSupportedException("The execution batch must never call the inspector back.");
            }
        }

        // ---- 1:1 order per disposition ----

        [Test]
        public void Batch_AllDispositions_StepsMatchPlanOrder()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan[] plans =
            {
                BuildPlan(MakeAbsent(Staging), MakeAbsent(Final), layout),                                                       // StartFresh
                BuildPlan(MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final), layout), // Cleanup
                BuildPlan(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout),                 // CompleteMissingPeer
                BuildPlan(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeCanonicalInit(Final, binding.FinalInitialization), layout), // CompleteReadyMarkers
                BuildPlan(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout),                     // AlreadyInitialized
                BuildPlan(MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true), MakeFullyCanonical(Final, binding), layout), // Publication
                BuildPlan(MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), layout) // Collision
            };

            foreach (CaptureRunInitializationRecoveryActionPlan plan in plans)
            {
                CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(plan);

                Assert.That(batch.Count, Is.EqualTo(plan.Count));
                for (int i = 0; i < plan.Count; i++)
                {
                    CaptureRunInitializationRecoveryPreparedStep prepared = batch.GetPreparedStep(i);
                    Assert.That(prepared.Action, Is.EqualTo(plan.GetStep(i).Action), "Step " + i + " action mismatch.");
                    Assert.That(prepared.RootRole, Is.EqualTo(plan.GetStep(i).RootRole), "Step " + i + " role mismatch.");
                    Assert.That(prepared.MarkerKind, Is.EqualTo(plan.GetStep(i).MarkerKind), "Step " + i + " kind mismatch.");
                    Assert.That(prepared.StepIndex, Is.EqualTo(i));
                }

                Assert.That(batch.IsValid, Is.True);
            }
        }

        // ---- Exclusive operation per action ----

        [Test]
        public void PreparedStep_ExclusiveOperationPerAction()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            // Cleanup plan: cleanup + routing
            CaptureRunInitializationRecoveryExecutionBatch cleanupBatch = BuildBatch(
                BuildPlan(MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final), layout));
            foreach (int i in Enumerable.Range(0, cleanupBatch.Count))
            {
                CaptureRunInitializationRecoveryPreparedStep prepared = cleanupBatch.GetPreparedStep(i);
                if (prepared.Action == CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary
                    || prepared.Action == CaptureRunInitializationRecoveryAction.RemoveEmptyRoot)
                {
                    Assert.That(prepared.CleanupOperation, Is.Not.Null);
                    Assert.That(prepared.ProvisionOperation, Is.Null);
                    Assert.That(prepared.MarkerWriteOperation, Is.Null);
                    Assert.That(prepared.IsRouting, Is.False);
                }
                else
                {
                    Assert.That(prepared.CleanupOperation, Is.Null);
                    Assert.That(prepared.ProvisionOperation, Is.Null);
                    Assert.That(prepared.MarkerWriteOperation, Is.Null);
                    Assert.That(prepared.IsRouting, Is.True);
                }
            }

            // CompleteMissingPeer: provision + write
            CaptureRunInitializationRecoveryExecutionBatch peerBatch = BuildBatch(
                BuildPlan(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout));
            foreach (int i in Enumerable.Range(0, peerBatch.Count))
            {
                CaptureRunInitializationRecoveryPreparedStep prepared = peerBatch.GetPreparedStep(i);
                if (prepared.Action == CaptureRunInitializationRecoveryAction.ProvisionRoot)
                {
                    Assert.That(prepared.ProvisionOperation, Is.Not.Null);
                    Assert.That(prepared.CleanupOperation, Is.Null);
                    Assert.That(prepared.MarkerWriteOperation, Is.Null);
                }
                else if (prepared.Action == CaptureRunInitializationRecoveryAction.WriteMarker)
                {
                    Assert.That(prepared.MarkerWriteOperation, Is.Not.Null);
                    Assert.That(prepared.CleanupOperation, Is.Null);
                    Assert.That(prepared.ProvisionOperation, Is.Null);
                }
            }
        }

        // ---- Ordering ----

        [Test]
        public void Batch_CompleteMissingPeer_Order()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                BuildPlan(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout));

            Assert.That(batch.Count, Is.EqualTo(4));
            Assert.That(batch.GetPreparedStep(0).Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.ProvisionRoot));
            Assert.That(batch.GetPreparedStep(1).Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.WriteMarker));
            Assert.That(batch.GetPreparedStep(1).RootRole, Is.EqualTo(Final));
            Assert.That(batch.GetPreparedStep(1).MarkerKind, Is.EqualTo(InitKind));
            Assert.That(batch.GetPreparedStep(2).Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.WriteMarker));
            Assert.That(batch.GetPreparedStep(2).RootRole, Is.EqualTo(Staging));
            Assert.That(batch.GetPreparedStep(2).MarkerKind, Is.EqualTo(ReadyKind));
            Assert.That(batch.GetPreparedStep(3).Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.WriteMarker));
            Assert.That(batch.GetPreparedStep(3).RootRole, Is.EqualTo(Final));
            Assert.That(batch.GetPreparedStep(3).MarkerKind, Is.EqualTo(ReadyKind));
        }

        [Test]
        public void Batch_CompleteReadyMarkers_Order()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            // with a tmp deletion prefix
            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                BuildPlan(
                    MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Absent, null, hasInitTmp: true),
                    MakeCanonicalInit(Final, binding.FinalInitialization),
                    layout));

            Assert.That(batch.Count, Is.EqualTo(3));
            Assert.That(batch.GetPreparedStep(0).Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary));
            Assert.That(batch.GetPreparedStep(1).Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.WriteMarker));
            Assert.That(batch.GetPreparedStep(1).RootRole, Is.EqualTo(Staging));
            Assert.That(batch.GetPreparedStep(1).MarkerKind, Is.EqualTo(ReadyKind));
            Assert.That(batch.GetPreparedStep(2).Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.WriteMarker));
            Assert.That(batch.GetPreparedStep(2).RootRole, Is.EqualTo(Final));
            Assert.That(batch.GetPreparedStep(2).MarkerKind, Is.EqualTo(ReadyKind));
        }

        [Test]
        public void Batch_RoutingDispositions_SingleRoutingStep()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan[] plans =
            {
                BuildPlan(MakeAbsent(Staging), MakeAbsent(Final), layout),
                BuildPlan(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout),
                BuildPlan(MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true), MakeFullyCanonical(Final, binding), layout),
                BuildPlan(MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), layout)
            };

            foreach (CaptureRunInitializationRecoveryActionPlan plan in plans)
            {
                CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(plan);
                Assert.That(batch.Count, Is.EqualTo(1));
                CaptureRunInitializationRecoveryPreparedStep prepared = batch.GetPreparedStep(0);
                Assert.That(prepared.IsRouting, Is.True);
                Assert.That(prepared.CleanupOperation, Is.Null);
                Assert.That(prepared.ProvisionOperation, Is.Null);
                Assert.That(prepared.MarkerWriteOperation, Is.Null);
            }
        }

        // ---- Write operation materialization ----

        [Test]
        public void Batch_WriteOperation_FourCombos_BytesExact()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryExecutionBatch batchA = BuildBatch(
                BuildPlan(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout));

            AssertWrite(batchA.GetPreparedStep(1), Final, InitKind,
                binding.FinalInitialization, CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.FinalInitialization));
            AssertWrite(batchA.GetPreparedStep(2), Staging, ReadyKind,
                binding.StagingReady, CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady));
            AssertWrite(batchA.GetPreparedStep(3), Final, ReadyKind,
                binding.FinalReady, CaptureRunReadyMarkerCodec.SerializeCanonical(binding.FinalReady));

            CaptureRunInitializationRecoveryExecutionBatch batchB = BuildBatch(
                BuildPlan(MakeAbsent(Staging), MakeCanonicalInit(Final, binding.FinalInitialization), layout));

            AssertWrite(batchB.GetPreparedStep(1), Staging, InitKind,
                binding.StagingInitialization, CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.StagingInitialization));
        }

        [Test]
        public void Batch_SourceInitAndCanonicalReady_NeverMaterialized()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            // CompleteMissingPeer (staging source): no write operation for staging init
            CaptureRunInitializationRecoveryExecutionBatch peerBatch = BuildBatch(
                BuildPlan(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout));
            foreach (int i in Enumerable.Range(0, peerBatch.Count))
            {
                CaptureRunInitializationRecoveryPreparedStep prepared = peerBatch.GetPreparedStep(i);
                if (prepared.Action == CaptureRunInitializationRecoveryAction.WriteMarker
                    && prepared.RootRole == Staging
                    && prepared.MarkerKind == InitKind)
                {
                    Assert.Fail("Source initialization must never be materialized.");
                }
            }

            // CompleteReadyMarkers (staging ready present): no write operation for staging ready
            CaptureRunInitializationRecoveryExecutionBatch readyBatch = BuildBatch(
                BuildPlan(MakeFullyCanonical(Staging, binding), MakeCanonicalInit(Final, binding.FinalInitialization), layout));
            foreach (int i in Enumerable.Range(0, readyBatch.Count))
            {
                CaptureRunInitializationRecoveryPreparedStep prepared = readyBatch.GetPreparedStep(i);
                if (prepared.Action == CaptureRunInitializationRecoveryAction.WriteMarker
                    && prepared.RootRole == Staging
                    && prepared.MarkerKind == ReadyKind)
                {
                    Assert.Fail("Canonical staging ready must never be materialized.");
                }
            }
        }

        // ---- Rejections ----

        [Test]
        public void Batch_NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunInitializationRecoveryExecutionBatchBuilder.Build(null));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Batch_InvalidPlan_Rejected()
        {
            CaptureRunInitializationRecoveryActionPlan plan = (CaptureRunInitializationRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryActionPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationRecoveryExecutionBatchBuilder.Build(plan));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Batch_GetPreparedStep_OutOfRange_Rejected()
        {
            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(BuildPlan(MakeAbsent(Staging), MakeAbsent(Final)));

            Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetPreparedStep(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetPreparedStep(batch.Count));
        }

        [Test]
        public void PreparedStep_CorruptedPathSet_ProvisionStep_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet source = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerPathSet corrupted = ForgePathSet(source, "_stagingInitializationTemporaryPath", layout.FinalRunRoot);

            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout); // step 0 = ProvisionRoot(Final)

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryPreparedStep(plan, corrupted, 0));
            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void PreparedStep_CorruptedPathSet_RoutingSteps_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet source = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerPathSet corrupted = ForgePathSet(source, "_stagingInitializationTemporaryPath", layout.FinalRunRoot);

            CaptureRunInitializationRecoveryActionPlan[] plans =
            {
                BuildPlan(MakeAbsent(Staging), MakeAbsent(Final), layout), // StartFreshInitialization
                BuildPlan(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout), // InitializationReady
                BuildPlan(MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true), MakeFullyCanonical(Final, binding), layout), // ContinuePublicationRecovery
                BuildPlan(MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), layout) // StopRunRootCollision
            };

            foreach (CaptureRunInitializationRecoveryActionPlan plan in plans)
            {
                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => new CaptureRunInitializationRecoveryPreparedStep(plan, corrupted, 0));
                Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
            }
        }

        [Test]
        public void PreparedStep_CorruptedPathSet_Reflection_IsInvalid()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet source = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerPathSet corrupted = ForgePathSet(source, "_stagingInitializationTemporaryPath", layout.FinalRunRoot);

            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(MakeAbsent(Staging), MakeAbsent(Final), layout); // routing

            CaptureRunInitializationRecoveryPreparedStep prepared = (CaptureRunInitializationRecoveryPreparedStep)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryPreparedStep));
            SetField(prepared, "_actionPlan", plan);
            SetField(prepared, "_markerPaths", corrupted);
            SetField(prepared, "_stepIndex", 0);
            SetField(prepared, "_cleanupOperation", null);
            SetField(prepared, "_provisionOperation", null);
            SetField(prepared, "_markerWriteOperation", null);

            Assert.That(prepared.IsValid, Is.False);
        }

        // ---- PreparedStep foreign / corrupted operations ----

        [Test]
        public void PreparedStep_ForeignCleanupOperation_Invalid()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);
            CaptureRunInitializationRecoveryCleanupOperation foreign = new CaptureRunInitializationRecoveryCleanupOperation(plan, markerPaths, 0);

            CaptureRunInitializationRecoveryActionPlan otherPlan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true, hasReadyTmp: true),
                MakeAbsent(Final),
                layout);

            CaptureRunInitializationRecoveryPreparedStep prepared = (CaptureRunInitializationRecoveryPreparedStep)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryPreparedStep));
            SetField(prepared, "_actionPlan", otherPlan);
            SetField(prepared, "_markerPaths", markerPaths);
            SetField(prepared, "_stepIndex", 0);
            SetField(prepared, "_cleanupOperation", foreign);
            SetField(prepared, "_provisionOperation", null);
            SetField(prepared, "_markerWriteOperation", null);

            Assert.That(prepared.IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_ForeignProvisionOperation_Invalid()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunRootLayout otherLayout = MakeLayout(2);
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout); // step 0 = ProvisionRoot(Final)

            CaptureRunRootProvisionOperation foreign = new CaptureRunRootProvisionOperation(otherLayout, Final);

            CaptureRunInitializationRecoveryPreparedStep prepared = (CaptureRunInitializationRecoveryPreparedStep)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryPreparedStep));
            SetField(prepared, "_actionPlan", plan);
            SetField(prepared, "_markerPaths", markerPaths);
            SetField(prepared, "_stepIndex", 0);
            SetField(prepared, "_cleanupOperation", null);
            SetField(prepared, "_provisionOperation", foreign);
            SetField(prepared, "_markerWriteOperation", null);

            Assert.That(prepared.IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_CorruptedWriteOperation_Invalid()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout); // step 0 = Write(Staging, Ready)

            // corrupt bytes
            CaptureRunMarkerWriteOperation corrupted = ForgeWriteOperation(
                Staging, ReadyKind,
                markerPaths.StagingReadyTemporaryPath, markerPaths.StagingReadyPath,
                new byte[] { 1, 2, 3 });

            CaptureRunInitializationRecoveryPreparedStep prepared = (CaptureRunInitializationRecoveryPreparedStep)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryPreparedStep));
            SetField(prepared, "_actionPlan", plan);
            SetField(prepared, "_markerPaths", markerPaths);
            SetField(prepared, "_stepIndex", 0);
            SetField(prepared, "_cleanupOperation", null);
            SetField(prepared, "_provisionOperation", null);
            SetField(prepared, "_markerWriteOperation", corrupted);

            Assert.That(prepared.IsValid, Is.False);
        }

        // ---- Batch array corruption ----

        [Test]
        public void Batch_ArrayCorruption_Invalid()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout); // 2 steps

            CaptureRunInitializationRecoveryExecutionBatch good = BuildBatch(plan);
            CaptureRunInitializationRecoveryPreparedStep step0 = good.GetPreparedStep(0);
            CaptureRunInitializationRecoveryPreparedStep step1 = good.GetPreparedStep(1);

            // null array
            Assert.That(ForgeBatch(plan, markerPaths, null).IsValid, Is.False);

            // null element
            Assert.That(ForgeBatch(plan, markerPaths, new[] { null, step1 }).IsValid, Is.False);

            // missing (shorter)
            Assert.That(ForgeBatch(plan, markerPaths, new[] { step0 }).IsValid, Is.False);

            // extra (longer)
            Assert.That(ForgeBatch(plan, markerPaths, new[] { step0, step1, step0 }).IsValid, Is.False);

            // order swapped
            Assert.That(ForgeBatch(plan, markerPaths, new[] { step1, step0 }).IsValid, Is.False);
        }

        [Test]
        public void Batch_RoutingStepWithInjectedOperation_Invalid()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(MakeAbsent(Staging), MakeAbsent(Final), layout); // StartFresh, 1 routing step

            CaptureRunInitializationRecoveryCleanupOperation injected = new CaptureRunInitializationRecoveryCleanupOperation(
                BuildPlan(MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final), layout),
                markerPaths,
                0);

            CaptureRunInitializationRecoveryPreparedStep prepared = (CaptureRunInitializationRecoveryPreparedStep)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryPreparedStep));
            SetField(prepared, "_actionPlan", plan);
            SetField(prepared, "_markerPaths", markerPaths);
            SetField(prepared, "_stepIndex", 0);
            SetField(prepared, "_cleanupOperation", injected);
            SetField(prepared, "_provisionOperation", null);
            SetField(prepared, "_markerWriteOperation", null);

            Assert.That(prepared.IsValid, Is.False);
        }

        // ---- Factory IsOperationFor ----

        [Test]
        public void Factory_IsOperationFor_MatchesAndRejects()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout); // step 0 = Write(Staging, Ready)

            CaptureRunMarkerWriteOperation op = CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, 0);

            Assert.That(CaptureRunInitializationRecoveryMarkerWriteOperationFactory.IsOperationFor(plan, markerPaths, 0, op), Is.True);
            Assert.That(CaptureRunInitializationRecoveryMarkerWriteOperationFactory.IsOperationFor(plan, markerPaths, 1, op), Is.False);
            Assert.That(CaptureRunInitializationRecoveryMarkerWriteOperationFactory.IsOperationFor(plan, markerPaths, 0, null), Is.False);
        }

        // ---- Non-mutation ----

        [Test]
        public void Batch_DoesNotDisposeOwner()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout,
                disposeLog,
                out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(snapshot));

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(plan);

            Assert.That(disposeLog, Is.Empty, "The execution batch must not dispose the owner.");
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(batch.LockIdentityEvidence, Is.SameAs(snapshot.Operation.LockIdentityEvidence));
            Assert.That(batch.RootLayout, Is.SameAs(layout));
            Assert.That(batch.ExpectedBinding, Is.SameAs(plan.Decision.ExpectedBinding));
            Assert.That(batch.IsValid, Is.True);
        }

        // ---- Shape ----

        [Test]
        public void Batch_ArrayNotExposed_FieldsThreeReadonly()
        {
            Type type = typeof(CaptureRunInitializationRecoveryExecutionBatch);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            Assert.That(
                type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(p => p.PropertyType == typeof(CaptureRunInitializationRecoveryPreparedStep[])),
                Is.False,
                "The prepared-step array must not be exposed.");

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);

            Assert.That(
                type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(p => p.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
                              || p.PropertyType == typeof(CaptureRunLockLease)),
                Is.False,
                "The batch must not expose the ownership lease or raw lock lease.");
        }

        [Test]
        public void PreparedStep_Shape_SixReadonlyFields()
        {
            Type type = typeof(CaptureRunInitializationRecoveryPreparedStep);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(6));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Builder_Shape_NoFields()
        {
            Type type = typeof(CaptureRunInitializationRecoveryExecutionBatchBuilder);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance), Is.Empty);
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies_NoBackendCalls()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryPreparedStep.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryExecutionBatch.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryExecutionBatchBuilder.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("Stream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("Trace"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("System.IO"));

                Assert.That(source, Does.Not.Contain("ICaptureRunInitializationRecoveryCleanupBackend"));
                Assert.That(source, Does.Not.Contain("ICaptureRunRootProvisioner"));
                Assert.That(source, Does.Not.Contain("ICaptureRunMarkerAtomicWriter"));
            }

            string batchSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryExecutionBatch.cs"));
            Assert.That(batchSource, Does.Not.Contain("List<"));
            Assert.That(batchSource, Does.Not.Contain("ToArray"));
            Assert.That(batchSource, Does.Not.Contain("Array.Copy"));
        }

        // ---- Assertion helpers ----

        private static CaptureRunMarkerWriteOperation ForgeWriteOperation(
            CaptureRunRootRole rootRole,
            CaptureRunMarkerKind markerKind,
            string temporaryPath,
            string finalPath,
            byte[] canonicalBytes)
        {
            CaptureRunMarkerWriteOperation op = (CaptureRunMarkerWriteOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerWriteOperation));
            SetField(op, "_rootRole", rootRole);
            SetField(op, "_markerKind", markerKind);
            SetField(op, "_temporaryPath", temporaryPath);
            SetField(op, "_finalPath", finalPath);
            SetField(op, "_canonicalBytes", canonicalBytes);
            return op;
        }

        private static CaptureRunInitializationRecoveryExecutionBatch ForgeBatch(
            CaptureRunInitializationRecoveryActionPlan actionPlan,
            CaptureRunMarkerPathSet markerPaths,
            CaptureRunInitializationRecoveryPreparedStep[] steps)
        {
            CaptureRunInitializationRecoveryExecutionBatch batch = (CaptureRunInitializationRecoveryExecutionBatch)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryExecutionBatch));
            SetField(batch, "_actionPlan", actionPlan);
            SetField(batch, "_markerPaths", markerPaths);
            SetField(batch, "_steps", steps);
            return batch;
        }

        private static void AssertWrite(
            CaptureRunInitializationRecoveryPreparedStep prepared,
            CaptureRunRootRole role,
            CaptureRunMarkerKind kind,
            object marker,
            byte[] expectedBytes)
        {
            Assert.That(prepared.Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.WriteMarker));
            Assert.That(prepared.RootRole, Is.EqualTo(role));
            Assert.That(prepared.MarkerKind, Is.EqualTo(kind));
            Assert.That(prepared.MarkerWriteOperation, Is.Not.Null);
            Assert.That(prepared.MarkerWriteOperation.RootRole, Is.EqualTo(role));
            Assert.That(prepared.MarkerWriteOperation.MarkerKind, Is.EqualTo(kind));
            Assert.That(prepared.MarkerWriteOperation.GetCanonicalBytes(), Is.EqualTo(expectedBytes));
            Assert.That(prepared.IsValid, Is.True);
        }
    }
}
