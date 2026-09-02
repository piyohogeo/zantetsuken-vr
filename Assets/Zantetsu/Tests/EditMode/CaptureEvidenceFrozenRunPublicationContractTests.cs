using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureEvidenceFrozenRunPublicationContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

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

        private static CaptureRunRootLayout MakeLayout(long testRunId = 3)
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

        private static object GetField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return field.GetValue(target);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureEvidenceFrozenRunPublicationContractTests).Assembly.Location);
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

        private sealed class FakePlanStore : ICapturePublicationPlanStore
        {
            public int WritePlanCallCount { get; private set; }

            public CapturePublicationPlan LastPlan { get; private set; }

            public Func<CapturePublicationPlan, CapturePublicationPlanWriteReceipt> WriteHandler { get; set; }

            public CapturePublicationPlanWriteReceipt WritePlan(CapturePublicationPlan plan)
            {
                WritePlanCallCount++;
                LastPlan = plan;
                return WriteHandler?.Invoke(plan);
            }

            public CapturePublicationPlan ReadPlan(int maximumCanonicalByteCount)
            {
                throw new NotSupportedException();
            }

            public CapturePublicationPlan ReadOrRecoverPlan(int maximumCanonicalByteCount)
            {
                throw new NotSupportedException();
            }

            public bool DiscardInvalidTemporaryPlan(int maximumCanonicalByteCount)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class TempStore : IDisposable
        {
            private readonly string _sandbox;

            public TempStore(long testRunId = 3)
            {
                _sandbox = Path.Combine(Path.GetTempPath(), "zantetsu-frozen-pub-" + Guid.NewGuid().ToString("N"));
                string staging = Path.Combine(_sandbox, "staging");
                string final = Path.Combine(_sandbox, "final");
                Directory.CreateDirectory(staging);
                Directory.CreateDirectory(final);
                Layout = new CaptureRunRootLayout(staging, final, testRunId);
                Store = new CaptureArtifactFileStore(Layout);
                Coordinator = new CaptureEvidenceRunPublicationCoordinator(Store);
            }

            public CaptureRunRootLayout Layout { get; }

            public CaptureArtifactFileStore Store { get; }

            public CaptureEvidenceRunPublicationCoordinator Coordinator { get; }

            public void Dispose()
            {
                if (Directory.Exists(_sandbox))
                {
                    Directory.Delete(_sandbox, true);
                }
            }
        }

        // ---- Freeze receipt forging ----

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private CaptureRunInitializationSession MakeLifecycleSession(
            CaptureRunRootLayout layout,
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CaptureRunLockIdentityEvidence identity)
        {
            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator execution = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationExecutionReceipt receipt = execution.Execute(batch);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);

            CaptureRunInitializationSessionIssue issue = CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);
            return issue.Session;
        }

        private static CaptureFrameDraftRegistry ForgeDraftRegistry(long testRunId)
        {
            CaptureFrameDraftRegistry registry =
                (CaptureFrameDraftRegistry)FormatterServices.GetUninitializedObject(typeof(CaptureFrameDraftRegistry));
            CaptureDraftRunContext run =
                (CaptureDraftRunContext)FormatterServices.GetUninitializedObject(typeof(CaptureDraftRunContext));
            SetField(run, "<TestRunId>k__BackingField", testRunId);
            SetField(registry, "_run", run);
            SetField(registry, "_pendingCount", 0);
            SetField(registry, "_reservationCount", 0);
            return registry;
        }

        private static CaptureArtifactRegistry ForgeArtifactRegistry()
        {
            CaptureArtifactRegistry registry =
                (CaptureArtifactRegistry)FormatterServices.GetUninitializedObject(typeof(CaptureArtifactRegistry));
            SetField(registry, "_reservedArtifactCount", 0);
            return registry;
        }

        private static CaptureEvidenceRunFreezeReceipt ForgeFreezeReceipt(
            CaptureRunInitializationSession session,
            CaptureRunLockIdentityEvidence lockIdentityEvidence,
            CaptureFrameDraftRegistry drafts,
            CaptureArtifactRegistry artifacts)
        {
            long testRunId = session.TestRunId;

            CaptureEvidenceDraftCoordinator evidence =
                (CaptureEvidenceDraftCoordinator)FormatterServices.GetUninitializedObject(typeof(CaptureEvidenceDraftCoordinator));
            SetField(evidence, "_drafts", drafts);
            SetField(evidence, "_artifacts", artifacts);
            SetField(evidence, "_drainStarted", true);
            SetField(evidence, "_queuedCancelled", true);
            SetField(evidence, "_joined", true);
            SetField(evidence, "_occupied", new bool[0]);

            TraceLogger logger = (TraceLogger)FormatterServices.GetUninitializedObject(typeof(TraceLogger));
            SetField(logger, "_testRunId", testRunId);

            TraceFlightRecorder recorder = (TraceFlightRecorder)FormatterServices.GetUninitializedObject(typeof(TraceFlightRecorder));
            SetField(recorder, "_state", TraceFlightRecorderState.Frozen);
            SetField(recorder, "_logger", logger);

            FreezeTerminalTraceBufferBuilder bufferBuilder =
                (FreezeTerminalTraceBufferBuilder)FormatterServices.GetUninitializedObject(typeof(FreezeTerminalTraceBufferBuilder));
            SetField(bufferBuilder, "_draftRegistry", drafts);

            CaptureFrameFreezeTerminalCoordinator issuedBy =
                (CaptureFrameFreezeTerminalCoordinator)FormatterServices.GetUninitializedObject(typeof(CaptureFrameFreezeTerminalCoordinator));
            SetField(issuedBy, "_recorder", recorder);
            SetField(issuedBy, "_bufferBuilder", bufferBuilder);

            FreezeTerminalTraceBuffer terminalBuffer =
                (FreezeTerminalTraceBuffer)FormatterServices.GetUninitializedObject(typeof(FreezeTerminalTraceBuffer));
            SetField(terminalBuffer, "_testRunId", testRunId);

            CaptureEvidenceRunFreezeReceipt receipt =
                (CaptureEvidenceRunFreezeReceipt)FormatterServices.GetUninitializedObject(typeof(CaptureEvidenceRunFreezeReceipt));
            SetField(receipt, "_issuedBy", issuedBy);
            SetField(receipt, "_evidence", evidence);
            SetField(receipt, "_runSession", session);
            SetField(receipt, "_lockIdentityEvidence", lockIdentityEvidence);
            SetField(receipt, "_terminalBuffer", terminalBuffer);
            return receipt;
        }

        private CaptureEvidenceRunFreezeReceipt MakeValidFreezeReceipt(CaptureRunRootLayout layout)
        {
            return MakeValidFreezeReceipt(layout, null, out _);
        }

        private CaptureEvidenceRunFreezeReceipt MakeValidFreezeReceipt(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeValidFreezeReceipt(layout, null, out owner);
        }

        private CaptureEvidenceRunFreezeReceipt MakeValidFreezeReceipt(
            CaptureRunRootLayout layout,
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunInitializationSession session = MakeLifecycleSession(layout, disposeLog, out owner, out CaptureRunLockIdentityEvidence identity);
            CaptureFrameDraftRegistry drafts = ForgeDraftRegistry(layout.TestRunId);
            CaptureArtifactRegistry artifacts = ForgeArtifactRegistry();
            return ForgeFreezeReceipt(session, identity, drafts, artifacts);
        }

        // ---- Store / coordinator forging ----

        private static CaptureArtifactFileStore ForgeStore(CaptureRunRootLayout layout)
        {
            CaptureArtifactFileStore store =
                (CaptureArtifactFileStore)FormatterServices.GetUninitializedObject(typeof(CaptureArtifactFileStore));
            SetField(store, "_rootLayout", layout);
            SetField(store, "_publicationPlanPath", Path.Combine(layout.StagingRunRoot, "publication.plan"));
            return store;
        }

        private static CaptureEvidenceRunPublicationCoordinator ForgeCoordinator(
            CaptureArtifactFileStore store,
            ICapturePublicationPlanStore planStore = null)
        {
            CaptureEvidenceRunPublicationCoordinator coordinator =
                (CaptureEvidenceRunPublicationCoordinator)FormatterServices.GetUninitializedObject(
                    typeof(CaptureEvidenceRunPublicationCoordinator));
            SetField(coordinator, "_store", store);
            SetField(coordinator, "_freshPublicationGate", new object());
            SetField(coordinator, "_recoveryReceiptAuthority", new object());

            CaptureEvidencePublicationCoordinator publication =
                (CaptureEvidencePublicationCoordinator)FormatterServices.GetUninitializedObject(
                    typeof(CaptureEvidencePublicationCoordinator));
            SetField(publication, "_planStore", planStore ?? new FakePlanStore());
            SetField(coordinator, "_publication", publication);

            return coordinator;
        }

        private static CapturePublicationPlanWriteReceipt ForgeWriteReceipt(
            ICapturePublicationPlanStore store,
            CapturePublicationPlan plan,
            string absolutePath = null,
            int byteCount = 1)
        {
            CapturePublicationPlanWriteReceipt receipt =
                (CapturePublicationPlanWriteReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CapturePublicationPlanWriteReceipt));
            SetField(receipt, "<IssuedBy>k__BackingField", store);
            SetField(receipt, "<Plan>k__BackingField", plan);
            SetField(receipt, "<AbsolutePath>k__BackingField", absolutePath);
            SetField(receipt, "<ByteCount>k__BackingField", byteCount);
            return receipt;
        }

        private static CaptureEvidenceRunPublicationCoordinator.IssuanceProof MintProof(
            CaptureEvidenceRunPublicationCoordinator coordinator,
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            CapturePublicationPlanWriteReceipt writeReceipt)
        {
            return new CaptureEvidenceRunPublicationCoordinator.IssuanceProof(
                coordinator,
                GetField(coordinator, "_freshPublicationGate"),
                freezeReceipt,
                writeReceipt,
                freezeReceipt.Drafts,
                freezeReceipt.Artifacts,
                freezeReceipt.LockIdentityEvidence);
        }

        private CaptureEvidenceFrozenRunPublicationResult MakeResult()
        {
            return MakeResult(out _);
        }

        private CaptureEvidenceFrozenRunPublicationResult MakeResult(
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureArtifactFileStore store = ForgeStore(layout);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout, out owner);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                layout.TestRunId,
                InitId,
                HashA,
                Array.Empty<CaptureArtifactDescriptor>(),
                Array.Empty<CaptureFrameEvidenceEntry>());
            CapturePublicationPlanWriteReceipt writeReceipt = new CapturePublicationPlanWriteReceipt(
                store, plan, store.PublicationPlanPath, 16);
            return CaptureEvidenceFrozenRunPublicationResult.Create(
                coordinator,
                MintProof(coordinator, freezeReceipt, writeReceipt),
                freezeReceipt,
                writeReceipt);
        }

        // ---- Coordinator ----

        [Test]
        public void Coordinator_NullFreezeReceipt_Rejected()
        {
            using (TempStore temp = new TempStore())
            {
                ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                    () => temp.Coordinator.PersistFrozenRun(null, HashA));
                Assert.That(ex.ParamName, Is.EqualTo("freezeReceipt"));
            }
        }

        [Test]
        public void Coordinator_NullHash_Rejected()
        {
            using (TempStore temp = new TempStore())
            {
                CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(temp.Layout);
                ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                    () => temp.Coordinator.PersistFrozenRun(freezeReceipt, null));
                Assert.That(ex.ParamName, Is.EqualTo("runManifestContentHash"));
            }
        }

        [Test]
        public void Coordinator_InvalidHash_Rejected()
        {
            using (TempStore temp = new TempStore())
            {
                CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(temp.Layout);

                ArgumentException ex1 = Assert.Throws<ArgumentException>(
                    () => temp.Coordinator.PersistFrozenRun(freezeReceipt, "A".PadRight(64, 'a')));
                Assert.That(ex1.ParamName, Is.EqualTo("runManifestContentHash"));

                ArgumentException ex2 = Assert.Throws<ArgumentException>(
                    () => temp.Coordinator.PersistFrozenRun(freezeReceipt, "abcd"));
                Assert.That(ex2.ParamName, Is.EqualTo("runManifestContentHash"));

                ArgumentException ex3 = Assert.Throws<ArgumentException>(
                    () => temp.Coordinator.PersistFrozenRun(freezeReceipt, HashA.Replace('a', 'g')));
                Assert.That(ex3.ParamName, Is.EqualTo("runManifestContentHash"));
            }
        }

        [Test]
        public void Coordinator_InvalidFreezeReceipt_Rejected()
        {
            using (TempStore temp = new TempStore())
            {
                CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(temp.Layout, out CaptureRunInitializationSessionOwnershipLease owner);
                Assert.That(owner.IsCreated, Is.True);
                owner.Dispose();

                Assert.That(freezeReceipt.IsValid, Is.False);
                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => temp.Coordinator.PersistFrozenRun(freezeReceipt, HashA));
                Assert.That(ex.ParamName, Is.EqualTo("freezeReceipt"));
            }
        }

        [Test]
        public void Coordinator_ForeignRootLayout_Rejected()
        {
            CaptureRunRootLayout layoutA = MakeLayout(3);
            CaptureRunRootLayout layoutB = MakeLayout(7);
            CaptureArtifactFileStore store = ForgeStore(layoutA);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store);
            CaptureEvidenceRunFreezeReceipt foreign = MakeValidFreezeReceipt(layoutB);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => coordinator.PersistFrozenRun(foreign, HashA));
            Assert.That(ex.ParamName, Is.EqualTo("freezeReceipt"));
        }

        [Test]
        public void Coordinator_Success_ReturnsExactCorrelatedResult()
        {
            using (TempStore temp = new TempStore())
            {
                CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(temp.Layout);
                CaptureEvidenceFrozenRunPublicationResult result = temp.Coordinator.PersistFrozenRun(freezeReceipt, HashA);

                Assert.That(result, Is.Not.Null);
                Assert.That(result.IsValid, Is.True);
                Assert.That(ReferenceEquals(result.IssuedBy, temp.Coordinator), Is.True);
                Assert.That(ReferenceEquals(result.Store, temp.Store), Is.True);
                Assert.That(ReferenceEquals(result.FreezeReceipt, freezeReceipt), Is.True);
                Assert.That(ReferenceEquals(result.PlanWriteReceipt.IssuedBy, temp.Store), Is.True);
                Assert.That(ReferenceEquals(result.Plan, result.PlanWriteReceipt.Plan), Is.True);
                Assert.That(ReferenceEquals(result.Drafts, freezeReceipt.Drafts), Is.True);
                Assert.That(ReferenceEquals(result.Artifacts, freezeReceipt.Artifacts), Is.True);
                Assert.That(ReferenceEquals(result.RunSession, freezeReceipt.RunSession), Is.True);
                Assert.That(ReferenceEquals(result.RootLayout, temp.Layout), Is.True);
                Assert.That(ReferenceEquals(result.LockIdentityEvidence, freezeReceipt.LockIdentityEvidence), Is.True);
                Assert.That(result.TestRunId, Is.EqualTo(temp.Layout.TestRunId));
                Assert.That(result.RunInitializationId, Is.EqualTo(freezeReceipt.RunInitializationId));
                Assert.That(result.RunManifestContentHash, Is.EqualTo(HashA));
                Assert.That(result.PublicationPlanPath, Is.EqualTo(temp.Store.PublicationPlanPath));
                Assert.That(result.CanonicalByteCount, Is.GreaterThan(0));
            }
        }

        [Test]
        public void Coordinator_PlanBuiltFromExactRegistriesAndHash()
        {
            using (TempStore temp = new TempStore())
            {
                CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(temp.Layout);
                CaptureEvidenceFrozenRunPublicationResult result = temp.Coordinator.PersistFrozenRun(freezeReceipt, HashA);

                Assert.That(result.Plan.TestRunId, Is.EqualTo(freezeReceipt.TestRunId));
                Assert.That(result.Plan.TestRunId, Is.EqualTo(freezeReceipt.Drafts.Run.TestRunId));
                Assert.That(result.Plan.RunInitializationId, Is.EqualTo(freezeReceipt.RunInitializationId));
                Assert.That(result.Plan.RunManifestContentHash, Is.EqualTo(HashA));
                Assert.That(result.Plan.ArtifactCount, Is.EqualTo(0));
                Assert.That(result.Plan.CaptureFrameEvidenceCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Coordinator_NullWriteReceipt_NoResult()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureArtifactFileStore store = ForgeStore(layout);
            FakePlanStore fake = new FakePlanStore();
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store, fake);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout);

            Assert.Throws<InvalidOperationException>(() => coordinator.PersistFrozenRun(freezeReceipt, HashA));
            Assert.That(fake.WritePlanCallCount, Is.EqualTo(1));
        }

        [Test]
        public void Coordinator_ForeignStoreWriteReceipt_NoResult()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureArtifactFileStore store = ForgeStore(layout);
            FakePlanStore fake = new FakePlanStore();
            CaptureArtifactFileStore otherStore = ForgeStore(MakeLayout(9));
            fake.WriteHandler = plan => new CapturePublicationPlanWriteReceipt(otherStore, plan, "C:\\other\\plan", 16);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store, fake);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout);

            Assert.Throws<InvalidOperationException>(() => coordinator.PersistFrozenRun(freezeReceipt, HashA));
        }

        [Test]
        public void Coordinator_ForeignPlanWriteReceipt_NoResult()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureArtifactFileStore store = ForgeStore(layout);
            FakePlanStore fake = new FakePlanStore();
            CapturePublicationPlan foreignPlan = new CapturePublicationPlan(
                9, InitId, HashA,
                Array.Empty<CaptureArtifactDescriptor>(),
                Array.Empty<CaptureFrameEvidenceEntry>());
            fake.WriteHandler = plan => new CapturePublicationPlanWriteReceipt(fake, foreignPlan, "C:\\other\\plan", 16);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store, fake);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout);

            Assert.Throws<InvalidOperationException>(() => coordinator.PersistFrozenRun(freezeReceipt, HashA));
        }

        [Test]
        public void Coordinator_StoreException_PropagatesSameInstance()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureArtifactFileStore store = ForgeStore(layout);
            InvalidOperationException expected = new InvalidOperationException("boom");
            FakePlanStore fake = new FakePlanStore();
            fake.WriteHandler = plan => throw expected;
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store, fake);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout);

            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
                () => coordinator.PersistFrozenRun(freezeReceipt, HashA));
            Assert.That(ReferenceEquals(actual, expected), Is.True);
        }

        [Test]
        public void Coordinator_StoreException_NoRetryNoFallbackNoOwnerDispose()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureArtifactFileStore store = ForgeStore(layout);
            List<string> disposeLog = new List<string>();
            FakePlanStore fake = new FakePlanStore();
            fake.WriteHandler = plan => throw new InvalidOperationException("boom");
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store, fake);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout, disposeLog, out CaptureRunInitializationSessionOwnershipLease owner);

            Assert.Throws<InvalidOperationException>(() => coordinator.PersistFrozenRun(freezeReceipt, HashA));

            Assert.That(fake.WritePlanCallCount, Is.EqualTo(1));
            Assert.That(freezeReceipt.IsValid, Is.True);
            Assert.That(freezeReceipt.RunSession.IsValid, Is.True);
            Assert.That(freezeReceipt.LockIdentityEvidence.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(disposeLog, Is.Empty);
        }

        // ---- Result ----

        [Test]
        public void Result_NormalForwardsAllValues()
        {
            CaptureEvidenceFrozenRunPublicationResult result = MakeResult();

            Assert.That(result.IsValid, Is.True);
            Assert.That(ReferenceEquals(result.Store, result.IssuedBy.Store), Is.True);
            Assert.That(ReferenceEquals(result.Plan, result.PlanWriteReceipt.Plan), Is.True);
            Assert.That(ReferenceEquals(result.Drafts, result.FreezeReceipt.Drafts), Is.True);
            Assert.That(ReferenceEquals(result.Artifacts, result.FreezeReceipt.Artifacts), Is.True);
            Assert.That(ReferenceEquals(result.RunSession, result.FreezeReceipt.RunSession), Is.True);
            Assert.That(ReferenceEquals(result.RootLayout, result.FreezeReceipt.RootLayout), Is.True);
            Assert.That(ReferenceEquals(result.LockIdentityEvidence, result.FreezeReceipt.LockIdentityEvidence), Is.True);
            Assert.That(result.TestRunId, Is.EqualTo(result.FreezeReceipt.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(result.FreezeReceipt.RunInitializationId));
            Assert.That(result.RunManifestContentHash, Is.EqualTo(HashA));
            Assert.That(result.PublicationPlanPath, Is.EqualTo(result.Store.PublicationPlanPath));
            Assert.That(result.CanonicalByteCount, Is.GreaterThan(0));
        }

        [Test]
        public void Result_SharedStoreDifferentCoordinator_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureArtifactFileStore store = ForgeStore(layout);
            CaptureEvidenceRunPublicationCoordinator first = ForgeCoordinator(store);
            CaptureEvidenceRunPublicationCoordinator second = ForgeCoordinator(store);

            CaptureEvidenceFrozenRunPublicationResult result = MakeResult();
            SetField(result, "_issuedBy", second);
            Assert.That(ReferenceEquals(result.Store, second.Store), Is.True);
            Assert.That(result.IsValid, Is.False);
            Assert.That(ReferenceEquals(first, second), Is.False);
        }

        [Test]
        public void Result_RecoveryAuthorityCannotMintProof_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureArtifactFileStore store = ForgeStore(layout);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                layout.TestRunId, InitId, HashA,
                Array.Empty<CaptureArtifactDescriptor>(),
                Array.Empty<CaptureFrameEvidenceEntry>());
            CapturePublicationPlanWriteReceipt writeReceipt = new CapturePublicationPlanWriteReceipt(
                store, plan, store.PublicationPlanPath, 16);

            // A proof minted with the recovery authority instead of the fresh
            // publication gate is rejected at construction.
            object recoveryAuthority = GetField(coordinator, "_recoveryReceiptAuthority");
            CaptureEvidenceRunPublicationCoordinator.IssuanceProof forged =
                new CaptureEvidenceRunPublicationCoordinator.IssuanceProof(
                    coordinator,
                    recoveryAuthority,
                    freezeReceipt,
                    writeReceipt,
                    freezeReceipt.Drafts,
                    freezeReceipt.Artifacts,
                    freezeReceipt.LockIdentityEvidence);

            Assert.Throws<InvalidOperationException>(
                () => CaptureEvidenceFrozenRunPublicationResult.Create(coordinator, forged, freezeReceipt, writeReceipt));
        }

        [Test]
        public void Result_FieldSubstitution_Rejected()
        {
            CaptureEvidenceFrozenRunPublicationResult result = MakeResult();
            CaptureEvidenceRunPublicationCoordinator other = ForgeCoordinator(ForgeStore(MakeLayout(9)));

            SetField(result, "_issuedBy", other);
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_ProofAndReceiptsSubstitution_Rejected()
        {
            CaptureEvidenceFrozenRunPublicationResult first = MakeResult();
            CaptureEvidenceFrozenRunPublicationResult second = MakeResult();

            CaptureEvidenceFrozenRunPublicationResult proofSwapped = MakeResult();
            SetField(proofSwapped, "_proof", GetField(second, "_proof"));
            Assert.That(proofSwapped.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult freezeReceiptSwapped = MakeResult();
            SetField(freezeReceiptSwapped, "_freezeReceipt", GetField(second, "_freezeReceipt"));
            Assert.That(freezeReceiptSwapped.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult writeReceiptSwapped = MakeResult();
            SetField(writeReceiptSwapped, "_writeReceipt", GetField(second, "_writeReceipt"));
            Assert.That(writeReceiptSwapped.IsValid, Is.False);

            Assert.That(first.IsValid, Is.True);
            Assert.That(second.IsValid, Is.True);
        }

        [Test]
        public void Result_SameCoordinatorCrossCallSubstitution_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureArtifactFileStore store = ForgeStore(layout);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                layout.TestRunId, InitId, HashA,
                Array.Empty<CaptureArtifactDescriptor>(),
                Array.Empty<CaptureFrameEvidenceEntry>());

            CaptureEvidenceRunFreezeReceipt freeze1 = MakeValidFreezeReceipt(layout);
            CaptureEvidenceRunFreezeReceipt freeze2 = MakeValidFreezeReceipt(layout);
            CapturePublicationPlanWriteReceipt write1 = new CapturePublicationPlanWriteReceipt(store, plan, store.PublicationPlanPath, 16);
            CapturePublicationPlanWriteReceipt write2 = new CapturePublicationPlanWriteReceipt(store, plan, store.PublicationPlanPath, 16);

            CaptureEvidenceFrozenRunPublicationResult first = CaptureEvidenceFrozenRunPublicationResult.Create(
                coordinator, MintProof(coordinator, freeze1, write1), freeze1, write1);
            CaptureEvidenceFrozenRunPublicationResult second = CaptureEvidenceFrozenRunPublicationResult.Create(
                coordinator, MintProof(coordinator, freeze2, write2), freeze2, write2);

            Assert.That(first.IsValid, Is.True);
            Assert.That(second.IsValid, Is.True);

            // Same coordinator, same run identity, same plan: only the exact
            // freeze receipt and write receipt references distinguish them, so
            // the per-call proof must reject cross-substitution.
            SetField(first, "_freezeReceipt", freeze2);
            Assert.That(first.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult writeReceiptSwapped = CaptureEvidenceFrozenRunPublicationResult.Create(
                coordinator, MintProof(coordinator, freeze2, write2), freeze2, write2);
            SetField(writeReceiptSwapped, "_writeReceipt", write1);
            Assert.That(writeReceiptSwapped.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult proofSwapped = CaptureEvidenceFrozenRunPublicationResult.Create(
                coordinator, MintProof(coordinator, freeze1, write1), freeze1, write1);
            SetField(proofSwapped, "_proof", GetField(second, "_proof"));
            Assert.That(proofSwapped.IsValid, Is.False);
        }

        [Test]
        public void Result_WriteReceiptInternalCorruption_False()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureArtifactFileStore store = ForgeStore(layout);

            CaptureEvidenceFrozenRunPublicationResult foreignStore = MakeResult();
            SetField(foreignStore.PlanWriteReceipt, "<IssuedBy>k__BackingField", ForgeStore(MakeLayout(9)));
            Assert.That(foreignStore.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult foreignPlan = MakeResult();
            SetField(foreignPlan.PlanWriteReceipt, "<Plan>k__BackingField", new CapturePublicationPlan(
                9, InitId, HashA,
                Array.Empty<CaptureArtifactDescriptor>(),
                Array.Empty<CaptureFrameEvidenceEntry>()));
            Assert.That(foreignPlan.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult badPath = MakeResult();
            SetField(badPath.PlanWriteReceipt, "<AbsolutePath>k__BackingField", "C:\\wrong\\plan");
            Assert.That(badPath.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult zeroBytes = MakeResult();
            SetField(zeroBytes.PlanWriteReceipt, "<ByteCount>k__BackingField", 0);
            Assert.That(zeroBytes.IsValid, Is.False);
        }

        [Test]
        public void Result_PlanCorruption_False()
        {
            CaptureEvidenceFrozenRunPublicationResult testRunId = MakeResult();
            SetField(testRunId.Plan, "_testRunId", 999L);
            Assert.That(testRunId.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult initId = MakeResult();
            SetField(initId.Plan, "_runInitializationId", "ffffffffffffffffffffffffffffffff");
            Assert.That(initId.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult manifestHash = MakeResult();
            SetField(manifestHash.Plan, "_runManifestContentHash", "A".PadRight(64, 'a'));
            Assert.That(manifestHash.IsValid, Is.False);
        }

        [Test]
        public void Result_DraftsArtifactsSessionIdentityCorruption_False()
        {
            CaptureEvidenceFrozenRunPublicationResult draftsNull = MakeResult();
            SetField(GetField(draftsNull.FreezeReceipt, "_evidence"), "_drafts", null);
            Assert.That(draftsNull.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult artifactsNull = MakeResult();
            SetField(GetField(artifactsNull.FreezeReceipt, "_evidence"), "_artifacts", null);
            Assert.That(artifactsNull.IsValid, Is.False);

            // Freeze receipt lock identity evidence nulled.
            CaptureEvidenceFrozenRunPublicationResult identityNull = MakeResult();
            SetField(identityNull.FreezeReceipt, "_lockIdentityEvidence", null);
            Assert.That(identityNull.IsValid, Is.False);

            // Foreign lock identity evidence swapped in. Its owner is also
            // registered for teardown.
            CaptureRunRootLayout layout9 = MakeLayout(9);
            MakeLifecycleSession(layout9, null, out _, out CaptureRunLockIdentityEvidence foreignIdentity);
            CaptureEvidenceFrozenRunPublicationResult identitySwapped = MakeResult();
            SetField(identitySwapped.FreezeReceipt, "_lockIdentityEvidence", foreignIdentity);
            Assert.That(identitySwapped.IsValid, Is.False);

            // Foreign session swapped in: its RootLayout/TestRunId no longer
            // match the retained lock identity evidence.
            CaptureRunInitializationSession foreignSession = MakeLifecycleSession(layout9, null, out _, out _);
            CaptureEvidenceFrozenRunPublicationResult sessionSwapped = MakeResult();
            SetField(sessionSwapped.FreezeReceipt, "_runSession", foreignSession);
            Assert.That(sessionSwapped.IsValid, Is.False);
        }

        [Test]
        public void Result_FreezeReceiptStructuralNullCorruption_False()
        {
            CaptureEvidenceFrozenRunPublicationResult evidenceNull = MakeResult();
            SetField(evidenceNull.FreezeReceipt, "_evidence", null);
            Assert.That(evidenceNull.IsValid, Is.False);

            CaptureEvidenceFrozenRunPublicationResult sessionNull = MakeResult();
            SetField(sessionNull.FreezeReceipt, "_runSession", null);
            Assert.That(sessionNull.IsValid, Is.False);
        }

        [Test]
        public void Result_ArtifactReservationReappears_False()
        {
            CaptureEvidenceFrozenRunPublicationResult result = MakeResult();
            SetField(result.Artifacts, "_reservedArtifactCount", 1);
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_OwnerDispose_False()
        {
            CaptureEvidenceFrozenRunPublicationResult result = MakeResult(out CaptureRunInitializationSessionOwnershipLease owner);
            Assert.That(result.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_Uninitialized_ConvergesFalse()
        {
            CaptureEvidenceFrozenRunPublicationResult result =
                (CaptureEvidenceFrozenRunPublicationResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureEvidenceFrozenRunPublicationResult));

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_AuthorityGetterAbsent()
        {
            Type type = typeof(CaptureEvidenceFrozenRunPublicationResult);

            Assert.That(type.GetProperty("Authority", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Null);
            Assert.That(type.GetProperty("Proof", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Null);
        }

        [Test]
        public void Result_TypeShape()
        {
            Type type = typeof(CaptureEvidenceFrozenRunPublicationResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.FieldType.IsValueType, Is.False, field.Name + " must be a reference field.");
            }

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            MethodInfo create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(create, Is.Not.Null);
            Assert.That(create.ReturnType, Is.EqualTo(typeof(CaptureEvidenceFrozenRunPublicationResult)));

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    prop.PropertyType == typeof(CaptureRunLockLease)
                    || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    prop.Name + " must not expose a raw or ownership lease.");
            }
        }

        [Test]
        public void FreezeReceipt_Shape_NonOwning_NoLeaseExposure()
        {
            Type type = typeof(CaptureEvidenceRunFreezeReceipt);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(
                    field.FieldType == typeof(CaptureRunLockLease)
                    || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    field.Name + " must not hold a raw or ownership lease.");
            }

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    prop.PropertyType == typeof(CaptureRunLockLease)
                    || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    prop.Name + " must not expose a raw or ownership lease.");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    method.ReturnType == typeof(CaptureRunLockLease)
                    || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    method.Name + " must not return a raw or ownership lease.");
            }

            // Identity evidence is forwarded; the session is a non-owning reference.
            Assert.That(type.GetProperty("LockIdentityEvidence", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Not.Null);
        }

        [Test]
        public void Coordinator_FreshAndRecoveryAuthoritiesDistinct()
        {
            using (TempStore temp = new TempStore())
            {
                object fresh = GetField(temp.Coordinator, "_freshPublicationGate");
                object recovery = GetField(temp.Coordinator, "_recoveryReceiptAuthority");
                Assert.That(fresh, Is.Not.Null);
                Assert.That(recovery, Is.Not.Null);
                Assert.That(ReferenceEquals(fresh, recovery), Is.False);
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Coordinator_Source_SinglePlanWriteAndSingleIsValid()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureEvidenceRunPublicationCoordinator.cs"));

            Assert.That(source, Does.Contain("internal CaptureEvidenceFrozenRunPublicationResult PersistFrozenRun"));
            Assert.That(source, Does.Not.Contain("CapturePublicationPlanWriteReceipt PersistFrozenRun"));
            Assert.That(CountOccurrences(source, "BuildAndPersist("), Is.EqualTo(1));
            Assert.That(CountOccurrences(source, "freezeReceipt.IsValid"), Is.EqualTo(1));
        }

        [Test]
        public void Result_Source_SinglePredicateAndNoFilesystem()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureEvidenceFrozenRunPublicationResult.cs"));

            // The factory performs only O(1) exact binding; the full predicate
            // is invoked once by IsValid (declaration + IsValid call), and the
            // freeze receipt is fully validated only inside that predicate.
            Assert.That(CountOccurrences(source, "IsCorrelated("), Is.EqualTo(2));
            Assert.That(CountOccurrences(source, "freezeReceipt.IsValid"), Is.EqualTo(1));
            Assert.That(CountOccurrences(source, "IsMintedByThis("), Is.EqualTo(2));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("Path."));
            Assert.That(source, Does.Not.Contain("using System.IO"));
            Assert.That(source, Does.Not.Contain("using System.Linq"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("Thread"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("Logger"));
            Assert.That(source, Does.Not.Contain("Notifier"));
            Assert.That(source, Does.Not.Contain("CleanupBackend"));
            Assert.That(source, Does.Not.Contain("Trace"));
            Assert.That(source, Does.Not.Contain("catch"));
        }
    }
}
