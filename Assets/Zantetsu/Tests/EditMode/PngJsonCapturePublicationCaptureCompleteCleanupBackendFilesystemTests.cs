using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class PngJsonCapturePublicationCaptureCompleteCleanupBackendFilesystemTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentKind CaptureIndexTemporary => CaptureRunPublicationDocumentKind.CaptureIndexTemporary;

        private static CaptureRunPublicationDocumentKind PublicationPlanTemporary => CaptureRunPublicationDocumentKind.PublicationPlanTemporary;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

        private static CaptureRunPublicationArtifactKind Png => CaptureRunPublicationArtifactKind.Png;

        private static CaptureRunPublicationArtifactKind Sidecar => CaptureRunPublicationArtifactKind.Sidecar;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _owners.Count - 1; i >= 0; i--)
            {
                _owners[i].Dispose();
            }

            _owners.Clear();

            for (int i = _sandboxes.Count - 1; i >= 0; i--)
            {
                string sandbox = _sandboxes[i];
                if (Directory.Exists(sandbox))
                {
                    Directory.Delete(sandbox, true);
                }
            }

            _sandboxes.Clear();
        }

        // ---- General helpers ----

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
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

        private static (string sandbox, string staging, string final) MakeSandbox()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsuken-cleanup-" + Guid.NewGuid().ToString("N"));
            string staging = Path.Combine(sandbox, "staging");
            string final = Path.Combine(sandbox, "final");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(final);
            return (sandbox, staging, final);
        }

        private static CaptureRunRootLayout MakeLayout(string stagingBase, string finalBase, long testRunId = 1)
        {
            return new CaptureRunRootLayout(stagingBase, finalBase, testRunId);
        }

        private static string Sha256(byte[] bytes)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }

            const string hex = "0123456789abcdef";
            char[] chars = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = hex[hash[i] >> 4];
                chars[i * 2 + 1] = hex[hash[i] & 15];
            }

            return new string(chars);
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

        private sealed class TrackingMemoryStream : MemoryStream
        {
            public TrackingMemoryStream(byte[] buffer)
                : base(buffer)
            {
            }

            public bool Disposed { get; private set; }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        private sealed class FakeCleanupFileSystem : ICaptureCompleteCleanupFileSystem
        {
            private readonly Dictionary<CaptureIndexCommitDirectory, string> _directoryPaths =
                new Dictionary<CaptureIndexCommitDirectory, string>();

            public bool Supported = true;

            public bool DirectoryFlushSupported = true;

            public bool EscapesRoot;

            public bool ThrowOnOpenDirectory;

            public bool ThrowOnFlushDirectory;

            public bool DirectoryEmpty = true;

            public CaptureIndexCommitFile OpenFile;

            public CaptureIndexFileOpenStatus OpenStatus = CaptureIndexFileOpenStatus.Opened;

            public readonly List<CaptureIndexCommitFile> DeletedFiles = new List<CaptureIndexCommitFile>();

            public readonly List<CaptureIndexCommitDirectory> DeletedDirectories = new List<CaptureIndexCommitDirectory>();

            public readonly Queue<CaptureIndexFileOpen> OpenResults = new Queue<CaptureIndexFileOpen>();

            public readonly List<string> OpenedDirectoryPaths = new List<string>();

            public readonly List<string> FlushedDirectoryPaths = new List<string>();

            public int FlushDirectoryCalls;

            public bool IsSupported => Supported;

            public bool IsDirectoryFlushSupported => DirectoryFlushSupported;

            public CaptureIndexDirectoryOpen TryOpenDirectory(string absolutePath)
            {
                if (ThrowOnOpenDirectory)
                {
                    return CaptureIndexDirectoryOpen.Of(CaptureIndexFileOpenStatus.IoFailure);
                }

                return CaptureIndexDirectoryOpen.Opened(OpenDirectory(absolutePath));
            }

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
            {
                if (ThrowOnOpenDirectory)
                {
                    throw new IOException("open failed");
                }

                OpenedDirectoryPaths.Add(absolutePath);
                CaptureIndexCommitDirectory directory = new CaptureIndexCommitDirectory(
                    new SafeFileHandle(IntPtr.Zero, true),
                    absolutePath,
                    "\\\\?\\" + absolutePath);
                _directoryPaths[directory] = absolutePath;
                return directory;
            }

            public CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name)
            {
                if (EscapesRoot)
                {
                    return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.EscapesRoot);
                }

                if (OpenResults.Count > 0)
                {
                    return OpenResults.Dequeue();
                }

                if (OpenStatus == CaptureIndexFileOpenStatus.Opened && OpenFile != null)
                {
                    return CaptureIndexFileOpen.Opened(OpenFile);
                }

                return CaptureIndexFileOpen.Of(OpenStatus);
            }

            public void Delete(CaptureIndexCommitFile file)
            {
                DeletedFiles.Add(file);
            }

            public void FlushDirectory(CaptureIndexCommitDirectory directory)
            {
                FlushDirectoryCalls++;
                FlushedDirectoryPaths.Add(
                    _directoryPaths.TryGetValue(directory, out string path) ? path : "<unknown>");
                if (ThrowOnFlushDirectory)
                {
                    throw new IOException("flush failed");
                }
            }

            public void DeleteDirectory(CaptureIndexCommitDirectory directory)
            {
                DeletedDirectories.Add(directory);
            }

            public bool IsDirectoryEmpty(CaptureIndexCommitDirectory directory)
            {
                return DirectoryEmpty;
            }
        }

        // ---- Lease ----

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
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
            return MakeRootObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CaptureRunInitializationOpenOutcome ForgeOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult result,
            CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            CaptureRunInitializationOpenOutcome outcome = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(outcome, "_orchestrationResult", result);
            SetField(outcome, "_sessionIssue", null);
            SetField(outcome, "_lockIdentityEvidence", lockIdentityEvidence);
            return outcome;
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout)
        {
            CaptureRunMarkerBinding binding = MakeMarkerBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            FakeInspector inspector = new FakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, identity);
        }

        private CaptureRunPublicationRecoveryInspectionOperation MakeRecoveryInspectionOperation(
            int maximumPlanBytes,
            int maximumEntryCount,
            int maximumPathBytes,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout)
        {
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(null, out owner, layout),
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
                publicationPlanTemporary ?? MakeDoc(PublicationPlanTemporary, DocAbsent),
                publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent),
                captureIndexTemporary ?? MakeDoc(CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private CaptureRunPublicationRecoveryDecision MakeDecision(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout)
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeRecoveryInspectionOperation(1000, 4, 64, out owner, layout);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, publicationPlanTemporary, captureIndexTemporary: captureIndexTemporary, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, publicationPlanTemporary, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan), captureIndexTemporary: captureIndexTemporary);
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(
                MakeDecision(plan ?? MakePlan(), indexAuthoritative, captureIndexTemporary, publicationPlanTemporary, out owner, layout));
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

        private static PngJsonCapturePublicationPlanEntry[] MakeEntries(int count)
        {
            PngJsonCapturePublicationPlanEntry[] entries = new PngJsonCapturePublicationPlanEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = MakeEntry(i + 1);
            }

            return entries;
        }

        private static PngJsonCapturePublicationPlan MakePlan(
            long testRunId = 1,
            PngJsonCapturePublicationPlanEntry[] entries = null)
        {
            return new PngJsonCapturePublicationPlan(
                testRunId,
                InitId,
                HashA,
                entries ?? new[] { MakeEntry(10) });
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
            PngJsonCapturePublicationArtifactEntryObservation[] entries,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            long traceCount = 100)
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            inspector.Snapshot = PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                inspector, operation, traceStatus, traceCount, entries);
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

            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator orchestrator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());
            return orchestrator.Execute(operation);
        }

        private PngJsonCapturePublicationArtifactRecoveryOrchestrationResult BuildCommitResult(
            CaptureRunRootLayout layout,
            int entryCount,
            CaptureRunPublicationEvidenceStatus stagingStatus,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(entryCount));
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, null, out owner, layout);
            return BuildCleanupResult(authority, entryCount, stagingStatus);
        }

        private PngJsonCapturePublicationArtifactRecoveryOrchestrationResult BuildCaptureCompleteResult(
            CaptureRunRootLayout layout,
            int entryCount,
            CaptureRunPublicationEvidenceStatus stagingStatus,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(entryCount));
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, true, null, null, out owner, layout);
            return BuildCleanupResult(authority, entryCount, stagingStatus);
        }

        private PngJsonCapturePublicationCaptureCompleteCleanupActionPlan BuildPlan(CaptureRunRootLayout layout, bool commitRoute)
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result =
                commitRoute
                    ? BuildCommitResult(layout, 1, EvMatchesExpected, out _)
                    : BuildCaptureCompleteResult(layout, 1, EvMatchesExpected, out _);
            return PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
        }

        private PngJsonCapturePublicationCaptureCompleteCleanupOperation BuildOperation(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan,
            int stepIndex)
        {
            CaptureRunPublicationPathSet publicationPaths =
                plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            return PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(
                plan, publicationPaths, markerPaths, stepIndex);
        }

        private static int FindStepIndex(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan,
            CaptureRunPublicationCaptureCompleteCleanupAction action,
            CaptureRunPublicationArtifactKind artifactKind = CaptureRunPublicationArtifactKind.None)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupStep step = plan.GetStep(i);
                if (step.Action == action && (artifactKind == CaptureRunPublicationArtifactKind.None || step.ArtifactKind == artifactKind))
                {
                    return i;
                }
            }

            return -1;
        }

        // ---- Filesystem writes ----

        private static void WriteBytes(string absolutePath, byte[] bytes)
        {
            string parent = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(absolutePath, bytes);
        }

        private static void WriteMarkers(CaptureRunRootLayout layout, bool stagingReady)
        {
            CaptureRunMarkerBinding binding = MakeMarkerBinding(layout);

            string stagingRoot = layout.StagingRunRoot;
            string finalRoot = layout.FinalRunRoot;
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(finalRoot);

            File.WriteAllBytes(
                Path.Combine(stagingRoot, "run.init"),
                CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.StagingInitialization));
            File.WriteAllBytes(
                Path.Combine(finalRoot, "run.init"),
                CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.FinalInitialization));

            if (stagingReady)
            {
                File.WriteAllBytes(
                    Path.Combine(stagingRoot, "run.ready"),
                    CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady));
                File.WriteAllBytes(
                    Path.Combine(finalRoot, "run.ready"),
                    CaptureRunReadyMarkerCodec.SerializeCanonical(binding.FinalReady));
            }
        }

        // ---- Normal paths: each action deletes exactly its target ----

        [Test]
        public void Execute_DeletePublicationPlanTemporary_DeletesOnlyTmp()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(1));
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, MakeDoc(PublicationPlanTemporary, DocCanonical, 100, plan), out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvAbsent));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            int stepIndex = FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary);
            Assert.That(stepIndex, Is.GreaterThanOrEqualTo(0));
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, stepIndex);

            string tmpPath = Path.Combine(layout.StagingRunRoot, "publication.plan.tmp");
            WriteBytes(tmpPath, PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan));

            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(File.Exists(tmpPath), Is.False);
        }

        [Test]
        public void Execute_DeleteCaptureIndexTemporary_DeletesOnlyTmp()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(1));
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, true, MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan), null, out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvAbsent));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            int stepIndex = FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary);
            Assert.That(stepIndex, Is.GreaterThanOrEqualTo(0));
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, stepIndex);

            byte[] canonical = PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan);
            string indexPath = Path.Combine(layout.FinalRunRoot, "capture.index");
            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            WriteBytes(indexPath, canonical);
            WriteBytes(tmpPath, canonical);

            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(File.Exists(tmpPath), Is.False);
            Assert.That(File.Exists(indexPath), Is.True);
        }

        [Test]
        public void Execute_DeleteStagingArtifact_DeletesOnlyTarget()
        {
            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] sidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };

            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationPlanEntry entry = new PngJsonCapturePublicationPlanEntry(
                10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json",
                png.LongLength, sidecar.LongLength, Sha256(png), Sha256(sidecar));
            PngJsonCapturePublicationPlan plan = new PngJsonCapturePublicationPlan(1, InitId, HashA, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, null, out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvMatchesExpected));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            int stepIndex = FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, Png);
            Assert.That(stepIndex, Is.GreaterThanOrEqualTo(0));
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, stepIndex);

            string pngPath = Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage");
            string sidecarPath = Path.Combine(layout.StagingRunRoot, "frames", "10.json.stage");
            WriteBytes(pngPath, png);
            WriteBytes(sidecarPath, sidecar);

            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(File.Exists(pngPath), Is.False);
            Assert.That(File.Exists(sidecarPath), Is.True);
        }

        [Test]
        public void Execute_RemoveStagingFramesRoot_RemovesEmptyFrames()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            int stepIndex = FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot);
            Assert.That(stepIndex, Is.GreaterThanOrEqualTo(0));
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, stepIndex);

            string framesPath = Path.Combine(layout.StagingRunRoot, "frames");
            Directory.CreateDirectory(framesPath);

            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(Directory.Exists(framesPath), Is.False);
        }

        [Test]
        public void Execute_DeletePublicationPlan_DeletesOnlyPlan()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            int stepIndex = FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan);
            Assert.That(stepIndex, Is.GreaterThanOrEqualTo(0));
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, stepIndex);

            byte[] canonical = PngJsonCapturePublicationPlanCodec.SerializeCanonical(operation.AuthoritativePlan);
            string indexPath = Path.Combine(layout.FinalRunRoot, "capture.index");
            string planPath = Path.Combine(layout.StagingRunRoot, "publication.plan");
            WriteBytes(indexPath, canonical);
            WriteBytes(planPath, canonical);

            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(File.Exists(planPath), Is.False);
            Assert.That(File.Exists(indexPath), Is.True);
        }

        [Test]
        public void Execute_DeleteStagingReadyMarker_DeletesOnlyReady()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            int stepIndex = FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker);
            Assert.That(stepIndex, Is.GreaterThanOrEqualTo(0));
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, stepIndex);

            WriteMarkers(layout, stagingReady: true);

            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, "run.ready")), Is.False);
            Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, "run.init")), Is.True);
        }

        [Test]
        public void Execute_DeleteStagingInitializationMarker_DeletesOnlyInit()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            int stepIndex = FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker);
            Assert.That(stepIndex, Is.GreaterThanOrEqualTo(0));
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, stepIndex);

            WriteMarkers(layout, stagingReady: false);

            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, "run.init")), Is.False);
        }

        [Test]
        public void Execute_RemoveStagingRunRoot_RemovesEmptyRoot()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            int stepIndex = FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot);
            Assert.That(stepIndex, Is.GreaterThanOrEqualTo(0));
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, stepIndex);

            Directory.CreateDirectory(layout.StagingRunRoot);

            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);

            // The run root's real parent - the directory that held its entry -
            // survives, and so does the trusted base root above it.
            Assert.That(Directory.Exists(Path.GetDirectoryName(layout.StagingRunRoot)), Is.True);
            Assert.That(Directory.Exists(layout.StagingTrustedBaseRoot), Is.True);

            // The final side is untouched: nothing was created there and
            // nothing was removed.
            Assert.That(Directory.Exists(layout.FinalTrustedBaseRoot), Is.True);
            Assert.That(Directory.Exists(layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Execute_RemoveStagingRunRoot_FlushesRealParentDirectory_NotTheTrustedBaseRoot()
        {
            CaptureRunRootLayout layout = MakeLayout("C:\\staging", "D:\\final", 1);
            string realParent = Path.GetDirectoryName(layout.StagingRunRoot);
            Assert.That(realParent, Is.Not.EqualTo(layout.StagingTrustedBaseRoot));

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot));

            FakeCleanupFileSystem fileSystem = new FakeCleanupFileSystem
            {
                OpenStatus = CaptureIndexFileOpenStatus.Absent,
                DirectoryEmpty = true,
            };
            PngJsonCapturePublicationCaptureCompleteCleanupBackend backend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout, fileSystem);

            Assert.That(backend.Execute(operation, token), Is.Not.Null);

            // The real parent is opened first - before the run root is deleted -
            // and it is the only directory flushed. The trusted base root is a
            // grandparent and does not hold the deleted entry.
            Assert.That(fileSystem.OpenedDirectoryPaths,
                Is.EqualTo(new[] { realParent, layout.StagingRunRoot }));
            Assert.That(fileSystem.FlushedDirectoryPaths, Is.EqualTo(new[] { realParent }));
            Assert.That(fileSystem.FlushedDirectoryPaths, Has.No.Member(layout.StagingTrustedBaseRoot));

            // Exactly one directory deletion, and it is the run root.
            Assert.That(fileSystem.DeletedDirectories, Has.Count.EqualTo(1));
            Assert.That(fileSystem.DeletedFiles, Is.Empty);
        }

        // ---- Pre-side-effect rejection ----

        [Test]
        public void Execute_NullOperation_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            Assert.Throws<ArgumentNullException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(null, null));
        }

        [Test]
        public void Execute_InvalidToken_RejectedBeforeSideEffect()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, 0);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan other = BuildPlan(layout, commitRoute: true);
            Assert.That(other.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken foreign), Is.True);

            Assert.Throws<ArgumentException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, foreign));
        }

        [Test]
        public void Execute_ForeignRootLayout_RejectedBeforeSideEffect()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            (string otherSandbox, string otherStaging, string otherFinal) = MakeSandbox();
            _sandboxes.Add(otherSandbox);
            CaptureRunRootLayout otherLayout = MakeLayout(otherStaging, otherFinal, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, 0);

            Assert.Throws<ArgumentException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(otherLayout).Execute(operation, token));
        }

        [Test]
        public void Execute_OwnerReleased_RejectedBeforeSideEffect()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result =
                BuildCommitResult(layout, 1, EvMatchesExpected, out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = BuildOperation(actionPlan, 0);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.Throws<ArgumentException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token));
        }

        // ---- Mismatch / missing / non-empty never change the target ----

        [Test]
        public void Execute_DeleteStagingArtifact_Missing_Rejected()
        {
            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] sidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };

            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationPlanEntry entry = new PngJsonCapturePublicationPlanEntry(
                10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json",
                png.LongLength, sidecar.LongLength, Sha256(png), Sha256(sidecar));
            PngJsonCapturePublicationPlan plan = new PngJsonCapturePublicationPlan(1, InitId, HashA, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, null, out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvMatchesExpected));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, Png));

            Assert.Throws<IOException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token));
        }

        [Test]
        public void Execute_DeleteStagingArtifact_HashMismatch_NotDeleted()
        {
            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] sidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };

            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationPlanEntry entry = new PngJsonCapturePublicationPlanEntry(
                10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json",
                png.LongLength, sidecar.LongLength, Sha256(png), Sha256(sidecar));
            PngJsonCapturePublicationPlan plan = new PngJsonCapturePublicationPlan(1, InitId, HashA, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, null, out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvMatchesExpected));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, Png));

            string pngPath = Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage");
            byte[] wrong = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 };
            WriteBytes(pngPath, wrong);

            Assert.Throws<InvalidDataException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token));
            Assert.That(File.Exists(pngPath), Is.True);
        }

        [Test]
        public void Execute_DeletePublicationPlanTemporary_Mismatch_NotDeleted()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(1));
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, MakeDoc(PublicationPlanTemporary, DocCanonical, 100, plan), out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvAbsent));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary));

            string tmpPath = Path.Combine(layout.StagingRunRoot, "publication.plan.tmp");
            WriteBytes(tmpPath, new byte[] { 1, 2, 3, 4 });

            Assert.Throws<InvalidDataException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token));
            Assert.That(File.Exists(tmpPath), Is.True);
        }

        [Test]
        public void Execute_DeleteCaptureIndexTemporary_IndexMismatch_NotDeleted()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(1));
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, true, MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan), null, out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvAbsent));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary));

            byte[] canonical = PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan);
            string indexPath = Path.Combine(layout.FinalRunRoot, "capture.index");
            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            WriteBytes(indexPath, new byte[] { 1, 2, 3 });
            WriteBytes(tmpPath, canonical);

            Assert.Throws<InvalidDataException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token));
            Assert.That(File.Exists(tmpPath), Is.True);
        }

        [Test]
        public void Execute_RemoveStagingFramesRoot_NonEmpty_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot));

            string framesPath = Path.Combine(layout.StagingRunRoot, "frames");
            Directory.CreateDirectory(framesPath);
            File.WriteAllBytes(Path.Combine(framesPath, "stray.txt"), new byte[] { 1 });

            Assert.Throws<IOException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token));
            Assert.That(Directory.Exists(framesPath), Is.True);
        }

        // ---- Marker re-verification ----

        [Test]
        public void Execute_DeleteStagingReadyMarker_InitMismatch_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker));

            WriteMarkers(layout, stagingReady: true);

            // Break the peer binding: rewrite the staging init marker so its
            // content hash no longer matches the ready marker's StagingInitSha256.
            CaptureRunMarkerBinding other = CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId, "11111111111111111111111111111111", layout.StagingRunRootSha256, layout.FinalRunRootSha256);
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, "run.init"),
                CaptureRunInitializationMarkerCodec.SerializeCanonical(other.StagingInitialization));

            Assert.Throws<InvalidDataException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token));
            Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, "run.ready")), Is.True);
        }

        [Test]
        public void Execute_DeleteStagingInitializationMarker_ReadyStillExists_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker));

            WriteMarkers(layout, stagingReady: true);

            Assert.Throws<IOException>(() =>
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout).Execute(operation, token));
            Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, "run.init")), Is.True);
        }

        // ---- Handle-bound identity, preflight, flush, and disposal ----

        [Test]
        public void Execute_DeleteStagingArtifact_SwappedFile_DeletesVerifiedIdentity()
        {
            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] sidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };
            CaptureRunRootLayout layout = MakeLayout("C:\\staging", "D:\\final", 1);

            PngJsonCapturePublicationPlanEntry entry = new PngJsonCapturePublicationPlanEntry(
                10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json",
                png.LongLength, sidecar.LongLength, Sha256(png), Sha256(sidecar));
            PngJsonCapturePublicationPlan plan = new PngJsonCapturePublicationPlan(1, InitId, HashA, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, null, out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvMatchesExpected));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, Png));

            FakeCleanupFileSystem fileSystem = new FakeCleanupFileSystem();
            fileSystem.OpenFile = new CaptureIndexCommitFile(null, new MemoryStream(png));
            PngJsonCapturePublicationCaptureCompleteCleanupBackend backend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout, fileSystem);

            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt = backend.Execute(operation, token);

            Assert.That(receipt, Is.Not.Null);
            // Only the exact verified file identity is deleted; a file swapped
            // into the path after the open is never re-opened or deleted.
            Assert.That(fileSystem.DeletedFiles, Has.Count.EqualTo(1));
            Assert.That(fileSystem.DeletedFiles[0], Is.SameAs(fileSystem.OpenFile));
        }

        [Test]
        public void Execute_RemoveStagingFramesRoot_ParentSwap_NoDelete()
        {
            CaptureRunRootLayout layout = MakeLayout("C:\\staging", "D:\\final", 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot));

            FakeCleanupFileSystem fileSystem = new FakeCleanupFileSystem { ThrowOnOpenDirectory = true };
            PngJsonCapturePublicationCaptureCompleteCleanupBackend backend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout, fileSystem);

            Assert.Throws<IOException>(() => backend.Execute(operation, token));
            Assert.That(fileSystem.DeletedDirectories, Is.Empty);
            Assert.That(fileSystem.DeletedFiles, Is.Empty);
        }

        [Test]
        public void Execute_FlushUnsupported_RejectedBeforeDelete()
        {
            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] sidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };
            CaptureRunRootLayout layout = MakeLayout("C:\\staging", "D:\\final", 1);

            PngJsonCapturePublicationPlanEntry entry = new PngJsonCapturePublicationPlanEntry(
                10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json",
                png.LongLength, sidecar.LongLength, Sha256(png), Sha256(sidecar));
            PngJsonCapturePublicationPlan plan = new PngJsonCapturePublicationPlan(1, InitId, HashA, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, null, out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvMatchesExpected));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, Png));

            FakeCleanupFileSystem fileSystem = new FakeCleanupFileSystem { DirectoryFlushSupported = false };
            PngJsonCapturePublicationCaptureCompleteCleanupBackend backend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout, fileSystem);

            Assert.Throws<CaptureArtifactNoFollowUnavailableException>(() => backend.Execute(operation, token));
            Assert.That(fileSystem.DeletedFiles, Is.Empty);
            Assert.That(fileSystem.DeletedDirectories, Is.Empty);
        }

        [Test]
        public void Execute_FlushFailureAfterDelete_NoReceipt_NoRollback()
        {
            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] sidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };
            CaptureRunRootLayout layout = MakeLayout("C:\\staging", "D:\\final", 1);

            PngJsonCapturePublicationPlanEntry entry = new PngJsonCapturePublicationPlanEntry(
                10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json",
                png.LongLength, sidecar.LongLength, Sha256(png), Sha256(sidecar));
            PngJsonCapturePublicationPlan plan = new PngJsonCapturePublicationPlan(1, InitId, HashA, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, null, out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvMatchesExpected));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, Png));

            FakeCleanupFileSystem fileSystem = new FakeCleanupFileSystem { ThrowOnFlushDirectory = true };
            fileSystem.OpenFile = new CaptureIndexCommitFile(null, new MemoryStream(png));
            PngJsonCapturePublicationCaptureCompleteCleanupBackend backend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout, fileSystem);

            Assert.Throws<IOException>(() => backend.Execute(operation, token));

            // The delete already ran and is never rolled back.
            Assert.That(fileSystem.DeletedFiles, Has.Count.EqualTo(1));
            Assert.That(fileSystem.DeletedFiles[0], Is.SameAs(fileSystem.OpenFile));
        }

        [Test]
        public void Execute_Receipt_CorrelatesExactly()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot));

            string framesPath = Path.Combine(layout.StagingRunRoot, "frames");
            Directory.CreateDirectory(framesPath);

            PngJsonCapturePublicationCaptureCompleteCleanupBackend backend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout);
            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt = backend.Execute(operation, token);

            Assert.That(ReferenceEquals(receipt.IssuedBy, backend), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(receipt.IsIssuedFor(backend, operation, token), Is.True);
        }

        [Test]
        public void Execute_DisposesOpenedFile_AfterDelete()
        {
            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] sidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };
            CaptureRunRootLayout layout = MakeLayout("C:\\staging", "D:\\final", 1);

            PngJsonCapturePublicationPlanEntry entry = new PngJsonCapturePublicationPlanEntry(
                10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json",
                png.LongLength, sidecar.LongLength, Sha256(png), Sha256(sidecar));
            PngJsonCapturePublicationPlan plan = new PngJsonCapturePublicationPlan(1, InitId, HashA, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, null, out _, layout);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCleanupResult(authority, 1, EvMatchesExpected));
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, Png));

            TrackingMemoryStream stream = new TrackingMemoryStream(png);
            FakeCleanupFileSystem fileSystem = new FakeCleanupFileSystem();
            fileSystem.OpenFile = new CaptureIndexCommitFile(null, stream);
            PngJsonCapturePublicationCaptureCompleteCleanupBackend backend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout, fileSystem);

            backend.Execute(operation, token);

            Assert.That(stream.Disposed, Is.True);
        }

        [Test]
        public void Execute_RemoveStagingRunRoot_ParentOpenFailure_NoDelete()
        {
            CaptureRunRootLayout layout = MakeLayout("C:\\staging", "D:\\final", 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot));

            // The real parent directory open (and its flush probe) fails before
            // the staging run root is deleted.
            FakeCleanupFileSystem fileSystem = new FakeCleanupFileSystem { ThrowOnOpenDirectory = true };
            PngJsonCapturePublicationCaptureCompleteCleanupBackend backend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout, fileSystem);

            Assert.Throws<IOException>(() => backend.Execute(operation, token));
            Assert.That(fileSystem.DeletedDirectories, Is.Empty);
        }

        [Test]
        public void Execute_ReadyMarker_FinalInitOpenFailure_DisposesStagingInit()
        {
            CaptureRunRootLayout layout = MakeLayout("C:\\staging", "D:\\final", 1);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = BuildPlan(layout, commitRoute: true);
            Assert.That(actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation =
                BuildOperation(actionPlan, FindStepIndex(actionPlan, CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker));

            CaptureRunMarkerBinding binding = MakeMarkerBinding(layout);
            byte[] readyBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady);
            byte[] stagingInitBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.StagingInitialization);

            TrackingMemoryStream readyStream = new TrackingMemoryStream(readyBytes);
            TrackingMemoryStream stagingInitStream = new TrackingMemoryStream(stagingInitBytes);

            FakeCleanupFileSystem fileSystem = new FakeCleanupFileSystem();
            fileSystem.OpenResults.Enqueue(CaptureIndexFileOpen.Opened(new CaptureIndexCommitFile(null, readyStream)));
            fileSystem.OpenResults.Enqueue(CaptureIndexFileOpen.Opened(new CaptureIndexCommitFile(null, stagingInitStream)));
            fileSystem.OpenResults.Enqueue(CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.IoFailure));

            PngJsonCapturePublicationCaptureCompleteCleanupBackend backend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout, fileSystem);

            Assert.Throws<IOException>(() => backend.Execute(operation, token));

            // The staging init stream/handle opened before the final init open
            // failure must still be disposed.
            Assert.That(stagingInitStream.Disposed, Is.True);
            Assert.That(readyStream.Disposed, Is.True);
        }
    }
}
