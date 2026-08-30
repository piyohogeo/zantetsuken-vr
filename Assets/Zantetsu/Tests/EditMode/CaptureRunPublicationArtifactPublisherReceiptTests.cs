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
    public class CaptureRunPublicationArtifactPublisherReceiptTests
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

        private static CaptureRunPublicationArtifactKind Png => CaptureRunPublicationArtifactKind.Png;

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

        private static CaptureRunPublicationArtifactPublishOperation MakePublishOperation(
            out CaptureRunPublicationArtifactInspectionOperation inspectionOperation,
            out CaptureRunPublicationArtifactRecoveryActionPlan plan)
        {
            inspectionOperation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                inspectionOperation, inspectionOperation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);
            plan = CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(
                CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                    MakeArtifactSnapshot(new FakeArtifactInspector(), inspectionOperation, EvMatchesExpected, 100, new[] { observation })));
            return CaptureRunPublicationArtifactPublishOperationFactory.Create(plan, 0);
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationArtifactPublisherReceiptTests).Assembly.Location);
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

        private sealed class FakePublisher : ICaptureRunPublicationArtifactPublisher
        {
            public int CallCount { get; private set; }

            public Exception ExceptionToThrow { get; set; }

            public CaptureRunPublicationArtifactPublishReceipt ReceiptToReturn { get; set; }

            public CaptureRunPublicationArtifactPublishOperation LastOperation { get; private set; }

            public CaptureRunPublicationArtifactPublishReceipt Publish(CaptureRunPublicationArtifactPublishOperation operation)
            {
                CallCount++;
                LastOperation = operation;
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return ReceiptToReturn;
            }
        }

        // ---- Interface shape ----

        [Test]
        public void Interface_InternalSingleMethod()
        {
            Type type = typeof(ICaptureRunPublicationArtifactPublisher);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsInterface, Is.True);

            MethodInfo[] methods = type.GetMethods();
            Assert.That(methods.Length, Is.EqualTo(1));
            Assert.That(methods[0].Name, Is.EqualTo("Publish"));
            Assert.That(methods[0].ReturnType, Is.EqualTo(typeof(CaptureRunPublicationArtifactPublishReceipt)));

            ParameterInfo[] parameters = methods[0].GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunPublicationArtifactPublishOperation)));
        }

        // ---- Receipt rejection ----

        [Test]
        public void Receipt_NullIssuer_Rejected()
        {
            CaptureRunPublicationArtifactPublishOperation operation = MakePublishOperation(out _, out _);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationArtifactPublishReceipt(null, operation));
            Assert.That(ex.ParamName, Is.EqualTo("issuedBy"));
        }

        [Test]
        public void Receipt_NullOperation_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationArtifactPublishReceipt(new FakePublisher(), null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_InvalidOperation_Rejected()
        {
            CaptureRunPublicationArtifactPublishOperation operation = (CaptureRunPublicationArtifactPublishOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactPublishOperation));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactPublishReceipt(new FakePublisher(), operation));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_ForgedOperation_Rejected()
        {
            CaptureRunPublicationArtifactPublishOperation operation = MakePublishOperation(out _, out _);
            SetField(operation, "_actionPlan", null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactPublishReceipt(new FakePublisher(), operation));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        // ---- Receipt forwarding ----

        [Test]
        public void Receipt_IssuedByAndOperationReferenceEquals()
        {
            FakePublisher publisher = new FakePublisher();
            CaptureRunPublicationArtifactPublishOperation operation = MakePublishOperation(out _, out _);

            CaptureRunPublicationArtifactPublishReceipt receipt = new CaptureRunPublicationArtifactPublishReceipt(publisher, operation);

            Assert.That(receipt.IssuedBy, Is.SameAs(publisher));
            Assert.That(receipt.Operation, Is.SameAs(operation));
        }

        [Test]
        public void Receipt_ForwardsAllValues()
        {
            FakePublisher publisher = new FakePublisher();
            CaptureRunPublicationArtifactPublishOperation operation = MakePublishOperation(out _, out _);

            CaptureRunPublicationArtifactPublishReceipt receipt = new CaptureRunPublicationArtifactPublishReceipt(publisher, operation);

            Assert.That(receipt.EntryIndex, Is.EqualTo(operation.EntryIndex));
            Assert.That(receipt.ArtifactKind, Is.EqualTo(operation.ArtifactKind));
            Assert.That(receipt.CaptureFrameId, Is.EqualTo(operation.CaptureFrameId));
            Assert.That(receipt.SourcePath, Is.EqualTo(operation.SourcePath));
            Assert.That(receipt.DestinationPath, Is.EqualTo(operation.DestinationPath));
            Assert.That(receipt.ExpectedByteCount, Is.EqualTo(operation.ExpectedByteCount));
            Assert.That(receipt.ExpectedContentSha256, Is.EqualTo(operation.ExpectedContentSha256));
            Assert.That(receipt.RootLayout, Is.SameAs(operation.RootLayout));
            Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(receipt.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
        }

        // ---- Receipt IsValid / IsIssuedFor ----

        [Test]
        public void Receipt_IsValid_NormalTrue()
        {
            CaptureRunPublicationArtifactPublishReceipt receipt = new CaptureRunPublicationArtifactPublishReceipt(
                new FakePublisher(), MakePublishOperation(out _, out _));

            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void Receipt_IsValid_UninitializedFalse()
        {
            CaptureRunPublicationArtifactPublishReceipt receipt = (CaptureRunPublicationArtifactPublishReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactPublishReceipt));

            Assert.That(receipt.IsValid, Is.False);
        }

        [Test]
        public void Receipt_IsValid_OperationCorruptedFalse()
        {
            CaptureRunPublicationArtifactPublishOperation operation = MakePublishOperation(out _, out _);
            CaptureRunPublicationArtifactPublishReceipt receipt = new CaptureRunPublicationArtifactPublishReceipt(
                new FakePublisher(), operation);
            Assert.That(receipt.IsValid, Is.True);

            SetField(operation, "_actionPlan", null);

            Assert.That(receipt.IsValid, Is.False);
        }

        [Test]
        public void Receipt_IsValid_LeaseReleasedFalse()
        {
            CaptureRunPublicationArtifactPublishOperation operation = MakePublishOperation(out CaptureRunPublicationArtifactInspectionOperation inspection, out _);
            CaptureRunPublicationArtifactPublishReceipt receipt = new CaptureRunPublicationArtifactPublishReceipt(
                new FakePublisher(), operation);
            Assert.That(receipt.IsValid, Is.True);

            inspection.LockLease.Dispose();

            Assert.That(receipt.IsValid, Is.False);
        }

        [Test]
        public void Receipt_IsIssuedFor_MatchForeignAndDifferentOperation()
        {
            FakePublisher publisher = new FakePublisher();
            FakePublisher otherPublisher = new FakePublisher();
            CaptureRunPublicationArtifactPublishOperation operation = MakePublishOperation(out _, out _);
            CaptureRunPublicationArtifactPublishReceipt receipt = new CaptureRunPublicationArtifactPublishReceipt(publisher, operation);

            Assert.That(receipt.IsIssuedFor(publisher, operation), Is.True);
            Assert.That(receipt.IsIssuedFor(otherPublisher, operation), Is.False);

            CaptureRunPublicationArtifactPublishOperation otherOperation = MakePublishOperation(out _, out _);
            Assert.That(receipt.IsIssuedFor(publisher, otherOperation), Is.False);
        }

        // ---- Fake publisher ----

        [Test]
        public void FakePublisher_ReturnsReceiptForSameOperation()
        {
            FakePublisher publisher = new FakePublisher();
            CaptureRunPublicationArtifactPublishOperation operation = MakePublishOperation(out _, out _);
            CaptureRunPublicationArtifactPublishReceipt receipt = new CaptureRunPublicationArtifactPublishReceipt(publisher, operation);
            publisher.ReceiptToReturn = receipt;

            CaptureRunPublicationArtifactPublishReceipt returned = publisher.Publish(operation);

            Assert.That(returned, Is.SameAs(receipt));
            Assert.That(publisher.CallCount, Is.EqualTo(1));
            Assert.That(publisher.LastOperation, Is.SameAs(operation));
        }

        [Test]
        public void FakePublisher_ExceptionNotTransformedOrRetried()
        {
            InvalidOperationException boom = new InvalidOperationException("boom");
            FakePublisher publisher = new FakePublisher { ExceptionToThrow = boom };
            CaptureRunPublicationArtifactPublishOperation operation = MakePublishOperation(out _, out _);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => publisher.Publish(operation));

            Assert.That(ex, Is.SameAs(boom));
            Assert.That(publisher.CallCount, Is.EqualTo(1));
        }

        // ---- No mutation ----

        [Test]
        public void Receipt_DoesNotMutateOperation()
        {
            CaptureRunPublicationArtifactPublishOperation operation = MakePublishOperation(out _, out CaptureRunPublicationArtifactRecoveryActionPlan plan);
            string sourceBefore = operation.SourcePath;
            string destinationBefore = operation.DestinationPath;

            CaptureRunPublicationArtifactPublishReceipt receipt = new CaptureRunPublicationArtifactPublishReceipt(
                new FakePublisher(), operation);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(plan.IsValid, Is.True);
            Assert.That(operation.SourcePath, Is.EqualTo(sourceBefore));
            Assert.That(operation.DestinationPath, Is.EqualTo(destinationBefore));
            Assert.That(receipt.IsValid, Is.True);
        }

        // ---- Shape ----

        [Test]
        public void Receipt_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationArtifactPublishReceipt);

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
        public void Receipt_FieldShape_TwoReadonlyRefs_NoStaticState()
        {
            FieldInfo[] fields = typeof(CaptureRunPublicationArtifactPublishReceipt).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(2));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(ICaptureRunPublicationArtifactPublisher)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunPublicationArtifactPublishOperation)));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.FieldType.IsArray, Is.False);
                Assert.That(typeof(Stream).IsAssignableFrom(field.FieldType), Is.False);
            }

            FieldInfo[] staticFields = typeof(CaptureRunPublicationArtifactPublishReceipt).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty, "Receipt must not hold static mutable state.");
        }

        // ---- Source / contract ----

        [Test]
        public void Publisher_Source_NoForbiddenDependenciesAndContract()
        {
            string interfaceSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunPublicationArtifactPublisher.cs"));
            string receiptSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactPublishReceipt.cs"));

            foreach (string source in new[] { interfaceSource, receiptSource })
            {
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("System.Security.Cryptography"));
                Assert.That(source, Does.Not.Contain("SHA256"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("Dictionary"));
                Assert.That(source, Does.Not.Contain("HashSet"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("Registry"));
            }

            // The interface XML contract must state the required guarantees.
            Assert.That(interfaceSource, Does.Contain("overwrite"));
            Assert.That(interfaceSource, Does.Contain("durably"));
            Assert.That(interfaceSource, Does.Contain("re-inspection"));
            Assert.That(interfaceSource, Does.Contain("volumes"));
            Assert.That(interfaceSource, Does.Contain("must not return a receipt"));
        }
    }
}
