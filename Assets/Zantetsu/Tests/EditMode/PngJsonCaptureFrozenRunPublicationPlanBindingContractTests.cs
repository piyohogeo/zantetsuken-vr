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
    public class PngJsonCaptureFrozenRunPublicationPlanBindingContractTests
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

            string dir = Path.GetDirectoryName(typeof(PngJsonCaptureFrozenRunPublicationPlanBindingContractTests).Assembly.Location);
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

        private static void AssertNoForbiddenDependencies(string source)
        {
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("Codec"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("ComputeHash"));
            Assert.That(source, Does.Not.Contain("using System.Linq"));
            Assert.That(source, Does.Not.Contain(".Where("));
            Assert.That(source, Does.Not.Contain(".Select("));
            Assert.That(source, Does.Not.Contain("using UnityEngine"));
            Assert.That(source, Does.Not.Contain("Logger"));
            Assert.That(source, Does.Not.Contain("Notifier"));
            Assert.That(source, Does.Not.Contain("TryAcquire"));
            Assert.That(source, Does.Not.Contain(".Dispose()"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("Thread"));
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

        private static CaptureEvidenceRunPublicationCoordinator ForgeCoordinator(CaptureArtifactFileStore store)
        {
            CaptureEvidenceRunPublicationCoordinator coordinator =
                (CaptureEvidenceRunPublicationCoordinator)FormatterServices.GetUninitializedObject(
                    typeof(CaptureEvidenceRunPublicationCoordinator));
            SetField(coordinator, "_store", store);
            SetField(coordinator, "_freshPublicationGate", new object());
            SetField(coordinator, "_recoveryReceiptAuthority", new object());
            return coordinator;
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

        // ---- Generic plan building ----

        private static CaptureArtifactDescriptor MakeImageDescriptor(long id)
        {
            string idStr = id.ToString(CultureInfo.InvariantCulture);
            return new CaptureArtifactDescriptor(
                "frame/" + idStr + "/image",
                CaptureArtifactKind.FrameImage,
                "image/png",
                1,
                "frames/" + idStr + ".png.stage",
                "frames/" + idStr + ".png",
                100 + id,
                HashA);
        }

        private static CaptureArtifactDescriptor MakeMetadataDescriptor(long id)
        {
            string idStr = id.ToString(CultureInfo.InvariantCulture);
            return new CaptureArtifactDescriptor(
                "frame/" + idStr + "/metadata",
                CaptureArtifactKind.FrameMetadata,
                "application/vnd.zantetsu.capture-frame+json",
                2,
                "frames/" + idStr + ".json.stage",
                "frames/" + idStr + ".json",
                200 + id,
                HashB);
        }

        private static CaptureFrameEvidenceEntry MakeFrameEvidence(long id)
        {
            string idStr = id.ToString(CultureInfo.InvariantCulture);
            return new CaptureFrameEvidenceEntry(
                id,
                new[] { "frame/" + idStr + "/image", "frame/" + idStr + "/metadata" });
        }

        private static CapturePublicationPlan MakeGenericPlan(long testRunId, long[] frameIds)
        {
            CaptureArtifactDescriptor[] descriptors = new CaptureArtifactDescriptor[frameIds.Length * 2];
            CaptureFrameEvidenceEntry[] evidence = new CaptureFrameEvidenceEntry[frameIds.Length];
            int d = 0;
            for (int i = 0; i < frameIds.Length; i++)
            {
                descriptors[d++] = MakeImageDescriptor(frameIds[i]);
                descriptors[d++] = MakeMetadataDescriptor(frameIds[i]);
                evidence[i] = MakeFrameEvidence(frameIds[i]);
            }

            Array.Sort(descriptors, (a, b) => string.CompareOrdinal(a.ArtifactId, b.ArtifactId));
            return new CapturePublicationPlan(testRunId, InitId, HashA, descriptors, evidence);
        }

        private CaptureEvidenceFrozenRunPublicationResult MakeFrozenResult(CapturePublicationPlan genericPlan)
        {
            return MakeFrozenResult(genericPlan, null, out _);
        }

        private CaptureEvidenceFrozenRunPublicationResult MakeFrozenResult(
            CapturePublicationPlan genericPlan,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeFrozenResult(genericPlan, null, out owner);
        }

        private CaptureEvidenceFrozenRunPublicationResult MakeFrozenResult(
            CapturePublicationPlan genericPlan,
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunRootLayout layout = MakeLayout(genericPlan.TestRunId);
            CaptureArtifactFileStore store = ForgeStore(layout);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout, disposeLog, out owner);
            CapturePublicationPlanWriteReceipt writeReceipt = new CapturePublicationPlanWriteReceipt(
                store, genericPlan, store.PublicationPlanPath, 16);
            return CaptureEvidenceFrozenRunPublicationResult.Create(
                coordinator,
                MintProof(coordinator, freezeReceipt, writeReceipt),
                freezeReceipt,
                writeReceipt);
        }

        private PngJsonCaptureFrozenRunPublicationPlanBinding MakeBinding(params long[] frameIds)
        {
            return MakeBinding(frameIds, out _);
        }

        private PngJsonCaptureFrozenRunPublicationPlanBinding MakeBinding(
            long[] frameIds,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CapturePublicationPlan genericPlan = MakeGenericPlan(3, frameIds);
            CaptureEvidenceFrozenRunPublicationResult frozen = MakeFrozenResult(genericPlan, out owner);
            return PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(frozen);
        }

        // ---- Builder: normal ----

        [Test]
        public void Builder_ZeroFrames_BuildsEmptyBinding()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding();

            Assert.That(binding.IsValid, Is.True);
            Assert.That(binding.LegacyPlan.EntryCount, Is.EqualTo(0));
            Assert.That(binding.GenericPlan.ArtifactCount, Is.EqualTo(0));
            Assert.That(binding.GenericPlan.CaptureFrameEvidenceCount, Is.EqualTo(0));
        }

        [Test]
        public void Builder_MultipleFrames_BuildsCorrectBinding()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1, 2, 3);

            Assert.That(binding.IsValid, Is.True);
            Assert.That(binding.LegacyPlan.EntryCount, Is.EqualTo(3));

            for (int i = 0; i < 3; i++)
            {
                PngJsonCapturePublicationPlanEntry entry = binding.LegacyPlan.GetEntry(i);
                Assert.That(entry.CaptureFrameId, Is.EqualTo(i + 1));
            }
        }

        [Test]
        public void Binding_ForwardsExactReferences()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1, 2);
            CaptureEvidenceFrozenRunPublicationResult frozen = binding.FrozenPublicationResult;

            Assert.That(ReferenceEquals(binding.GenericPlan, frozen.Plan), Is.True);
            Assert.That(ReferenceEquals(binding.FreezeReceipt, frozen.FreezeReceipt), Is.True);
            Assert.That(ReferenceEquals(binding.Drafts, frozen.Drafts), Is.True);
            Assert.That(ReferenceEquals(binding.Artifacts, frozen.Artifacts), Is.True);
            Assert.That(ReferenceEquals(binding.RunSession, frozen.RunSession), Is.True);
            Assert.That(ReferenceEquals(binding.RootLayout, frozen.RootLayout), Is.True);
            Assert.That(ReferenceEquals(binding.LockIdentityEvidence, frozen.LockIdentityEvidence), Is.True);
            Assert.That(binding.TestRunId, Is.EqualTo(frozen.TestRunId));
            Assert.That(binding.RunInitializationId, Is.EqualTo(frozen.RunInitializationId));
            Assert.That(binding.RunManifestContentHash, Is.EqualTo(frozen.RunManifestContentHash));
        }

        [Test]
        public void Binding_IdentityAndHashMatchAcrossPlans()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1, 2);

            Assert.That(binding.LegacyPlan.TestRunId, Is.EqualTo(binding.GenericPlan.TestRunId));
            Assert.That(binding.LegacyPlan.RunInitializationId, Is.EqualTo(binding.GenericPlan.RunInitializationId));
            Assert.That(binding.LegacyPlan.RunManifestContentSha256, Is.EqualTo(binding.GenericPlan.RunManifestContentHash));
            Assert.That(binding.GenericPlan.TestRunId, Is.EqualTo(binding.TestRunId));
            Assert.That(binding.GenericPlan.RunInitializationId, Is.EqualTo(binding.RunInitializationId));
            Assert.That(binding.GenericPlan.RunManifestContentHash, Is.EqualTo(binding.RunManifestContentHash));
        }

        [Test]
        public void Binding_FrameAscendingAndImageMetadataPair()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1, 2, 3);

            for (int i = 0; i < binding.LegacyPlan.EntryCount; i++)
            {
                PngJsonCapturePublicationPlanEntry entry = binding.LegacyPlan.GetEntry(i);
                CaptureFrameEvidenceEntry evidence = binding.GenericPlan.GetCaptureFrameEvidence(i);
                Assert.That(entry.CaptureFrameId, Is.EqualTo(evidence.CaptureFrameId));

                string id = entry.CaptureFrameId.ToString(CultureInfo.InvariantCulture);
                Assert.That(evidence.GetArtifactId(0), Is.EqualTo("frame/" + id + "/image"));
                Assert.That(evidence.GetArtifactId(1), Is.EqualTo("frame/" + id + "/metadata"));
            }
        }

        [Test]
        public void Binding_PathLengthHashTransfer()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(7, 42);

            PngJsonCapturePublicationPlanEntry first = binding.LegacyPlan.GetEntry(0);
            Assert.That(first.CaptureFrameId, Is.EqualTo(7));
            Assert.That(first.PngStagingRelativePath, Is.EqualTo("frames/7.png.stage"));
            Assert.That(first.PngFinalRelativePath, Is.EqualTo("frames/7.png"));
            Assert.That(first.SidecarStagingRelativePath, Is.EqualTo("frames/7.json.stage"));
            Assert.That(first.SidecarFinalRelativePath, Is.EqualTo("frames/7.json"));
            Assert.That(first.PngByteLength, Is.EqualTo(100 + 7));
            Assert.That(first.PngContentSha256, Is.EqualTo(HashA));
            Assert.That(first.SidecarByteLength, Is.EqualTo(200 + 7));
            Assert.That(first.SidecarContentSha256, Is.EqualTo(HashB));

            PngJsonCapturePublicationPlanEntry second = binding.LegacyPlan.GetEntry(1);
            Assert.That(second.CaptureFrameId, Is.EqualTo(42));
            Assert.That(second.PngByteLength, Is.EqualTo(100 + 42));
            Assert.That(second.SidecarByteLength, Is.EqualTo(200 + 42));
        }

        // ---- Builder: rejection ----

        [Test]
        public void Builder_NullFrozenResult_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(null));
            Assert.That(ex.ParamName, Is.EqualTo("frozenPublicationResult"));
        }

        [Test]
        public void Builder_InvalidFrozenResult_Rejected()
        {
            CaptureEvidenceFrozenRunPublicationResult frozen = MakeFrozenResult(MakeGenericPlan(3, new long[] { 1 }), out CaptureRunInitializationSessionOwnershipLease owner);
            Assert.That(owner.IsCreated, Is.True);
            owner.Dispose();

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(frozen));
        }

        [Test]
        public void Builder_ArtifactCountMismatch_Rejected()
        {
            CaptureArtifactDescriptor image1 = MakeImageDescriptor(1);
            CaptureArtifactDescriptor metadata1 = MakeMetadataDescriptor(1);
            CaptureArtifactDescriptor image2 = MakeImageDescriptor(2);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { image1, metadata1, image2 },
                new[]
                {
                    new CaptureFrameEvidenceEntry(1, new[] { "frame/1/image", "frame/1/metadata" }),
                    new CaptureFrameEvidenceEntry(2, new[] { "frame/2/image" })
                });

            CaptureEvidenceFrozenRunPublicationResult frozen = MakeFrozenResult(plan);
            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(frozen));
        }

        [Test]
        public void Builder_FrameZeroArtifacts_Rejected()
        {
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                Array.Empty<CaptureArtifactDescriptor>(),
                new[] { new CaptureFrameEvidenceEntry(1, Array.Empty<string>()) });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        [Test]
        public void Builder_FrameOneArtifact_Rejected()
        {
            CaptureArtifactDescriptor descriptor = MakeImageDescriptor(1);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { descriptor },
                new[] { new CaptureFrameEvidenceEntry(1, new[] { "frame/1/image" }) });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        [Test]
        public void Builder_FrameThreeArtifacts_Rejected()
        {
            CaptureArtifactDescriptor image = MakeImageDescriptor(1);
            CaptureArtifactDescriptor metadata = MakeMetadataDescriptor(1);
            CaptureArtifactDescriptor extra = new CaptureArtifactDescriptor(
                "frame/1/extra", CaptureArtifactKind.FrameImage, "image/png", 1,
                "frames/1.extra.stage", "frames/1.extra", 300, HashA);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { extra, image, metadata },
                new[] { new CaptureFrameEvidenceEntry(1, new[] { "frame/1/extra", "frame/1/image", "frame/1/metadata" }) });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        [Test]
        public void Builder_ArtifactIdMissing_Rejected()
        {
            CaptureArtifactDescriptor image = MakeImageDescriptor(1);
            CaptureArtifactDescriptor wrong = new CaptureArtifactDescriptor(
                "frame/1/wrong", CaptureArtifactKind.FrameMetadata,
                "application/vnd.zantetsu.capture-frame+json", 2,
                "frames/1.wrong.stage", "frames/1.wrong", 200, HashB);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { image, wrong },
                new[] { new CaptureFrameEvidenceEntry(1, new[] { "frame/1/image", "frame/1/wrong" }) });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        [Test]
        public void Builder_CrossFrameArtifactReference_Rejected()
        {
            CaptureArtifactDescriptor image1 = MakeImageDescriptor(1);
            CaptureArtifactDescriptor metadata1 = MakeMetadataDescriptor(1);
            CaptureArtifactDescriptor image2 = MakeImageDescriptor(2);
            CaptureArtifactDescriptor metadata2 = MakeMetadataDescriptor(2);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { image1, metadata1, image2, metadata2 },
                new[]
                {
                    new CaptureFrameEvidenceEntry(1, new[] { "frame/1/image", "frame/2/image" }),
                    new CaptureFrameEvidenceEntry(2, new[] { "frame/2/image", "frame/2/metadata" })
                });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        [Test]
        public void Builder_RunScopedOrSharedArtifact_Rejected()
        {
            CaptureArtifactDescriptor image = MakeImageDescriptor(1);
            CaptureArtifactDescriptor metadata = MakeMetadataDescriptor(1);
            CaptureArtifactDescriptor shared = new CaptureArtifactDescriptor(
                "run/shared", CaptureArtifactKind.FrameImage, "image/png", 1,
                "frames/shared.stage", "frames/shared", 300, HashA);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { image, metadata, shared },
                new[] { new CaptureFrameEvidenceEntry(1, new[] { "frame/1/image", "frame/1/metadata" }) });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        [Test]
        public void Builder_KindMismatch_Rejected()
        {
            CaptureArtifactDescriptor image = new CaptureArtifactDescriptor(
                "frame/1/image", CaptureArtifactKind.FrameMetadata, "image/png", 1,
                "frames/1.png.stage", "frames/1.png", 100, HashA);
            CaptureArtifactDescriptor metadata = MakeMetadataDescriptor(1);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { image, metadata },
                new[] { MakeFrameEvidence(1) });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        [Test]
        public void Builder_FormatIdMismatch_Rejected()
        {
            CaptureArtifactDescriptor image = new CaptureArtifactDescriptor(
                "frame/1/image", CaptureArtifactKind.FrameImage, "image/jpeg", 1,
                "frames/1.png.stage", "frames/1.png", 100, HashA);
            CaptureArtifactDescriptor metadata = MakeMetadataDescriptor(1);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { image, metadata },
                new[] { MakeFrameEvidence(1) });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        [Test]
        public void Builder_FormatVersionMismatch_Rejected()
        {
            CaptureArtifactDescriptor metadata = new CaptureArtifactDescriptor(
                "frame/1/metadata", CaptureArtifactKind.FrameMetadata,
                "application/vnd.zantetsu.capture-frame+json", 3,
                "frames/1.json.stage", "frames/1.json", 200, HashB);
            CaptureArtifactDescriptor image = MakeImageDescriptor(1);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { image, metadata },
                new[] { MakeFrameEvidence(1) });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        [Test]
        public void Builder_StagingPathMismatch_Rejected()
        {
            CaptureArtifactDescriptor image = new CaptureArtifactDescriptor(
                "frame/1/image", CaptureArtifactKind.FrameImage, "image/png", 1,
                "frames/1.png.tmp", "frames/1.png", 100, HashA);
            CaptureArtifactDescriptor metadata = MakeMetadataDescriptor(1);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { image, metadata },
                new[] { MakeFrameEvidence(1) });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        [Test]
        public void Builder_FinalPathMismatch_Rejected()
        {
            CaptureArtifactDescriptor metadata = new CaptureArtifactDescriptor(
                "frame/1/metadata", CaptureArtifactKind.FrameMetadata,
                "application/vnd.zantetsu.capture-frame+json", 2,
                "frames/1.json.stage", "frames/1.meta", 200, HashB);
            CaptureArtifactDescriptor image = MakeImageDescriptor(1);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3, InitId, HashA,
                new[] { image, metadata },
                new[] { MakeFrameEvidence(1) });

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(MakeFrozenResult(plan)));
        }

        // ---- Binding: corruption ----

        [Test]
        public void Binding_FieldNullCorruption_False()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1);

            SetField(binding, "_frozenPublicationResult", null);
            Assert.That(binding.IsValid, Is.False);
        }

        [Test]
        public void Binding_LegacyPlanNullCorruption_False()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1);

            SetField(binding, "_legacyPlan", null);
            Assert.That(binding.IsValid, Is.False);
        }

        [Test]
        public void Binding_EntryCorruption_False()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1, 2);

            SetField(binding.LegacyPlan, "_entries", new PngJsonCapturePublicationPlanEntry[1]);
            Assert.That(binding.IsValid, Is.False);
        }

        [Test]
        public void Binding_DescriptorCorruption_False()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1);

            SetField(binding.GenericPlan, "_artifactDescriptors", null);
            Assert.That(binding.IsValid, Is.False);
        }

        [Test]
        public void Binding_FreezeReceiptGraphCorruption_False()
        {
            // Freeze receipt lock identity evidence nulled.
            PngJsonCaptureFrozenRunPublicationPlanBinding identityNull = MakeBinding(1);
            SetField(identityNull.FreezeReceipt, "_lockIdentityEvidence", null);
            Assert.That(identityNull.IsValid, Is.False);

            // Foreign lock identity evidence swapped in.
            CaptureRunRootLayout layout9 = MakeLayout(9);
            MakeLifecycleSession(layout9, null, out _, out CaptureRunLockIdentityEvidence foreignIdentity);
            PngJsonCaptureFrozenRunPublicationPlanBinding identitySwapped = MakeBinding(1);
            SetField(identitySwapped.FreezeReceipt, "_lockIdentityEvidence", foreignIdentity);
            Assert.That(identitySwapped.IsValid, Is.False);

            // Foreign session swapped in.
            CaptureRunInitializationSession foreignSession = MakeLifecycleSession(layout9, null, out _, out _);
            PngJsonCaptureFrozenRunPublicationPlanBinding sessionSwapped = MakeBinding(1);
            SetField(sessionSwapped.FreezeReceipt, "_runSession", foreignSession);
            Assert.That(sessionSwapped.IsValid, Is.False);

            // Identity evidence internal ownership binding corrupted.
            PngJsonCaptureFrozenRunPublicationPlanBinding identityBroken = MakeBinding(1);
            SetField(identityBroken.FreezeReceipt.LockIdentityEvidence, "_ownershipLease", null);
            Assert.That(identityBroken.IsValid, Is.False);

            // Frozen publication result swapped in from a different binding.
            PngJsonCaptureFrozenRunPublicationPlanBinding first = MakeBinding(1);
            PngJsonCaptureFrozenRunPublicationPlanBinding second = MakeBinding(2);
            SetField(first, "_frozenPublicationResult", second.FrozenPublicationResult);
            Assert.That(first.IsValid, Is.False);
        }

        [Test]
        public void Binding_OwnerDispose_False()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(new long[] { 1 }, out CaptureRunInitializationSessionOwnershipLease owner);
            Assert.That(binding.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();
            Assert.That(binding.IsValid, Is.False);
        }

        [Test]
        public void Binding_Uninitialized_ConvergesFalse()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding =
                (PngJsonCaptureFrozenRunPublicationPlanBinding)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCaptureFrozenRunPublicationPlanBinding));

            Assert.That(binding.IsValid, Is.False);
        }

        [Test]
        public void Builder_DoesNotDisposeOwner()
        {
            List<string> disposeLog = new List<string>();
            CapturePublicationPlan genericPlan = MakeGenericPlan(3, new long[] { 1, 2 });
            CaptureEvidenceFrozenRunPublicationResult frozen = MakeFrozenResult(genericPlan, disposeLog, out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCaptureFrozenRunPublicationPlanBinding binding =
                PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(frozen);

            Assert.That(binding.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(disposeLog, Is.Empty, "The binding builder must not dispose the owner.");
        }

        // ---- Type / source shape ----

        [Test]
        public void Binding_TypeShape()
        {
            Type type = typeof(PngJsonCaptureFrozenRunPublicationPlanBinding);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.FieldType.IsValueType, Is.False, field.Name + " must be a reference field.");
            }

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            // Exactly one private assignment constructor; no public or internal
            // constructor accepts a legacy plan.
            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            // The atomic factory takes only the frozen result, so no legacy
            // plan can be injected from outside.
            MethodInfo create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(create, Is.Not.Null);
            Assert.That(create.ReturnType, Is.EqualTo(typeof(PngJsonCaptureFrozenRunPublicationPlanBinding)));
            ParameterInfo[] parameters = create.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureEvidenceFrozenRunPublicationResult)));

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    prop.PropertyType == typeof(CaptureRunLockLease)
                    || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    prop.Name + " must not expose a raw or ownership lease.");
            }

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    method.ReturnType == typeof(CaptureRunLockLease)
                    || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    method.Name + " must not return a raw or ownership lease.");
            }
        }

        [Test]
        public void Builder_HasNoFields()
        {
            Type type = typeof(PngJsonCaptureFrozenRunPublicationPlanBindingBuilder);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(
                type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static),
                Is.Empty);
        }

        [Test]
        public void BindingAndBuilder_Source_NoForbiddenDependencies()
        {
            string binding = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureFrozenRunPublicationPlanBinding.cs"));
            string builder = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.cs"));

            AssertNoForbiddenDependencies(binding);
            AssertNoForbiddenDependencies(builder);
        }

        // ---- Structure ----

        [Test]
        public void Builder_ThousandFrames_Correct()
        {
            const int frameCount = 1000;
            long[] frameIds = new long[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                frameIds[i] = i + 1;
            }

            CapturePublicationPlan genericPlan = MakeGenericPlan(3, frameIds);
            CaptureEvidenceFrozenRunPublicationResult frozen = MakeFrozenResult(genericPlan);
            PngJsonCaptureFrozenRunPublicationPlanBinding binding =
                PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(frozen);

            Assert.That(binding.IsValid, Is.True);
            Assert.That(binding.LegacyPlan.EntryCount, Is.EqualTo(frameCount));
            Assert.That(binding.GenericPlan.ArtifactCount, Is.EqualTo(frameCount * 2));

            PngJsonCapturePublicationPlanEntry first = binding.LegacyPlan.GetEntry(0);
            Assert.That(first.CaptureFrameId, Is.EqualTo(1));
            Assert.That(first.PngByteLength, Is.EqualTo(101));
            Assert.That(first.SidecarByteLength, Is.EqualTo(201));

            PngJsonCapturePublicationPlanEntry last = binding.LegacyPlan.GetEntry(frameCount - 1);
            Assert.That(last.CaptureFrameId, Is.EqualTo(frameCount));
            Assert.That(last.PngByteLength, Is.EqualTo(100 + frameCount));
            Assert.That(last.SidecarByteLength, Is.EqualTo(200 + frameCount));
        }
    }
}
