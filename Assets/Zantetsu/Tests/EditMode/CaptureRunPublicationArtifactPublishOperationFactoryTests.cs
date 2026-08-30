using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunPublicationArtifactPublishOperationFactoryTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const long PngBytes = 16;

        private const long SidecarBytes = 32;

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationArtifactKind NoneKind => CaptureRunPublicationArtifactKind.None;

        private static CaptureRunPublicationArtifactKind Png => CaptureRunPublicationArtifactKind.Png;

        private static CaptureRunPublicationArtifactKind Sidecar => CaptureRunPublicationArtifactKind.Sidecar;

        private static CaptureRunPublicationArtifactRecoveryAction PublishArtifact => CaptureRunPublicationArtifactRecoveryAction.PublishArtifact;

        // ---- Helpers ----

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "D:\\final" : "/final",
                testRunId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
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
                role, rootExists, hasInitTmp, initStatus, initMarker,
                hasReadyTmp, readyStatus, readyMarker, hasNonMarker, hasUnknown, false);
        }

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(CaptureRunRootRole role, CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CapturePublicationPlanEntry MakeEntry(
            long captureFrameId,
            long pngByteLength = PngBytes,
            long sidecarByteLength = SidecarBytes,
            string pngHash = null,
            string sidecarHash = null)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            return new CapturePublicationPlanEntry(
                captureFrameId,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".json.stage",
                "frames/" + id + ".png",
                "frames/" + id + ".json",
                pngByteLength,
                sidecarByteLength,
                pngHash ?? StagingHash,
                sidecarHash ?? StagingHash);
        }

        private static CapturePublicationPlanEntry[] MakeEntries(int count)
        {
            CapturePublicationPlanEntry[] entries = new CapturePublicationPlanEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = MakeEntry(i + 1);
            }

            return entries;
        }

        private static CapturePublicationPlan MakePlan(
            long testRunId = 1,
            string initId = null,
            string manifestHash = null,
            CapturePublicationPlanEntry[] entries = null)
        {
            return new CapturePublicationPlan(
                testRunId,
                initId ?? InitId,
                manifestHash ?? StagingHash,
                entries ?? new[] { MakeEntry(10) });
        }

        private static CaptureRunPublicationDocumentObservation MakeDoc(
            CaptureRunPublicationDocumentKind kind,
            CaptureRunPublicationDocumentObservationStatus status,
            int probedByteCount = 0,
            CapturePublicationPlan plan = null)
        {
            return new CaptureRunPublicationDocumentObservation(kind, status, probedByteCount, plan);
        }

        private static CaptureRunInitializationOpenOutcome ForgeOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult result,
            CaptureRunLockLease lease)
        {
            CaptureRunInitializationOpenOutcome outcome = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(outcome, "_orchestrationResult", result);
            SetField(outcome, "_lockLease", lease);
            return outcome;
        }

        private static CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(List<string> disposeLog = null)
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            FakeInspector inspector = new FakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, lease, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, lease);
        }

        private static CaptureRunPublicationRecoveryInspectionSnapshot MakeRecoverySnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null)
        {
            return new CaptureRunPublicationRecoveryInspectionSnapshot(
                issuedBy,
                operation,
                publicationPlanTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent),
                publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent),
                captureIndexTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private static CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog = null,
            bool indexAuthoritative = false,
            CapturePublicationPlan plan = null,
            int maximumEntryCount = 4)
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog);
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = new CaptureRunPublicationRecoveryInspectionOperation(
                outcome, 1000, maximumEntryCount, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            plan = plan ?? MakePlan();
            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, recoveryOperation, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, recoveryOperation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(recoverySnapshot);
            return new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
        }

        private static CaptureRunPublicationArtifactEntryObservation MakeEntryObservation(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactPathSet artifactPaths,
            CaptureRunPublicationEvidenceStatus stagingPngStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long stagingPngCount = 0,
            CaptureRunPublicationEvidenceStatus stagingSidecarStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long stagingSidecarCount = 0,
            CaptureRunPublicationEvidenceStatus finalPngStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long finalPngCount = 0,
            CaptureRunPublicationEvidenceStatus finalSidecarStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long finalSidecarCount = 0)
        {
            return new CaptureRunPublicationArtifactEntryObservation(
                operation,
                artifactPaths,
                stagingPngStatus,
                stagingPngCount,
                stagingSidecarStatus,
                stagingSidecarCount,
                finalPngStatus,
                finalPngCount,
                finalSidecarStatus,
                finalSidecarCount);
        }

        private static CaptureRunPublicationArtifactInspectionSnapshot MakeArtifactSnapshot(
            ICaptureRunPublicationArtifactInspector issuedBy,
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long traceCount = 0,
            CaptureRunPublicationArtifactEntryObservation[] entries = null)
        {
            if (entries == null)
            {
                entries = new CaptureRunPublicationArtifactEntryObservation[operation.EntryCount];
                for (int i = 0; i < entries.Length; i++)
                {
                    entries[i] = MakeEntryObservation(operation, operation.GetArtifactPaths(i));
                }
            }

            return new CaptureRunPublicationArtifactInspectionSnapshot(issuedBy, operation, traceStatus, traceCount, entries);
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildPlan(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactEntryObservation[] entries,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            long traceCount = 100)
        {
            return CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(
                CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                    MakeArtifactSnapshot(new FakeArtifactInspector(), operation, traceStatus, traceCount, entries)));
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildPublishPngPlan(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            out CaptureRunPublicationArtifactEntryObservation observation)
        {
            operation = MakeOperation();
            observation = MakeEntryObservation(operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);
            return BuildPlan(operation, new[] { observation });
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildPublishSidecarPlan(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            out CaptureRunPublicationArtifactEntryObservation observation)
        {
            operation = MakeOperation();
            observation = MakeEntryObservation(operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvAbsent, finalSidecarCount: 0);
            return BuildPlan(operation, new[] { observation });
        }

        private static CaptureRunPublicationArtifactPublishOperation ForgeOperation(
            CaptureRunPublicationArtifactRecoveryActionPlan plan,
            int stepIndex,
            CaptureRunPublicationArtifactPathSet pathSet)
        {
            CaptureRunPublicationArtifactPublishOperation operation = (CaptureRunPublicationArtifactPublishOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactPublishOperation));
            SetField(operation, "_actionPlan", plan);
            SetField(operation, "_stepIndex", stepIndex);
            SetField(operation, "_artifactPaths", pathSet);
            return operation;
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationArtifactPublishOperationFactoryTests).Assembly.Location);
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

        // ---- Fakes ----

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

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private sealed class FakeInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            public FakeInspector(CaptureRunInitializationRootObservation staging, CaptureRunInitializationRootObservation final)
            {
                _staging = staging;
                _final = final;
            }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                return new CaptureRunInitializationRecoveryInspectionSnapshot(this, operation, _staging, _final);
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            public CaptureRunInitializationRecoveryCleanupReceipt Execute(CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private sealed class FakePublicationInspector : ICaptureRunPublicationRecoveryInspector
        {
            public CaptureRunPublicationRecoveryInspectionSnapshot Inspect(CaptureRunPublicationRecoveryInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
            }
        }

        private sealed class FakeArtifactInspector : ICaptureRunPublicationArtifactInspector
        {
            public CaptureRunPublicationArtifactInspectionSnapshot Inspect(CaptureRunPublicationArtifactInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
            }
        }

        // ---- Forwarding ----

        [Test]
        public void Operation_Png_ForwardsAllValues()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out CaptureRunPublicationArtifactInspectionOperation operation, out _);
            CaptureRunPublicationArtifactPathSet pathSet = operation.GetArtifactPaths(0);

            CaptureRunPublicationArtifactPublishOperation publish = CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0);

            Assert.That(publish.ActionPlan, Is.SameAs(plan));
            Assert.That(publish.StepIndex, Is.EqualTo(0));
            Assert.That(publish.Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(publish.Decision, Is.SameAs(plan.Decision));
            Assert.That(publish.EntryIndex, Is.EqualTo(0));
            Assert.That(publish.ArtifactKind, Is.EqualTo(Png));
            Assert.That(publish.Entry, Is.SameAs(pathSet.Entry));
            Assert.That(publish.CaptureFrameId, Is.EqualTo(10));
            Assert.That(publish.SourcePath, Is.EqualTo(pathSet.StagingPngPath));
            Assert.That(publish.DestinationPath, Is.EqualTo(pathSet.FinalPngPath));
            Assert.That(publish.ExpectedByteCount, Is.EqualTo(PngBytes));
            Assert.That(publish.ExpectedContentSha256, Is.EqualTo(pathSet.Entry.PngContentSha256));
            Assert.That(publish.RootLayout, Is.SameAs(plan.RootLayout));
            Assert.That(publish.TestRunId, Is.EqualTo(1));
            Assert.That(publish.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(publish.IsValid, Is.True);
        }

        [Test]
        public void Operation_Sidecar_ForwardsAllValues()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishSidecarPlan(out CaptureRunPublicationArtifactInspectionOperation operation, out _);
            CaptureRunPublicationArtifactPathSet pathSet = operation.GetArtifactPaths(0);

            CaptureRunPublicationArtifactPublishOperation publish = CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0);

            Assert.That(publish.ArtifactKind, Is.EqualTo(Sidecar));
            Assert.That(publish.SourcePath, Is.EqualTo(pathSet.StagingSidecarPath));
            Assert.That(publish.DestinationPath, Is.EqualTo(pathSet.FinalSidecarPath));
            Assert.That(publish.ExpectedByteCount, Is.EqualTo(SidecarBytes));
            Assert.That(publish.ExpectedContentSha256, Is.EqualTo(pathSet.Entry.SidecarContentSha256));
            Assert.That(publish.IsValid, Is.True);
        }

        [Test]
        public void Operation_EntryOrderIndependent()
        {
            CapturePublicationPlan planEntries = MakePlan(entries: new[] { MakeEntry(1), MakeEntry(2) });
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, false, planEntries, 4);

            CaptureRunPublicationArtifactEntryObservation e0 = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);
            CaptureRunPublicationArtifactEntryObservation e1 = MakeEntryObservation(
                operation, operation.GetArtifactPaths(1),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, new[] { e0, e1 });

            CaptureRunPublicationArtifactPublishOperation publish = CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0);

            Assert.That(publish.StepIndex, Is.EqualTo(0));
            Assert.That(publish.EntryIndex, Is.EqualTo(1));
            Assert.That(publish.ArtifactKind, Is.EqualTo(Png));
            Assert.That(publish.CaptureFrameId, Is.EqualTo(2));
            Assert.That(publish.SourcePath, Is.EqualTo(operation.GetArtifactPaths(1).StagingPngPath));
            Assert.That(publish.DestinationPath, Is.EqualTo(operation.GetArtifactPaths(1).FinalPngPath));
            Assert.That(publish.IsValid, Is.True);
        }

        // ---- Rejection ----

        [Test]
        public void Factory_NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationArtifactPublishOperationFactory.Create(null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Factory_InvalidPlan_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = (CaptureRunPublicationArtifactRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryActionPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Factory_StepIndexOutOfRange_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);

            foreach (int bad in new[] { -1, 2, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, bad));
                Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
            }
        }

        [Test]
        public void Factory_NonPublishStep_Rejected()
        {
            // A CommitCaptureIndex plan's only step is a routing step.
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, new[] { observation });

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Factory_ReinspectStep_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);
            // The last step of a publish plan is ReinspectArtifacts.
            int reinspectIndex = plan.Count - 1;

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, reinspectIndex));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        // ---- Precondition ----

        [Test]
        public void Factory_StagingNotMatches_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out CaptureRunPublicationArtifactEntryObservation observation);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(observation, "_stagingPngStatus", EvAbsent);

            Assert.That(plan.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.Throws<ArgumentException>(() => CaptureRunPublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0));
        }

        [Test]
        public void Factory_FinalNotAbsent_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out CaptureRunPublicationArtifactEntryObservation observation);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(observation, "_finalPngStatus", EvMatchesExpected);
            SetField(observation, "_finalPngProbedByteCount", PngBytes);

            Assert.That(plan.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.Throws<ArgumentException>(() => CaptureRunPublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0));
        }

        // ---- Correlation ----

        [Test]
        public void Factory_ForeignDecisionPathSet_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationArtifactPathSet foreignPaths = MakeOperation().GetArtifactPaths(0);

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new CaptureRunPublicationArtifactPublishOperation(plan, token, 0, foreignPaths));
            Assert.That(ex.ParamName, Is.EqualTo("artifactPaths"));
        }

        [Test]
        public void Factory_StepObservationEntryIndexMismatch_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out CaptureRunPublicationArtifactInspectionOperation operation, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationArtifactPathSet pathSet = operation.GetArtifactPaths(0);
            SetField(pathSet, "_entryIndex", 1);

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new CaptureRunPublicationArtifactPublishOperation(plan, token, 0, pathSet));
            Assert.That(ex.ParamName, Is.EqualTo("artifactPaths"));
        }

        [Test]
        public void Factory_CrossToken_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan planA = BuildPublishPngPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan planB = BuildPublishPngPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken tokenA = planA.AcquireValidationToken();

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                CaptureRunPublicationArtifactPublishOperationFactory.CreateIndexLocal(planB, tokenA, 0));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void Factory_StaleToken_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out CaptureRunPublicationArtifactInspectionOperation operation, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            operation.LockLease.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                CaptureRunPublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        // ---- Forge defense ----

        [Test]
        public void Operation_ForgedFields_IsValidFalse_NoException()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out CaptureRunPublicationArtifactInspectionOperation operation, out CaptureRunPublicationArtifactEntryObservation observation);
            CaptureRunPublicationArtifactPathSet pathSet = operation.GetArtifactPaths(0);

            CaptureRunPublicationArtifactPublishOperation publish = CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0);
            Assert.That(publish.IsValid, Is.True);

            // Null plan.
            Assert.That(ForgeOperation(null, 0, pathSet).IsValid, Is.False);

            // Step index out of range.
            Assert.That(ForgeOperation(plan, 99, pathSet).IsValid, Is.False);

            // Null path set.
            Assert.That(ForgeOperation(plan, 0, null).IsValid, Is.False);

            // Foreign path set.
            CaptureRunPublicationArtifactPathSet foreign = MakeOperation().GetArtifactPaths(0);
            Assert.That(ForgeOperation(plan, 0, foreign).IsValid, Is.False);

            // Forged observation status invalidates the whole plan.
            SetField(observation, "_stagingPngStatus", EvAbsent);
            Assert.That(publish.IsValid, Is.False);

            // Forged plan entry hash invalidates the whole plan and any operation over it.
            CaptureRunPublicationArtifactRecoveryActionPlan plan2 = BuildPublishPngPlan(out CaptureRunPublicationArtifactInspectionOperation operation2, out _);
            CaptureRunPublicationArtifactPublishOperation publish2 = CaptureRunPublicationArtifactPublishOperationFactory.Create(plan2, 0);
            Assert.That(publish2.IsValid, Is.True);
            SetField(operation2.GetArtifactPaths(0).Entry, "_pngContentSha256", "nothex");
            Assert.That(plan2.IsValid, Is.False);
            Assert.That(publish2.IsValid, Is.False);
        }

        [Test]
        public void Operation_RepeatedCreate_DistinctInstances_SharedInput()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);

            CaptureRunPublicationArtifactPublishOperation first = CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0);
            CaptureRunPublicationArtifactPublishOperation second = CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0);

            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(first.ActionPlan, Is.SameAs(plan));
            Assert.That(second.ActionPlan, Is.SameAs(plan));
            Assert.That(first.Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(second.Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(first.SourcePath, Is.EqualTo(second.SourcePath));
            Assert.That(first.DestinationPath, Is.EqualTo(second.DestinationPath));
        }

        [Test]
        public void Operation_DoesNotMutateOrDisposeInputs()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(disposeLog);
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, new[] { observation });

            CaptureRunPublicationArtifactPublishOperation publish = CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0);

            Assert.That(disposeLog, Is.Empty, "Factory must not dispose the lease.");
            Assert.That(operation.LockLease.IsCreated, Is.True);
            Assert.That(plan.IsValid, Is.True);
            Assert.That(publish.IsValid, Is.True);
        }

        // ---- Shape ----

        [Test]
        public void Operation_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationArtifactPublishOperation);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
            }
        }

        [Test]
        public void Operation_FieldShape_ThreeReadonlyFields()
        {
            FieldInfo[] fields = typeof(CaptureRunPublicationArtifactPublishOperation).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(3));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Factory_IsStaticWithNoState()
        {
            Type type = typeof(CaptureRunPublicationArtifactPublishOperationFactory);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        // ---- Linearity / source ----

        [Test]
        public void Factory_LargePlan_IndexLocalLinear()
        {
            int count = 500;
            CapturePublicationPlan planEntries = MakePlan(entries: MakeEntries(count));
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, false, planEntries, count);

            CaptureRunPublicationArtifactEntryObservation[] entries = new CaptureRunPublicationArtifactEntryObservation[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = MakeEntryObservation(
                    operation, operation.GetArtifactPaths(i),
                    EvMatchesExpected, PngBytes, EvMatchesExpected, SidecarBytes,
                    EvAbsent, 0, EvAbsent, 0);
            }

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, entries);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationArtifactPublishOperation first = CaptureRunPublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0);
            Assert.That(first.EntryIndex, Is.EqualTo(0));
            Assert.That(first.ArtifactKind, Is.EqualTo(Png));

            CaptureRunPublicationArtifactPublishOperation last = CaptureRunPublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 2 * count - 1);
            Assert.That(last.EntryIndex, Is.EqualTo(count - 1));
            Assert.That(last.ArtifactKind, Is.EqualTo(Sidecar));
        }

        [Test]
        public void Factory_Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactPublishOperationFactory.cs"));

            Assert.That(source, Does.Not.Contain("List<"));
            Assert.That(source, Does.Not.Contain("ToArray"));
            Assert.That(source, Does.Not.Contain("Array.Copy"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("Dictionary"));
            Assert.That(source, Does.Not.Contain("HashSet"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("SHA"));
            Assert.That(source, Does.Not.Contain("Serialize"));
            Assert.That(source, Does.Not.Contain("Deserialize"));

            // The index-local path must not re-validate the whole plan.
            int indexLocal = source.IndexOf("CreateIndexLocal", StringComparison.Ordinal);
            Assert.That(indexLocal, Is.GreaterThan(0));
            Assert.That(source.Substring(indexLocal), Does.Not.Contain("actionPlan.IsValid"));

            // The operation file must not recompute hashes or touch the filesystem.
            string operationSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactPublishOperation.cs"));
            Assert.That(operationSource, Does.Not.Contain("File."));
            Assert.That(operationSource, Does.Not.Contain("Directory."));
            Assert.That(operationSource, Does.Not.Contain("SHA"));
            Assert.That(operationSource, Does.Not.Contain("System.Linq"));
            Assert.That(operationSource, Does.Not.Contain("List<"));
        }
    }
}
