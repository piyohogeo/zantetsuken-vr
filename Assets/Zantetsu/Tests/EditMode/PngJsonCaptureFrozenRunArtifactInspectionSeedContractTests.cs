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
    public class PngJsonCaptureFrozenRunArtifactInspectionSeedContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

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

            string dir = Path.GetDirectoryName(typeof(PngJsonCaptureFrozenRunArtifactInspectionSeedContractTests).Assembly.Location);
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
            public FakeHandle(string lockPath, bool isCreated = true, List<string> disposeLog = null)
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

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, null);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, null);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private static CaptureRunInitializationSession MakeLifecycleSession(
            CaptureRunRootLayout layout,
            CaptureRunLockLease lease)
        {
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator execution = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationExecutionReceipt receipt = execution.Execute(batch);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return new CaptureRunInitializationSession(lease, evidence);
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
            SetField(receipt, "_terminalBuffer", terminalBuffer);
            return receipt;
        }

        private static CaptureEvidenceRunFreezeReceipt MakeValidFreezeReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunLockLease lease = MakeLease(layout);
            CaptureRunInitializationSession session = MakeLifecycleSession(layout, lease);
            CaptureFrameDraftRegistry drafts = ForgeDraftRegistry(layout.TestRunId);
            CaptureArtifactRegistry artifacts = ForgeArtifactRegistry();
            return ForgeFreezeReceipt(session, drafts, artifacts);
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
                freezeReceipt.RunSession,
                freezeReceipt.LockLease);
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

        private static CaptureEvidenceFrozenRunPublicationResult MakeFrozenResult(CapturePublicationPlan genericPlan)
        {
            CaptureRunRootLayout layout = MakeLayout(genericPlan.TestRunId);
            CaptureArtifactFileStore store = ForgeStore(layout);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout);
            CapturePublicationPlanWriteReceipt writeReceipt = new CapturePublicationPlanWriteReceipt(
                store, genericPlan, store.PublicationPlanPath, 16);
            return CaptureEvidenceFrozenRunPublicationResult.Create(
                coordinator,
                MintProof(coordinator, freezeReceipt, writeReceipt),
                freezeReceipt,
                writeReceipt);
        }

        private static PngJsonCaptureFrozenRunPublicationPlanBinding MakeBinding(params long[] frameIds)
        {
            CapturePublicationPlan genericPlan = MakeGenericPlan(3, frameIds);
            CaptureEvidenceFrozenRunPublicationResult frozen = MakeFrozenResult(genericPlan);
            return PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(frozen);
        }

        private static PngJsonCaptureFrozenRunArtifactInspectionSeed MakeSeed(params long[] frameIds)
        {
            return PngJsonCaptureFrozenRunArtifactInspectionSeedBuilder.Build(MakeBinding(frameIds));
        }

        // ---- Normal ----

        [Test]
        public void Seed_ZeroFrames_Constructs()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed();

            Assert.That(seed.IsValid, Is.True);
            Assert.That(seed.AuthoritativePlan.EntryCount, Is.EqualTo(0));
            Assert.That(seed.GenericPlan.ArtifactCount, Is.EqualTo(0));
        }

        [Test]
        public void Seed_MultipleFrames_Constructs()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1, 2, 3);

            Assert.That(seed.IsValid, Is.True);
            Assert.That(seed.AuthoritativePlan.EntryCount, Is.EqualTo(3));
        }

        [Test]
        public void Seed_ForwardsAllValues()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1, 2);
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = seed.PlanBinding;

            Assert.That(ReferenceEquals(seed.PlanBinding, binding), Is.True);
            Assert.That(ReferenceEquals(seed.FrozenPublicationResult, binding.FrozenPublicationResult), Is.True);
            Assert.That(ReferenceEquals(seed.GenericPlan, binding.GenericPlan), Is.True);
            Assert.That(ReferenceEquals(seed.AuthoritativePlan, binding.LegacyPlan), Is.True);
            Assert.That(ReferenceEquals(seed.FreezeReceipt, binding.FreezeReceipt), Is.True);
            Assert.That(ReferenceEquals(seed.Drafts, binding.Drafts), Is.True);
            Assert.That(ReferenceEquals(seed.Artifacts, binding.Artifacts), Is.True);
            Assert.That(ReferenceEquals(seed.RunSession, binding.RunSession), Is.True);
            Assert.That(ReferenceEquals(seed.RootLayout, binding.RootLayout), Is.True);
            Assert.That(ReferenceEquals(seed.LockLease, binding.LockLease), Is.True);
            Assert.That(seed.TestRunId, Is.EqualTo(binding.TestRunId));
            Assert.That(seed.RunInitializationId, Is.EqualTo(binding.RunInitializationId));
            Assert.That(seed.RunManifestContentSha256, Is.EqualTo(binding.RunManifestContentHash));
        }

        [Test]
        public void Seed_ExactRootLayoutLeaseSession()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);

            Assert.That(ReferenceEquals(seed.RootLayout, seed.FreezeReceipt.RootLayout), Is.True);
            Assert.That(ReferenceEquals(seed.LockLease, seed.RunSession.LockLease), Is.True);
            Assert.That(seed.RunSession.OwnsLockLease(seed.LockLease), Is.True);
        }

        [Test]
        public void Seed_DispositionFixed()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);

            Assert.That(
                seed.Disposition,
                Is.EqualTo(CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative));
        }

        [Test]
        public void Seed_PublicationPlanPathMatches()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);

            Assert.That(
                seed.PublicationPaths.PublicationPlanPath,
                Is.EqualTo(seed.FrozenPublicationResult.PublicationPlanPath));
            Assert.That(ReferenceEquals(seed.PublicationPaths.RootLayout, seed.RootLayout), Is.True);
        }

        // ---- Rejection ----

        [Test]
        public void Seed_NullBinding_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCaptureFrozenRunArtifactInspectionSeed.Create(null));
            Assert.That(ex.ParamName, Is.EqualTo("planBinding"));
        }

        [Test]
        public void Seed_InvalidBinding_Rejected()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1);
            binding.FrozenPublicationResult.RunSession.Dispose();

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunArtifactInspectionSeed.Create(binding));
        }

        [Test]
        public void Seed_ForeignRootLayout_Rejected()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1);
            CaptureRunRootLayout layout9 = MakeLayout(9);
            SetField(binding.FrozenPublicationResult.FreezeReceipt, "_runSession",
                MakeLifecycleSession(layout9, MakeLease(layout9)));

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunArtifactInspectionSeed.Create(binding));
        }

        [Test]
        public void Seed_BindingPlanCorruption_Rejected()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1);
            SetField(binding, "_legacyPlan", null);

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunArtifactInspectionSeed.Create(binding));
        }

        [Test]
        public void Seed_ManifestHashMismatch_Rejected()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1);
            SetField(binding.GenericPlan, "_runManifestContentHash", HashB);

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunArtifactInspectionSeed.Create(binding));
        }

        [Test]
        public void Seed_TestRunIdMismatch_Rejected()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1);
            SetField(binding.GenericPlan, "_testRunId", 999L);

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunArtifactInspectionSeed.Create(binding));
        }

        [Test]
        public void Seed_InitializationIdMismatch_Rejected()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1);
            SetField(binding.GenericPlan, "_runInitializationId", "ffffffffffffffffffffffffffffffff");

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunArtifactInspectionSeed.Create(binding));
        }

        [Test]
        public void Seed_FrozenPublicationPlanPathCorruption_Rejected()
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeBinding(1);
            SetField(binding.FrozenPublicationResult.Store, "_publicationPlanPath", "C:\\wrong\\plan");

            Assert.Throws<ArgumentException>(
                () => PngJsonCaptureFrozenRunArtifactInspectionSeed.Create(binding));
        }

        // ---- Issuance boundary / tamper ----

        [Test]
        public void Seed_PlanBindingNull_False()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);
            SetField(seed, "_planBinding", null);
            Assert.That(seed.IsValid, Is.False);
        }

        [Test]
        public void Seed_PublicationPathsNull_False()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);
            SetField(seed, "_publicationPaths", null);
            Assert.That(seed.IsValid, Is.False);
        }

        [Test]
        public void Seed_PathSetRootLayoutSwap_False()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);
            SetField(seed.PublicationPaths, "_rootLayout", MakeLayout(9));
            Assert.That(seed.IsValid, Is.False);
        }

        [Test]
        public void Seed_PathSetPathSwap_False()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);
            SetField(seed.PublicationPaths, "_publicationPlanPath", "C:\\wrong\\plan");
            Assert.That(seed.IsValid, Is.False);
        }

        [Test]
        public void Seed_FreezeReceiptStructuralNull_False()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);
            SetField(seed.FreezeReceipt, "_runSession", null);
            Assert.That(seed.IsValid, Is.False);
        }

        [Test]
        public void Seed_SessionLeaseDispose_False()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);
            Assert.That(seed.IsValid, Is.True);

            seed.RunSession.Dispose();
            Assert.That(seed.IsValid, Is.False);
        }

        [Test]
        public void Seed_Uninitialized_ConvergesFalse()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed =
                (PngJsonCaptureFrozenRunArtifactInspectionSeed)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCaptureFrozenRunArtifactInspectionSeed));

            Assert.That(seed.IsValid, Is.False);
        }

        // ---- Type / source shape ----

        [Test]
        public void Seed_TypeShape()
        {
            Type type = typeof(PngJsonCaptureFrozenRunArtifactInspectionSeed);

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
            // constructor.
            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            // The atomic factory takes only the plan binding, so no path set,
            // legacy plan, frozen result, or lease can be injected.
            MethodInfo create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(create, Is.Not.Null);
            Assert.That(create.ReturnType, Is.EqualTo(typeof(PngJsonCaptureFrozenRunArtifactInspectionSeed)));
            ParameterInfo[] parameters = create.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(PngJsonCaptureFrozenRunPublicationPlanBinding)));
        }

        [Test]
        public void Builder_HasNoFields()
        {
            Type type = typeof(PngJsonCaptureFrozenRunArtifactInspectionSeedBuilder);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(
                type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static),
                Is.Empty);
        }

        [Test]
        public void Seed_Source_SingleValidationAndNoForbiddenDependencies()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureFrozenRunArtifactInspectionSeed.cs"));

            AssertNoForbiddenDependencies(source);

            // The binding is the sole full-plan validation boundary: once in the
            // factory and once in IsValid, never a bare generic/legacy plan
            // validation, and the path set is built exactly once.
            Assert.That(CountOccurrences(source, "planBinding.IsValid"), Is.EqualTo(2));
            Assert.That(source, Does.Not.Contain("genericPlan.IsValid"));
            Assert.That(source, Does.Not.Contain("legacyPlan.IsValid"));
            Assert.That(CountOccurrences(source, "new CaptureRunPublicationPathSet("), Is.EqualTo(1));
        }

        // ---- Structure ----

        [Test]
        public void Seed_ThousandFrames_Correct()
        {
            const int frameCount = 1000;
            long[] frameIds = new long[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                frameIds[i] = i + 1;
            }

            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(frameIds);

            Assert.That(seed.IsValid, Is.True);
            Assert.That(seed.AuthoritativePlan.EntryCount, Is.EqualTo(frameCount));
            Assert.That(seed.GenericPlan.ArtifactCount, Is.EqualTo(frameCount * 2));
            Assert.That(
                seed.Disposition,
                Is.EqualTo(CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative));
            Assert.That(
                seed.PublicationPaths.PublicationPlanPath,
                Is.EqualTo(seed.FrozenPublicationResult.PublicationPlanPath));
        }
    }
}
