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
    public class CaptureRunInitializationRecoveryActionPlanTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunRootRole NoneRole => CaptureRunRootRole.None;

        private static CaptureRunMarkerKind NoneKind => CaptureRunMarkerKind.None;

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

        private static CaptureRunMarkerBinding MakeBinding(CaptureRunRootLayout layout, string initId = InitId)
        {
            return CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                initId,
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
            bool limitExceeded = false,
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
                limitExceeded);
        }

        private static CaptureRunInitializationRootObservation MakeAbsent(CaptureRunRootRole role)
        {
            return MakeObservation(role, false, Absent, null, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeEmpty(CaptureRunRootRole role)
        {
            return MakeObservation(role, true, Absent, null, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeCanonicalInit(
            CaptureRunRootRole role,
            CaptureRunInitializationMarker marker)
        {
            return MakeObservation(role, true, Canonical, marker, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeCanonicalReady(
            CaptureRunRootRole role,
            CaptureRunReadyMarker marker)
        {
            return MakeObservation(role, true, Absent, null, Canonical, marker);
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
            layout = layout ?? MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwner(layout, disposeLog);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            return new CaptureRunInitializationRecoveryInspectionSnapshot(new FakeInspector(), operation, staging, final);
        }

        private CaptureRunInitializationRecoveryDecision Classify(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout = null)
        {
            return CaptureRunInitializationRecoveryClassifier.Classify(MakeSnapshot(staging, final, layout));
        }

        private CaptureRunInitializationRecoveryActionPlan Build(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout = null)
        {
            return CaptureRunInitializationRecoveryActionPlanBuilder.Build(Classify(staging, final, layout));
        }

        private static CaptureRunInitializationRecoveryStep S(
            CaptureRunInitializationRecoveryAction action,
            CaptureRunRootRole role = CaptureRunRootRole.None,
            CaptureRunMarkerKind kind = CaptureRunMarkerKind.None)
        {
            return new CaptureRunInitializationRecoveryStep(action, role, kind);
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationRecoveryActionPlanTests).Assembly.Location);
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
                throw new NotSupportedException("The plan builder must never call the inspector back.");
            }
        }

        // ---- Action enum ----

        [Test]
        public void ActionEnum_Contract()
        {
            Type type = typeof(CaptureRunInitializationRecoveryAction);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));

            string[] names = Enum.GetNames(type);
            Assert.That(names, Is.EqualTo(new[]
            {
                "None",
                "DeleteMarkerTemporary",
                "RemoveEmptyRoot",
                "ProvisionRoot",
                "WriteMarker",
                "StartFreshInitialization",
                "InitializationReady",
                "ContinuePublicationRecovery",
                "StopRunRootCollision"
            }));

            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(9));
            for (int i = 0; i < 9; i++)
            {
                Assert.That((int)values.GetValue(i), Is.EqualTo(i));
            }
        }

        // ---- Step combinations ----

        [Test]
        public void Step_ValidCombinations_Accepted()
        {
            Assert.That(S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Staging, InitKind).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Staging, ReadyKind).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Final, InitKind).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Final, ReadyKind).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.WriteMarker, Staging, InitKind).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.WriteMarker, Final, ReadyKind).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, Staging).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, Final).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.ProvisionRoot, Staging).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.ProvisionRoot, Final).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.StartFreshInitialization).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.InitializationReady).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.ContinuePublicationRecovery).IsValid, Is.True);
            Assert.That(S(CaptureRunInitializationRecoveryAction.StopRunRootCollision).IsValid, Is.True);
        }

        [Test]
        public void Step_InvalidCombinations_Rejected()
        {
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, NoneRole, InitKind));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Staging, NoneKind));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.WriteMarker, NoneRole, ReadyKind));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.WriteMarker, Final, NoneKind));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, NoneRole));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, Staging, InitKind));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.ProvisionRoot, NoneRole));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.ProvisionRoot, Final, ReadyKind));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.StartFreshInitialization, Staging));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.InitializationReady, NoneRole, ReadyKind));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.ContinuePublicationRecovery, Final));
            Assert.Throws<ArgumentException>(() => S(CaptureRunInitializationRecoveryAction.StopRunRootCollision, NoneRole, InitKind));
        }

        [Test]
        public void Step_UndefinedAction_Rejected()
        {
            foreach (CaptureRunInitializationRecoveryAction action in new[]
            {
                CaptureRunInitializationRecoveryAction.None,
                (CaptureRunInitializationRecoveryAction)9,
                (CaptureRunInitializationRecoveryAction)(-1)
            })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunInitializationRecoveryStep(action, NoneRole, NoneKind));

                Assert.That(ex.ParamName, Is.EqualTo("action"));
            }
        }

        [Test]
        public void Step_IsValid_False_WhenForged()
        {
            CaptureRunInitializationRecoveryStep none = (CaptureRunInitializationRecoveryStep)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryStep));
            Assert.That(none.IsValid, Is.False);

            CaptureRunInitializationRecoveryStep bad = (CaptureRunInitializationRecoveryStep)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryStep));
            SetField(bad, "_action", CaptureRunInitializationRecoveryAction.WriteMarker);
            SetField(bad, "_rootRole", Staging);
            SetField(bad, "_markerKind", NoneKind);
            Assert.That(bad.IsValid, Is.False);
        }

        [Test]
        public void Step_Shape_SealedThreeReadonlyFields()
        {
            Type type = typeof(CaptureRunInitializationRecoveryStep);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));

            int actionFields = 0;
            int roleFields = 0;
            int kindFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunInitializationRecoveryAction)) actionFields++;
                else if (field.FieldType == typeof(CaptureRunRootRole)) roleFields++;
                else if (field.FieldType == typeof(CaptureRunMarkerKind)) kindFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(actionFields, Is.EqualTo(1));
            Assert.That(roleFields, Is.EqualTo(1));
            Assert.That(kindFields, Is.EqualTo(1));
        }

        // ---- Golden plans per disposition ----

        [Test]
        public void StartFresh_Golden()
        {
            CaptureRunInitializationRecoveryActionPlan plan = Build(MakeAbsent(Staging), MakeAbsent(Final));

            AssertPlan(plan, S(CaptureRunInitializationRecoveryAction.StartFreshInitialization));
        }

        [Test]
        public void Cleanup_StagingMissingFinalEmpty_Golden()
        {
            CaptureRunInitializationRecoveryActionPlan plan = Build(MakeAbsent(Staging), MakeEmpty(Final));

            AssertPlan(plan,
                S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, Final),
                S(CaptureRunInitializationRecoveryAction.StartFreshInitialization));
        }

        [Test]
        public void Cleanup_BothEmpty_Golden()
        {
            CaptureRunInitializationRecoveryActionPlan plan = Build(MakeEmpty(Staging), MakeEmpty(Final));

            AssertPlan(plan,
                S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, Final),
                S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, Staging),
                S(CaptureRunInitializationRecoveryAction.StartFreshInitialization));
        }

        [Test]
        public void CompleteMissingPeer_StagingSource_FinalAbsent_Golden()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout);

            AssertPlan(plan,
                S(CaptureRunInitializationRecoveryAction.ProvisionRoot, Final),
                S(CaptureRunInitializationRecoveryAction.WriteMarker, Final, InitKind),
                S(CaptureRunInitializationRecoveryAction.WriteMarker, Staging, ReadyKind),
                S(CaptureRunInitializationRecoveryAction.WriteMarker, Final, ReadyKind));
        }

        [Test]
        public void CompleteMissingPeer_FinalSource_StagingEmpty_Golden()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeEmpty(Staging), MakeCanonicalInit(Final, binding.FinalInitialization), layout);

            AssertPlan(plan,
                S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, Staging),
                S(CaptureRunInitializationRecoveryAction.ProvisionRoot, Staging),
                S(CaptureRunInitializationRecoveryAction.WriteMarker, Staging, InitKind),
                S(CaptureRunInitializationRecoveryAction.WriteMarker, Staging, ReadyKind),
                S(CaptureRunInitializationRecoveryAction.WriteMarker, Final, ReadyKind));
        }

        [Test]
        public void CompleteReadyMarkers_BothReadyAbsent_Golden()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            AssertPlan(plan,
                S(CaptureRunInitializationRecoveryAction.WriteMarker, Staging, ReadyKind),
                S(CaptureRunInitializationRecoveryAction.WriteMarker, Final, ReadyKind));
        }

        [Test]
        public void CompleteReadyMarkers_StagingReadyPresent_Golden()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeFullyCanonical(Staging, binding),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            AssertPlan(plan, S(CaptureRunInitializationRecoveryAction.WriteMarker, Final, ReadyKind));
        }

        [Test]
        public void AlreadyInitialized_Golden()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout);

            AssertPlan(plan, S(CaptureRunInitializationRecoveryAction.InitializationReady));
        }

        [Test]
        public void RequiresPublicationRecovery_Golden()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            CaptureRunInitializationRecoveryActionPlan plan = Build(staging, final, layout);

            AssertPlan(plan, S(CaptureRunInitializationRecoveryAction.ContinuePublicationRecovery));
        }

        [Test]
        public void RunRootCollision_Golden()
        {
            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);

            CaptureRunInitializationRecoveryActionPlan plan = Build(staging, final);

            AssertPlan(plan, S(CaptureRunInitializationRecoveryAction.StopRunRootCollision));
        }

        // ---- Tmp deletion order ----

        [Test]
        public void Cleanup_AllFourTmp_FixedOrder()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true, hasReadyTmp: true);
            CaptureRunInitializationRootObservation final = MakeObservation(Final, true, Absent, null, Absent, null, hasInitTmp: true, hasReadyTmp: true);

            CaptureRunInitializationRecoveryActionPlan plan = Build(staging, final, layout);

            AssertPlan(plan,
                S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Staging, InitKind),
                S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Staging, ReadyKind),
                S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Final, InitKind),
                S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Final, ReadyKind),
                S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, Final),
                S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, Staging),
                S(CaptureRunInitializationRecoveryAction.StartFreshInitialization));
        }

        [Test]
        public void Cleanup_SingleTmpPresence_OnlyObservedStep()
        {
            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasReadyTmp: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);

            CaptureRunInitializationRecoveryActionPlan plan = Build(staging, final);

            AssertPlan(plan,
                S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Staging, ReadyKind),
                S(CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, Staging),
                S(CaptureRunInitializationRecoveryAction.StartFreshInitialization));
        }

        [Test]
        public void CompleteReadyMarkers_TmpDeletedBeforeWrites()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Canonical, binding.StagingInitialization, Absent, null, hasInitTmp: true);
            CaptureRunInitializationRootObservation final = MakeObservation(
                Final, true, Canonical, binding.FinalInitialization, Absent, null, hasReadyTmp: true);

            CaptureRunInitializationRecoveryActionPlan plan = Build(staging, final, layout);

            AssertPlan(plan,
                S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Staging, InitKind),
                S(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, Final, ReadyKind),
                S(CaptureRunInitializationRecoveryAction.WriteMarker, Staging, ReadyKind),
                S(CaptureRunInitializationRecoveryAction.WriteMarker, Final, ReadyKind));
        }

        // ---- Source init never rewritten ----

        [Test]
        public void CompleteMissingPeer_SourceInitNeverWritten()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout);

            foreach (int i in Enumerable.Range(0, plan.Count))
            {
                CaptureRunInitializationRecoveryStep step = plan.GetStep(i);
                bool writesStagingInit = step.Action == CaptureRunInitializationRecoveryAction.WriteMarker
                    && step.RootRole == Staging
                    && step.MarkerKind == InitKind;
                Assert.That(writesStagingInit, Is.False, "The source initialization marker must never be rewritten.");
            }
        }

        [Test]
        public void CompleteReadyMarkers_OnlyAbsentReadyWritten()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeFullyCanonical(Staging, binding),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            foreach (int i in Enumerable.Range(0, plan.Count))
            {
                CaptureRunInitializationRecoveryStep step = plan.GetStep(i);
                Assert.That(step.Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.WriteMarker));
                Assert.That(step.MarkerKind, Is.EqualTo(ReadyKind));
                Assert.That(step.RootRole, Is.EqualTo(Final), "Only the absent final ready marker may be written.");
            }
        }

        [Test]
        public void NoPlanOverwritesCanonicalMarker()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            // Fully canonical observation already initialized -> no WriteMarker steps at all.
            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout);

            foreach (int i in Enumerable.Range(0, plan.Count))
            {
                Assert.That(plan.GetStep(i).Action, Is.Not.EqualTo(CaptureRunInitializationRecoveryAction.WriteMarker));
            }
        }

        // ---- Routing dispositions have a single step, collision is non-mutating ----

        [Test]
        public void AlreadyInitialized_SingleRoutingStep()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout);

            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan.GetStep(0).Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.InitializationReady));
        }

        [Test]
        public void RequiresPublicationRecovery_SingleRoutingStep()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true),
                MakeFullyCanonical(Final, binding),
                layout);

            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan.GetStep(0).Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.ContinuePublicationRecovery));
        }

        [Test]
        public void Collision_SingleNonMutatingStep()
        {
            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true),
                MakeAbsent(Final));

            Assert.That(plan.Count, Is.EqualTo(1));
            CaptureRunInitializationRecoveryStep step = plan.GetStep(0);
            Assert.That(step.Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.StopRunRootCollision));
            Assert.That(step.RootRole, Is.EqualTo(NoneRole));
            Assert.That(step.MarkerKind, Is.EqualTo(NoneKind));
        }

        // ---- Constructor immutability and allocation ownership ----

        [Test]
        public void Plan_NullDecision_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryActionPlan(null));

            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Plan_Constructor_TakesOnlyDecision_NoArrayParameter()
        {
            ConstructorInfo[] constructors = typeof(CaptureRunInitializationRecoveryActionPlan)
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(constructors.Length, Is.EqualTo(1));

            ParameterInfo[] parameters = constructors[0].GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunInitializationRecoveryDecision)));
        }

        [Test]
        public void Collision_Plan_SingleStepCannotBeSwapped()
        {
            CaptureRunInitializationRecoveryActionPlan plan = Build(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true),
                MakeAbsent(Final));

            Assert.That(plan.Count, Is.EqualTo(1));
            CaptureRunInitializationRecoveryStep step = plan.GetStep(0);
            Assert.That(step.Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.StopRunRootCollision));
            Assert.That(step.RootRole, Is.EqualTo(NoneRole));
            Assert.That(step.MarkerKind, Is.EqualTo(NoneKind));

            Type type = typeof(CaptureRunInitializationRecoveryActionPlan);
            Assert.That(
                type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(p => p.CanWrite || p.PropertyType == typeof(CaptureRunInitializationRecoveryStep[])),
                Is.False,
                "The plan must expose no writable property and no step array.");
        }

        // ---- IsValid on broken held values ----

        [Test]
        public void Plan_IsValid_False_WhenForged()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryDecision decision = Classify(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout); // CompleteReadyMarkers

            CaptureRunInitializationRecoveryActionPlan empty = (CaptureRunInitializationRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryActionPlan));
            Assert.That(empty.IsValid, Is.False);

            CaptureRunInitializationRecoveryActionPlan nullStep = (CaptureRunInitializationRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryActionPlan));
            SetField(nullStep, "_decision", decision);
            SetField(nullStep, "_steps", new CaptureRunInitializationRecoveryStep[] { null, S(CaptureRunInitializationRecoveryAction.WriteMarker, Final, ReadyKind) });
            Assert.That(nullStep.IsValid, Is.False);

            CaptureRunInitializationRecoveryActionPlan wrong = (CaptureRunInitializationRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryActionPlan));
            SetField(wrong, "_decision", decision);
            SetField(wrong, "_steps", new[] { S(CaptureRunInitializationRecoveryAction.WriteMarker, Staging, ReadyKind) });
            Assert.That(wrong.IsValid, Is.False);
        }

        [Test]
        public void CorruptedInitMarker_NullId_FailsClosed()
        {
            AssertCorruptedMarkerFailsClosed(m => SetField(m, "_runInitializationId", null));
        }

        [Test]
        public void CorruptedInitMarker_ShortId_FailsClosed()
        {
            AssertCorruptedMarkerFailsClosed(m => SetField(m, "_runInitializationId", new string('0', 31)));
        }

        [Test]
        public void CorruptedInitMarker_UppercaseId_FailsClosed()
        {
            AssertCorruptedMarkerFailsClosed(m => SetField(m, "_runInitializationId", "ABCDEF0123456789ABCDEF0123456789"));
        }

        [Test]
        public void CorruptedInitMarker_BadHash_FailsClosed()
        {
            AssertCorruptedMarkerFailsClosed(m => SetField(m, "_stagingRunRootSha256", "not-a-hash"));
        }

        [Test]
        public void Plan_GetStep_OutOfRange_Rejected()
        {
            CaptureRunInitializationRecoveryActionPlan plan = Build(MakeAbsent(Staging), MakeAbsent(Final));

            Assert.Throws<ArgumentOutOfRangeException>(() => plan.GetStep(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => plan.GetStep(plan.Count));
        }

        // ---- Non-mutation / non-dispose ----

        [Test]
        public void Build_DoesNotDisposeLeaseOrMutateInputs()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRootObservation staging = MakeFullyCanonical(Staging, binding);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout, disposeLog);
            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(snapshot);

            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(decision);

            Assert.That(disposeLog, Is.Empty, "The plan builder must not dispose the lock lease.");
            Assert.That(plan.Decision, Is.SameAs(decision));
            Assert.That(snapshot.Staging, Is.SameAs(staging));
            Assert.That(snapshot.Final, Is.SameAs(final));
            Assert.That(plan.RootLayout, Is.SameAs(layout));
            Assert.That(plan.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(plan.ExpectedBinding, Is.SameAs(decision.ExpectedBinding));
            Assert.That(plan.ExpectedBinding.RunInitializationId, Is.EqualTo(binding.RunInitializationId));
        }

        // ---- Array non-exposure and single-allocation contract ----

        [Test]
        public void Source_NoRedundantAllocation()
        {
            string builder = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryActionPlanBuilder.cs"));
            string plan = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryActionPlan.cs"));

            Assert.That(builder, Does.Not.Contain("List<"));
            Assert.That(builder, Does.Not.Contain("ToArray"));
            Assert.That(builder, Does.Not.Contain("System.Collections.Generic"));

            Assert.That(plan, Does.Not.Contain("Array.Copy"));
            Assert.That(plan, Does.Not.Contain("List<"));
            Assert.That(plan, Does.Not.Contain("ToArray"));
        }

        [Test]
        public void Plan_ArrayNotExposed_FieldsAreTwoReadonly()
        {
            Type type = typeof(CaptureRunInitializationRecoveryActionPlan);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            Assert.That(
                type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(p => p.PropertyType == typeof(CaptureRunInitializationRecoveryStep[])),
                Is.False,
                "The step array must not be exposed.");

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));

            int decisionFields = 0;
            int arrayFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunInitializationRecoveryDecision)) decisionFields++;
                else if (field.FieldType == typeof(CaptureRunInitializationRecoveryStep[])) arrayFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(decisionFields, Is.EqualTo(1));
            Assert.That(arrayFields, Is.EqualTo(1));
        }

        // ---- Shape / mutable static state ----

        [Test]
        public void Builder_NoFields_NoMutableStaticState()
        {
            Type type = typeof(CaptureRunInitializationRecoveryActionPlanBuilder);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance), Is.Empty);
        }

        [Test]
        public void NoMutableStaticState_AcrossTypes()
        {
            foreach (Type type in new[]
            {
                typeof(CaptureRunInitializationRecoveryStep),
                typeof(CaptureRunInitializationRecoveryActionPlan)
            })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
                }
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryAction.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryStep.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryActionPlan.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryActionPlanBuilder.cs"
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
            }
        }

        // ---- Assertion helpers ----

        private static void AssertPlan(
            CaptureRunInitializationRecoveryActionPlan plan,
            params CaptureRunInitializationRecoveryStep[] expected)
        {
            Assert.That(plan.Count, Is.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; i++)
            {
                CaptureRunInitializationRecoveryStep actual = plan.GetStep(i);
                Assert.That(actual.Action, Is.EqualTo(expected[i].Action), "Step " + i + " action mismatch.");
                Assert.That(actual.RootRole, Is.EqualTo(expected[i].RootRole), "Step " + i + " root role mismatch.");
                Assert.That(actual.MarkerKind, Is.EqualTo(expected[i].MarkerKind), "Step " + i + " marker kind mismatch.");
            }
        }

        private void AssertCorruptedMarkerFailsClosed(Action<CaptureRunInitializationMarker> corrupt)
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationMarker forged = (CaptureRunInitializationMarker)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationMarker));
            SetField(forged, "_testRunId", layout.TestRunId);
            SetField(forged, "_runInitializationId", InitId);
            SetField(forged, "_rootRole", Staging);
            SetField(forged, "_stagingRunRootSha256", layout.StagingRunRootSha256);
            SetField(forged, "_finalRunRootSha256", layout.FinalRunRootSha256);
            corrupt(forged);

            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Canonical, forged, Absent, null);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout);

            CaptureRunInitializationRecoveryDecision decision = (CaptureRunInitializationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryDecision));
            SetField(decision, "_snapshot", snapshot);
            SetField(decision, "_disposition", CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization);
            SetField(decision, "_expectedBinding", null);

            Assert.That(decision.IsValid, Is.False);

            ArgumentException buildEx = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationRecoveryActionPlanBuilder.Build(decision));
            Assert.That(buildEx.ParamName, Is.EqualTo("decision"));

            ArgumentException ctorEx = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryActionPlan(decision));
            Assert.That(ctorEx.ParamName, Is.EqualTo("decision"));

            CaptureRunInitializationRecoveryActionPlan plan = (CaptureRunInitializationRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryActionPlan));
            SetField(plan, "_decision", decision);
            SetField(plan, "_steps", new[] { S(CaptureRunInitializationRecoveryAction.StopRunRootCollision) });
            Assert.That(plan.IsValid, Is.False);
        }
    }
}
