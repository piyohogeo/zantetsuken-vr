using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class PngJsonCapturePublicationCaptureCompleteNotifierConcreteTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

        private static PngJsonCapturePublicationCaptureCompleteNotificationAcceptance Accepted =>
            PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.Accepted;

        private static PngJsonCapturePublicationCaptureCompleteNotificationAcceptance AlreadyAccepted =>
            PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.AlreadyAccepted;

        private static PngJsonCapturePublicationCaptureCompleteNotificationAcceptance IdentityConflict =>
            PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.IdentityConflict;

        private static PngJsonCapturePublicationCaptureCompleteNotificationAcceptance Rejected =>
            PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.Rejected;

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

        // ---- General helpers ----

        private static CaptureRunRootLayout MakeLayout(string stagingBase, string finalBase, long testRunId = 1)
        {
            return new CaptureRunRootLayout(stagingBase, finalBase, testRunId);
        }

        private static CaptureRunRootLayout MakeDefaultLayout(long testRunId = 1)
        {
            return MakeLayout(
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

        private static string RepositoryRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string ReadSource(string relativePath)
        {
            return File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));
        }

        private static int CountSubstring(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static long Min(long left, long right)
        {
            return left < right ? left : right;
        }

        private static long Probe(CaptureRunPublicationEvidenceStatus status, long expectedByteLength, long limit)
        {
            switch (status)
            {
                case CaptureRunPublicationEvidenceStatus.Absent:
                    return 0;

                case CaptureRunPublicationEvidenceStatus.MatchesExpected:
                    return expectedByteLength;

                case CaptureRunPublicationEvidenceStatus.Mismatch:
                    return 1;

                case CaptureRunPublicationEvidenceStatus.Invalid:
                    return 0;

                case CaptureRunPublicationEvidenceStatus.LimitExceeded:
                    return checked(limit + 1);

                default:
                    throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        // ---- Fakes ----

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            public FakeHandle(string lockPath, bool isCreated = true)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public void Dispose()
            {
            }
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

        private sealed class FakeArtifactInspector : IPngJsonCapturePublicationArtifactInspector
        {
            public PngJsonCapturePublicationArtifactInspectionSnapshot Snapshot { get; set; }

            public PngJsonCapturePublicationArtifactInspectionSnapshot Inspect(PngJsonCapturePublicationArtifactInspectionOperation operation)
            {
                return Snapshot;
            }
        }

        private sealed class FakePublisher : IPngJsonCapturePublicationArtifactPublisher
        {
            public IPngJsonCapturePublicationArtifactPublishAttempt TryBegin(
                PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
                return new FakeAttempt();
            }

            public PngJsonCapturePublicationArtifactPublishReceipt PublishReserved(
                IPngJsonCapturePublicationArtifactPublishAttempt attempt,
                PngJsonCapturePublicationArtifactPublishOperation operation,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
                return PngJsonCapturePublicationArtifactPublishReceipt.Create(this, operation, token);
            }

            public void End(IPngJsonCapturePublicationArtifactPublishAttempt attempt)
            {
            }

            private sealed class FakeAttempt : IPngJsonCapturePublicationArtifactPublishAttempt
            {
            }
        }

        private sealed class FakeCommitter : IPngJsonCaptureRunCaptureIndexCommitter
        {
            public PngJsonCaptureRunCaptureIndexCommitReceipt Commit(
                PngJsonCaptureRunCaptureIndexCommitOperation operation,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
                return PngJsonCaptureRunCaptureIndexCommitReceipt.Create(this, operation, token);
            }
        }

        private sealed class FakeCleanup : IPngJsonCapturePublicationCaptureCompleteCleanupBackend
        {
            public PngJsonCapturePublicationCaptureCompleteCleanupReceipt Execute(
                PngJsonCapturePublicationCaptureCompleteCleanupOperation operation,
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
            {
                return PngJsonCapturePublicationCaptureCompleteCleanupReceipt.Create(this, operation, token);
            }
        }

        private sealed class RecordingSink : IPngJsonCapturePublicationCaptureCompleteNotificationSink
        {
            public PngJsonCapturePublicationCaptureCompleteNotificationAcceptance Response = Accepted;
            public Exception Thrown;
            public int CallCount;
            public readonly List<PngJsonCapturePublicationCaptureCompleteNotificationIdentity> Identities =
                new List<PngJsonCapturePublicationCaptureCompleteNotificationIdentity>();

            public PngJsonCapturePublicationCaptureCompleteNotificationAcceptance Accept(
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity identity)
            {
                CallCount++;
                if (Thrown != null)
                {
                    throw Thrown;
                }

                Identities.Add(identity);
                return Response;
            }
        }

        private sealed class IdempotentSink : IPngJsonCapturePublicationCaptureCompleteNotificationSink
        {
            private readonly List<PngJsonCapturePublicationCaptureCompleteNotificationIdentity> _seen =
                new List<PngJsonCapturePublicationCaptureCompleteNotificationIdentity>();

            public int CallCount;

            public PngJsonCapturePublicationCaptureCompleteNotificationAcceptance Accept(
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity identity)
            {
                CallCount++;
                foreach (PngJsonCapturePublicationCaptureCompleteNotificationIdentity seen in _seen)
                {
                    if (seen.IsSameIdentity(identity))
                    {
                        return AlreadyAccepted;
                    }
                }

                _seen.Add(identity);
                return Accepted;
            }
        }

        private sealed class ConflictSink : IPngJsonCapturePublicationCaptureCompleteNotificationSink
        {
            private PngJsonCapturePublicationCaptureCompleteNotificationIdentity _first;

            public int CallCount;

            public PngJsonCapturePublicationCaptureCompleteNotificationAcceptance Accept(
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity identity)
            {
                CallCount++;
                if (_first == null)
                {
                    _first = identity;
                    return Accepted;
                }

                if (_first.IsSameIdentity(identity))
                {
                    return AlreadyAccepted;
                }

                if (_first.ConflictsWith(identity))
                {
                    return IdentityConflict;
                }

                return Accepted;
            }
        }

        // ---- Lease ----

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        // ---- Recovery decision graph ----

        private static CaptureRunMarkerBinding MakeMarkerBinding(CaptureRunRootLayout layout)
        {
            return CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);
        }

        private static CaptureRunInitializationRootObservation MakeRootObservation(
            CaptureRunRootRole role,
            bool rootExists,
            CaptureRunMarkerObservationStatus initStatus,
            CaptureRunInitializationMarker initMarker,
            CaptureRunMarkerObservationStatus readyStatus,
            CaptureRunReadyMarker readyMarker,
            bool hasNonMarker = false)
        {
            return new CaptureRunInitializationRootObservation(
                role, rootExists, false, initStatus, initMarker,
                false, readyStatus, readyMarker, hasNonMarker, false, false);
        }

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(
            CaptureRunRootRole role,
            CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeRootObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CaptureRunInitializationOpenOutcome ForgeOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult result,
            CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            CaptureRunInitializationOpenOutcome outcome =
                (CaptureRunInitializationOpenOutcome)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationOpenOutcome));
            SetField(outcome, "_orchestrationResult", result);
            SetField(outcome, "_sessionIssue", null);
            SetField(outcome, "_lockIdentityEvidence", lockIdentityEvidence);
            return outcome;
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunMarkerBinding binding = MakeMarkerBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            FakeInspector inspector = new FakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryInspectionOperation inspection =
                new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, identity);
        }

        private CaptureRunPublicationRecoveryInspectionOperation MakeRecoveryInspectionOperation(
            CaptureRunRootLayout layout,
            int maximumPlanBytes,
            int maximumEntryCount,
            int maximumPathBytes,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(layout, out owner),
                maximumPlanBytes,
                maximumEntryCount,
                maximumPathBytes);
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

        private CaptureRunPublicationRecoveryDecision MakeDecision(
            CaptureRunRootLayout layout,
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation =
                MakeRecoveryInspectionOperation(layout, 1000, 4, 64, out owner);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            CaptureRunRootLayout layout,
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(
                MakeDecision(layout, plan, indexAuthoritative, out owner));
        }

        // ---- Plan / entry / doc ----

        private static PngJsonCapturePublicationPlanEntry MakeEntry(
            long captureFrameId,
            long pngByteLength = 16,
            long sidecarByteLength = 32)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            return new PngJsonCapturePublicationPlanEntry(
                captureFrameId,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".json.stage",
                "frames/" + id + ".png",
                "frames/" + id + ".json",
                pngByteLength,
                sidecarByteLength,
                HashA,
                HashA);
        }

        private static PngJsonCapturePublicationPlan MakePlan(
            long testRunId,
            string initId,
            string manifestHash,
            PngJsonCapturePublicationPlanEntry[] entries)
        {
            return new PngJsonCapturePublicationPlan(testRunId, initId, manifestHash, entries);
        }

        private static CaptureRunPublicationDocumentObservation MakeDoc(
            CaptureRunPublicationDocumentKind kind,
            CaptureRunPublicationDocumentObservationStatus status,
            int probedByteCount = 0,
            PngJsonCapturePublicationPlan plan = null)
        {
            return new CaptureRunPublicationDocumentObservation(kind, status, probedByteCount, plan);
        }

        // ---- Operation / observation / snapshot ----

        private static PngJsonCapturePublicationArtifactInspectionOperation MakeOperation(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            long maximumPngByteCount = 1000)
        {
            return PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, maximumPngByteCount);
        }

        private static PngJsonCapturePublicationArtifactEntryObservation MakeIndexObservation(
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token,
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            int index,
            CaptureRunPublicationEvidenceStatus stagingPng,
            CaptureRunPublicationEvidenceStatus stagingSidecar,
            CaptureRunPublicationEvidenceStatus finalPng,
            CaptureRunPublicationEvidenceStatus finalSidecar)
        {
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(index);
            PngJsonCapturePublicationPlanEntry entry = paths.Entry;
            long pngLimit = Min(entry.PngByteLength, operation.MaximumPngByteCount);
            long sidecarLimit = Min(entry.SidecarByteLength, operation.MaximumSidecarByteCount);
            return PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                token, operation, paths,
                stagingPng, Probe(stagingPng, entry.PngByteLength, pngLimit),
                stagingSidecar, Probe(stagingSidecar, entry.SidecarByteLength, sidecarLimit),
                finalPng, Probe(finalPng, entry.PngByteLength, pngLimit),
                finalSidecar, Probe(finalSidecar, entry.SidecarByteLength, sidecarLimit));
        }

        private static FakeArtifactInspector MakeArtifactInspector(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationArtifactEntryObservation[] entries)
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            inspector.Snapshot = PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                inspector, operation, CaptureRunPublicationEvidenceStatus.MatchesExpected, 100, entries);
            return inspector;
        }

        // ---- Orchestration construction ----

        private static PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator MakeExecutionCoordinator()
        {
            return new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(
                new FakePublisher(), new FakeCommitter());
        }

        private static PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator MakeOrchestrator(
            IPngJsonCapturePublicationArtifactInspector inspector,
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator executionCoordinator)
        {
            return new PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator(inspector, executionCoordinator);
        }

        private PngJsonCapturePublicationArtifactRecoveryOrchestrationResult BuildCleanupResult(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            int entryCount,
            CaptureRunPublicationEvidenceStatus stagingStatus)
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                new PngJsonCapturePublicationArtifactEntryObservation[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                entries[i] = MakeIndexObservation(
                    token, operation, i,
                    stagingStatus, stagingStatus, EvMatchesExpected, EvMatchesExpected);
            }

            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator orchestrator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());
            return orchestrator.Execute(operation);
        }

        private static PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator MakeOrchestrationCoordinator()
        {
            return new PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator(
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(new FakeCleanup()));
        }

        private PngJsonCapturePublicationCaptureCompleteNotificationOperation BuildNotification(
            CaptureRunRootLayout layout,
            bool commitRoute,
            out PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult)
        {
            PngJsonCapturePublicationPlan plan = MakePlan(layout.TestRunId, InitId, HashA, new[] { MakeEntry(10) });
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(layout, plan, commitRoute, out _);
            cleanupResult = MakeOrchestrationCoordinator().Execute(
                BuildCleanupResult(authority, 1, EvMatchesExpected));
            return PngJsonCapturePublicationCaptureCompleteNotificationOperation.Create(cleanupResult);
        }

        // ---- Tests ----

        [Test]
        public void Constructor_NullSink_Rejected()
        {
            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    new PngJsonCapturePublicationCaptureCompleteNotifier(null)).ParamName,
                Is.EqualTo("sink"));
        }

        [Test]
        public void Notify_NullOperation_Rejected()
        {
            RecordingSink sink = new RecordingSink();
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            Assert.That(
                Assert.Throws<ArgumentNullException>(() => notifier.Notify(null)).ParamName,
                Is.EqualTo("operation"));
            Assert.That(sink.CallCount, Is.Zero);
        }

        [Test]
        public void Notify_InvalidOperation_Rejected_NoSinkContact()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult);
            SetField(cleanupResult, "_token", null);

            RecordingSink sink = new RecordingSink();
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            Assert.Throws<ArgumentException>(() => notifier.Notify(operation));
            Assert.That(sink.CallCount, Is.Zero);
        }

        [Test]
        public void Notify_Accepted_SinkOnce_ReceiptIssued()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            RecordingSink sink = new RecordingSink { Response = Accepted };
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt = notifier.Notify(operation);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(sink.CallCount, Is.EqualTo(1));
            Assert.That(ReferenceEquals(receipt.IssuedBy, notifier), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(receipt.IsIssuedFor(notifier, operation), Is.True);
        }

        [Test]
        public void Notify_AlreadyAccepted_SinkOnce_ReceiptIssued()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            RecordingSink sink = new RecordingSink { Response = AlreadyAccepted };
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt = notifier.Notify(operation);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(sink.CallCount, Is.EqualTo(1));
            Assert.That(receipt.IsIssuedFor(notifier, operation), Is.True);
        }

        [Test]
        public void Notify_IdentityConflict_NoReceipt_SinkOnce()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            RecordingSink sink = new RecordingSink { Response = IdentityConflict };
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            Assert.Throws<InvalidOperationException>(() => notifier.Notify(operation));
            Assert.That(sink.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Notify_Rejected_NoReceipt_SinkOnce()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            RecordingSink sink = new RecordingSink { Response = Rejected };
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            Assert.Throws<InvalidOperationException>(() => notifier.Notify(operation));
            Assert.That(sink.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Notify_None_NoReceipt_SinkOnce()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            RecordingSink sink = new RecordingSink
            {
                Response = PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.None
            };
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            Assert.Throws<InvalidOperationException>(() => notifier.Notify(operation));
            Assert.That(sink.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Notify_UndefinedValue_NoReceipt_SinkOnce()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            RecordingSink sink = new RecordingSink { Response = (PngJsonCapturePublicationCaptureCompleteNotificationAcceptance)99 };
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            Assert.Throws<InvalidOperationException>(() => notifier.Notify(operation));
            Assert.That(sink.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Notify_SinkException_SameInstancePropagated()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            IOException failure = new IOException("sink failure");
            RecordingSink sink = new RecordingSink { Thrown = failure };
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            IOException thrown = Assert.Throws<IOException>(() => notifier.Notify(operation));
            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(sink.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Notify_IdentityValues_ExactMatch()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            RecordingSink sink = new RecordingSink();
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            notifier.Notify(operation);

            PngJsonCapturePublicationCaptureCompleteNotificationIdentity identity = sink.Identities[0];
            Assert.That(identity.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(identity.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
            Assert.That(identity.RunManifestContentSha256, Is.EqualTo(operation.RunManifestContentSha256));
            Assert.That(identity.CaptureIndexPath, Is.EqualTo(operation.CaptureIndexPath));
        }

        [Test]
        public void Notify_SameIdentity_Renotify_AlreadyAccepted()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            IdempotentSink sink = new IdempotentSink();
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            PngJsonCapturePublicationCaptureCompleteNotificationReceipt first = notifier.Notify(operation);
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt second = notifier.Notify(operation);

            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(sink.CallCount, Is.EqualTo(2));
            Assert.That(second.IsIssuedFor(notifier, operation), Is.True);
        }

        [Test]
        public void Notify_SameTestRunId_DifferentIndexPath_Conflict()
        {
            CaptureRunRootLayout layoutA = MakeLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "D:\\final" : "/final",
                1);
            CaptureRunRootLayout layoutB = MakeLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "E:\\otherfinal" : "/otherfinal",
                1);

            PngJsonCapturePublicationCaptureCompleteNotificationOperation operationA =
                BuildNotification(layoutA, true, out _);
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operationB =
                BuildNotification(layoutB, true, out _);

            ConflictSink sink = new ConflictSink();
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt = notifier.Notify(operationA);
            Assert.That(receipt, Is.Not.Null);

            Assert.Throws<InvalidOperationException>(() => notifier.Notify(operationB));
            Assert.That(sink.CallCount, Is.EqualTo(2));
        }

        [Test]
        public void Identity_ConflictsWith_EachValueDifference()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            PngJsonCapturePublicationCaptureCompleteNotificationIdentity baseline =
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity.From(operation);

            PngJsonCapturePublicationCaptureCompleteNotificationIdentity otherInit =
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity.From(operation);
            SetField(otherInit, "_runInitializationId", "ffffffffffffffffffffffffffffffff");
            Assert.That(baseline.ConflictsWith(otherInit), Is.True);

            PngJsonCapturePublicationCaptureCompleteNotificationIdentity otherHash =
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity.From(operation);
            SetField(otherHash, "_runManifestContentSha256", HashB);
            Assert.That(baseline.ConflictsWith(otherHash), Is.True);

            PngJsonCapturePublicationCaptureCompleteNotificationIdentity otherPath =
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity.From(operation);
            SetField(otherPath, "_captureIndexPath", IsWindows ? "D:\\other\\capture.index" : "/other/capture.index");
            Assert.That(baseline.ConflictsWith(otherPath), Is.True);
        }

        [Test]
        public void Identity_SameIdentity_And_DifferentTestRunId_NoConflict()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);

            PngJsonCapturePublicationCaptureCompleteNotificationIdentity baseline =
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity.From(operation);
            PngJsonCapturePublicationCaptureCompleteNotificationIdentity same =
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity.From(operation);

            Assert.That(baseline.IsSameIdentity(same), Is.True);
            Assert.That(baseline.ConflictsWith(same), Is.False);

            PngJsonCapturePublicationCaptureCompleteNotificationOperation otherRun =
                BuildNotification(MakeDefaultLayout(2), true, out _);
            PngJsonCapturePublicationCaptureCompleteNotificationIdentity otherIdentity =
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity.From(otherRun);

            Assert.That(baseline.IsSameIdentity(otherIdentity), Is.False);
            Assert.That(baseline.ConflictsWith(otherIdentity), Is.False);
        }

        [Test]
        public void Identity_From_NullOperation_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity.From(null));
        }

        [Test]
        public void Notify_OwnerRelease_RejectedBeforeSink()
        {
            CaptureRunRootLayout layout = MakeDefaultLayout();
            PngJsonCapturePublicationPlan plan = MakePlan(layout.TestRunId, InitId, HashA, new[] { MakeEntry(10) });
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(layout, plan, true, out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult =
                MakeOrchestrationCoordinator().Execute(BuildCleanupResult(authority, 1, EvMatchesExpected));
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                PngJsonCapturePublicationCaptureCompleteNotificationOperation.Create(cleanupResult);

            owner.Dispose();
            _owners.Remove(owner);

            RecordingSink sink = new RecordingSink();
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);

            Assert.Throws<ArgumentException>(() => notifier.Notify(operation));
            Assert.That(sink.CallCount, Is.Zero);
        }

        [Test]
        public void Receipt_ForeignNotifierOrOtherOperation_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(MakeDefaultLayout(), true, out _);
            PngJsonCapturePublicationCaptureCompleteNotificationOperation other =
                BuildNotification(MakeDefaultLayout(), true, out _);

            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(new RecordingSink());
            PngJsonCapturePublicationCaptureCompleteNotifier foreign =
                new PngJsonCapturePublicationCaptureCompleteNotifier(new RecordingSink());

            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt = notifier.Notify(operation);

            Assert.That(receipt.IsIssuedFor(foreign, operation), Is.False);
            Assert.That(receipt.IsIssuedFor(notifier, other), Is.False);
            Assert.That(receipt.IsIssuedFor(null, operation), Is.False);
        }

        [Test]
        public void Shape_Notifier_InternalSealed_OneSinkField_NotDisposable()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteNotifier);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsClass, Is.True);
            Assert.That(typeof(IPngJsonCapturePublicationCaptureCompleteNotifier).IsAssignableFrom(type), Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].IsInitOnly, Is.True);
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(IPngJsonCapturePublicationCaptureCompleteNotificationSink)));
        }

        [Test]
        public void Shape_Identity_FourReadonlyFields_NoOperationOrGraphReference()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteNotificationIdentity);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True);
                Assert.That(field.FieldType == typeof(PngJsonCapturePublicationCaptureCompleteNotificationOperation), Is.False);
                Assert.That(field.FieldType == typeof(PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult), Is.False);
                Assert.That(field.FieldType == typeof(CaptureRunRootLayout), Is.False);
                Assert.That(field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease), Is.False);
            }
        }

        [Test]
        public void Shape_NoLeaseTokenStreamBytesOperationGraphHeld()
        {
            foreach (Type type in new[]
            {
                typeof(PngJsonCapturePublicationCaptureCompleteNotifier),
                typeof(PngJsonCapturePublicationCaptureCompleteNotificationIdentity)
            })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(
                        field.FieldType == typeof(CaptureRunLockLease)
                        || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || field.FieldType == typeof(Stream)
                        || field.FieldType == typeof(byte[])
                        || field.FieldType == typeof(PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult.ValidationToken)
                        || field.FieldType == typeof(PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken)
                        || field.FieldType == typeof(PngJsonCapturePublicationCaptureCompleteNotificationOperation)
                        || field.FieldType == typeof(PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult),
                        Is.False,
                        type.Name + "." + field.Name + " must not hold a lease, stream, bytes, token, or operation graph.");
                }
            }
        }

        [Test]
        public void Source_Notifier_NoDictionaryFilesystemClockRandomGuidThreadTask()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteNotifier.cs");

            Assert.That(source, Does.Not.Contain("new Dictionary"));
            Assert.That(source, Does.Not.Contain("Dictionary<"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("Guid"));
            Assert.That(source, Does.Not.Contain("Stopwatch"));
            Assert.That(source, Does.Not.Contain("Thread"));
            Assert.That(source, Does.Not.Contain("Task"));
        }

        [Test]
        public void Source_Notifier_SinkCallExactlyOnce_NoRetryLoop()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteNotifier.cs");

            Assert.That(CountSubstring(source, "_sink.Accept"), Is.EqualTo(1));
            Assert.That(source, Does.Not.Contain("for ("));
            Assert.That(source, Does.Not.Contain("while ("));
            Assert.That(source, Does.Not.Contain("do {"));
        }
    }
}
