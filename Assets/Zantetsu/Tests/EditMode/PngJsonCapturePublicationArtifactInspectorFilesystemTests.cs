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
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class PngJsonCapturePublicationArtifactInspectorFilesystemTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

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

        private static (string sandbox, string staging, string final) MakeSandbox()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsuken-inspector-" + Guid.NewGuid().ToString("N"));
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

        private static void WriteBytes(string absolutePath, byte[] bytes)
        {
            string parent = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(absolutePath, bytes);
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

        private sealed class TrackingNoFollowOpener : ICaptureArtifactNoFollowOpener
        {
            private readonly ICaptureArtifactNoFollowOpener _inner = CaptureArtifactNoFollowOpen.Create();

            public bool Supported = true;

            public readonly List<(string root, string relativePath)> Opens = new List<(string root, string relativePath)>();

            public bool IsSupported => Supported;

            public CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath)
            {
                Opens.Add((root, relativePath));
                return _inner.TryOpen(root, relativePath);
            }
        }

        private sealed class AbsentNoFollowOpener : ICaptureArtifactNoFollowOpener
        {
            public int OpenCount;

            public bool IsSupported => true;

            public CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath)
            {
                OpenCount++;
                return CaptureArtifactNoFollowOpenResult.Of(CaptureArtifactNoFollowOpenStatus.Absent);
            }
        }

        private sealed class ThrowingNoFollowOpener : ICaptureArtifactNoFollowOpener
        {
            public bool IsSupported => true;

            public CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath)
            {
                throw new IOException("read failed");
            }
        }

        private sealed class RecordingFileStream : FileStream
        {
            public int MaxReadRequest { get; private set; }
            public int TotalRead { get; private set; }

            public RecordingFileStream(string path)
                : base(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            {
            }

            public override int Read(byte[] array, int offset, int count)
            {
                if (count > MaxReadRequest)
                {
                    MaxReadRequest = count;
                }

                int read = base.Read(array, offset, count);
                TotalRead += read;
                return read;
            }
        }

        private sealed class StreamInjectingNoFollowOpener : ICaptureArtifactNoFollowOpener
        {
            private readonly string _streamPath;
            private readonly FileStream _stream;

            public bool IsSupported => true;

            public StreamInjectingNoFollowOpener(string streamPath, FileStream stream)
            {
                _streamPath = streamPath;
                _stream = stream;
            }

            public CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath)
            {
                string full = Path.Combine(root, relativePath);
                if (string.Equals(Path.GetFullPath(full), Path.GetFullPath(_streamPath), StringComparison.OrdinalIgnoreCase))
                {
                    return CaptureArtifactNoFollowOpenResult.Opened(_stream, null);
                }

                return CaptureArtifactNoFollowOpenResult.Of(CaptureArtifactNoFollowOpenStatus.Absent);
            }
        }

        // ---- Recovery authority construction ----

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private static CaptureRunMarkerBinding MakeMarkerBinding(CaptureRunRootLayout layout)
        {
            return new CaptureRunMarkerBinding(
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
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, identity);
        }

        private CaptureRunPublicationRecoveryInspectionOperation MakeRecoveryInspectionOperation(
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout)
        {
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(layout, out owner),
                1000,
                4,
                64);
        }

        private static CaptureRunPublicationRecoveryInspectionSnapshot MakeRecoverySnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            PngJsonCapturePublicationPlan plan)
        {
            return new CaptureRunPublicationRecoveryInspectionSnapshot(
                issuedBy,
                operation,
                new CaptureRunPublicationDocumentObservation(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent, 0, null),
                new CaptureRunPublicationDocumentObservation(PublicationPlan, DocCanonical, 100, plan),
                new CaptureRunPublicationDocumentObservation(CaptureRunPublicationDocumentKind.CaptureIndexTemporary, DocAbsent, 0, null),
                new CaptureRunPublicationDocumentObservation(CaptureIndex, DocAbsent, 0, null),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            PngJsonCapturePublicationPlan plan,
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation inspection = MakeRecoveryInspectionOperation(out owner, layout);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeRecoverySnapshot(inspector, inspection, plan);
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(
                CaptureRunPublicationRecoveryClassifier.Classify(snapshot));
        }

        // ---- Plan / entry ----

        private static PngJsonCapturePublicationPlanEntry MakeEntry(
            long captureFrameId,
            long pngByteLength,
            long sidecarByteLength,
            string pngContentSha256,
            string sidecarContentSha256)
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
                pngContentSha256,
                sidecarContentSha256);
        }

        private static PngJsonCapturePublicationPlan MakePlan(
            long testRunId,
            string runManifestContentSha256,
            PngJsonCapturePublicationPlanEntry[] entries)
        {
            return new PngJsonCapturePublicationPlan(testRunId, InitId, runManifestContentSha256, entries);
        }

        private static PngJsonCapturePublicationArtifactInspectionOperation MakeOperation(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            long maximumPngByteCount = 1000)
        {
            return PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, maximumPngByteCount);
        }

        // ---- Manifest / artifact construction ----

        private static TraceRunManifest MakeManifest(long testRunId = 1)
        {
            TraceRunContext context = new TraceRunContext(
                testRunId,
                1000,
                "build-1",
                "6000.3.22f1",
                ValidSha256,
                "scene-1",
                12345,
                0.02,
                3,
                "High",
                1,
                new Vector3(0f, -4.9f, 0f));

            TraceLogger logger = new TraceLogger(1);
            try
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
                recorder.TryTrigger();
                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
                return TraceRunManifest.Create(snapshot, context);
            }
            finally
            {
                logger.Dispose();
            }
        }

        private static CaptureRunReference MakeRun(TraceRunManifest manifest)
        {
            return new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, captureFrameId, 30, 1, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(
                context,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecord MakeRecord(CaptureRunReference run, CaptureFrameRequest request)
        {
            return new CaptureFrameRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
        }

        private static CaptureFramePngSaveReceipt MakeReceipt(string path, int byteCount, string hash)
        {
            ConstructorInfo ctor = typeof(CaptureFramePngSaveReceipt).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(int), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFramePngSaveReceipt)ctor.Invoke(new object[] { path, byteCount, hash });
        }

        private static CaptureFramePngArtifact MakeArtifact(
            TraceRunManifest manifest,
            long captureFrameId,
            byte[] pngBytes)
        {
            CaptureRunReference run = MakeRun(manifest);
            CaptureFrameRequest request = MakeRequest(captureFrameId);
            CaptureFrameRecord record = MakeRecord(run, request);
            CaptureFramePngSaveReceipt receipt = MakeReceipt(@"C:\capture\out.png", pngBytes.Length, Sha256(pngBytes));
            return new CaptureFramePngArtifact(record, request, receipt);
        }

        private static CaptureFramePngArtifact MakeArtifactWithPose(
            TraceRunManifest manifest,
            long captureFrameId,
            byte[] pngBytes,
            float poseX)
        {
            CaptureRunReference run = MakeRun(manifest);
            CaptureFrameRequest request = MakeRequest(captureFrameId);
            CaptureFrameRecord record = new CaptureFrameRecord(
                run, request, MakeTiming(),
                MakePose(poseX, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
            CaptureFramePngSaveReceipt receipt = MakeReceipt(@"C:\capture\out.png", pngBytes.Length, Sha256(pngBytes));
            return new CaptureFramePngArtifact(record, request, receipt);
        }

        // ---- Pre-side-effect rejection ----

        [Test]
        public void Inspect_NullOperation_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            PngJsonCapturePublicationArtifactInspector inspector =
                new PngJsonCapturePublicationArtifactInspector(Path.Combine(sandbox, "bundle"));

            Assert.Throws<ArgumentNullException>(() => inspector.Inspect(null));
        }

        [Test]
        public void Inspect_InvalidOperation_Rejected_NoFilesystemContact()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(MakePlan(1, HashA, new[] { MakeEntry(10, 16, 32, HashA, HashA) }), layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            PngJsonCapturePublicationArtifactInspectionOperation corrupted =
                (PngJsonCapturePublicationArtifactInspectionOperation)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionOperation));

            TrackingNoFollowOpener opener = new TrackingNoFollowOpener();
            PngJsonCapturePublicationArtifactInspector inspector =
                new PngJsonCapturePublicationArtifactInspector(Path.Combine(sandbox, "bundle"), opener, new CaptureArtifactVerificationBufferPool(4096));

            Assert.Throws<ArgumentException>(() => inspector.Inspect(corrupted));
            Assert.That(opener.Opens, Is.Empty);
        }

        [Test]
        public void Inspect_UnsupportedOpener_Rejected_NoFilesystemContact()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(MakePlan(1, HashA, new[] { MakeEntry(10, 16, 32, HashA, HashA) }), layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            TrackingNoFollowOpener opener = new TrackingNoFollowOpener { Supported = false };
            PngJsonCapturePublicationArtifactInspector inspector =
                new PngJsonCapturePublicationArtifactInspector(Path.Combine(sandbox, "bundle"), opener, new CaptureArtifactVerificationBufferPool(4096));

            Assert.Throws<CaptureArtifactNoFollowUnavailableException>(() => inspector.Inspect(operation));
            Assert.That(opener.Opens, Is.Empty);
        }

        [Test]
        public void Inspect_BufferUnavailable_Rejected_NoFilesystemContact()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(MakePlan(1, HashA, new[] { MakeEntry(10, 16, 32, HashA, HashA) }), layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(4096);
            CaptureArtifactVerificationBufferPool.Lease held = pool.TryRent();
            Assert.That(held, Is.Not.Null);
            try
            {
                TrackingNoFollowOpener opener = new TrackingNoFollowOpener();
                PngJsonCapturePublicationArtifactInspector inspector =
                    new PngJsonCapturePublicationArtifactInspector(Path.Combine(sandbox, "bundle"), opener, pool);

                Assert.Throws<CaptureArtifactVerificationDeferredException>(() => inspector.Inspect(operation));
                Assert.That(opener.Opens, Is.Empty);
            }
            finally
            {
                pool.Return(held);
            }
        }

        // ---- Normal path ----

        [Test]
        public void Inspect_TraceManifestAndArtifactsMatch_AllMatches()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
            string pngHash = Sha256(png);

            CaptureFramePngArtifact artifact = MakeArtifact(manifest, 10, png);
            byte[] sidecar = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
            string sidecarHash = Sha256(sidecar);

            PngJsonCapturePublicationPlanEntry entry = MakeEntry(10, png.LongLength, sidecar.LongLength, pngHash, sidecarHash);
            PngJsonCapturePublicationPlan plan = MakePlan(1, manifestHash, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(plan, layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage"), png);
            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.json.stage"), sidecar);
            WriteBytes(Path.Combine(layout.FinalRunRoot, "frames", "10.png"), png);
            WriteBytes(Path.Combine(layout.FinalRunRoot, "frames", "10.json"), sidecar);

            PngJsonCapturePublicationArtifactInspector inspector = new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            Assert.That(snapshot, Is.Not.Null);
            Assert.That(ReferenceEquals(snapshot.IssuedBy, inspector), Is.True);
            Assert.That(ReferenceEquals(snapshot.Operation, operation), Is.True);
            Assert.That(snapshot.TraceManifestStatus, Is.EqualTo(EvMatchesExpected));
            Assert.That(snapshot.TraceManifestProbedByteCount, Is.EqualTo(manifestBytes.LongLength));
            Assert.That(snapshot.Count, Is.EqualTo(1));

            PngJsonCapturePublicationArtifactEntryObservation observation = snapshot.GetEntry(0);
            Assert.That(observation.StagingPngStatus, Is.EqualTo(EvMatchesExpected));
            Assert.That(observation.StagingSidecarStatus, Is.EqualTo(EvMatchesExpected));
            Assert.That(observation.FinalPngStatus, Is.EqualTo(EvMatchesExpected));
            Assert.That(observation.FinalSidecarStatus, Is.EqualTo(EvMatchesExpected));
        }

        [Test]
        public void Inspect_TraceManifestAbsent()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationPlanEntry entry = MakeEntry(10, 16, 32, HashA, HashA);
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(MakePlan(1, HashA, new[] { entry }), layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            Directory.CreateDirectory(bundleDirectory);

            PngJsonCapturePublicationArtifactInspector inspector = new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            Assert.That(snapshot.TraceManifestStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.Absent));
            Assert.That(snapshot.TraceManifestProbedByteCount, Is.EqualTo(0));
        }

        [Test]
        public void Inspect_TraceManifestTestRunIdMismatch()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(2); // different test run id
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            PngJsonCapturePublicationPlanEntry entry = MakeEntry(10, 16, 32, HashA, HashA);
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(MakePlan(1, HashA, new[] { entry }), layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            PngJsonCapturePublicationArtifactInspector inspector = new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            Assert.That(snapshot.TraceManifestStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.Mismatch));
            Assert.That(snapshot.TraceManifestProbedByteCount, Is.EqualTo(manifestBytes.LongLength));
        }

        [Test]
        public void Inspect_PngLengthMismatch()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            string pngHash = Sha256(png);

            PngJsonCapturePublicationPlanEntry entry = MakeEntry(10, png.LongLength + 5, 32, pngHash, HashA);
            PngJsonCapturePublicationPlan plan = MakePlan(1, manifestHash, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(plan, layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage"), png);

            PngJsonCapturePublicationArtifactInspector inspector = new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            Assert.That(snapshot.GetEntry(0).StagingPngStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.Mismatch));
            Assert.That(snapshot.GetEntry(0).StagingPngProbedByteCount, Is.EqualTo(png.LongLength));
        }

        [Test]
        public void Inspect_SidecarCanonicalMismatch()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            string pngHash = Sha256(png);

            byte[] badSidecar = new byte[] { 123, 34, 110, 111, 116, 125 }; // not canonical

            PngJsonCapturePublicationPlanEntry entry = MakeEntry(10, png.LongLength, badSidecar.LongLength, pngHash, Sha256(badSidecar));
            PngJsonCapturePublicationPlan plan = MakePlan(1, manifestHash, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(plan, layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.json.stage"), badSidecar);

            PngJsonCapturePublicationArtifactInspector inspector = new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            Assert.That(snapshot.GetEntry(0).StagingSidecarStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.Mismatch));
        }

        [Test]
        public void Inspect_SidecarContentSha256Mismatch_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
            string pngHash = Sha256(png);

            CaptureFramePngArtifact expectedArtifact = MakeArtifact(manifest, 10, png);
            byte[] expectedSidecar = CaptureFramePngArtifactCodec.SerializeCanonical(expectedArtifact);

            // Same run / frame / PNG but a different canonical sidecar: the
            // decoded frame id, PNG hash, PNG length, and run-manifest hash all
            // still match, so only the raw sidecar bytes differ.
            CaptureFramePngArtifact foreignArtifact = MakeArtifactWithPose(manifest, 10, png, 9f);
            byte[] foreignSidecar = CaptureFramePngArtifactCodec.SerializeCanonical(foreignArtifact);
            Assert.That(foreignSidecar, Is.Not.EqualTo(expectedSidecar));

            PngJsonCapturePublicationPlanEntry entry = MakeEntry(
                10, png.LongLength, foreignSidecar.LongLength, pngHash, Sha256(expectedSidecar));
            PngJsonCapturePublicationPlan plan = MakePlan(1, manifestHash, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(plan, layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage"), png);
            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.json.stage"), foreignSidecar);

            PngJsonCapturePublicationArtifactInspector inspector = new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            Assert.That(snapshot.GetEntry(0).StagingSidecarStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.Mismatch));
        }

        [Test]
        public void Inspect_SidecarByteLengthMismatch_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
            string pngHash = Sha256(png);

            CaptureFramePngArtifact artifact = MakeArtifact(manifest, 10, png);
            byte[] sidecar = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);

            // Declared sidecar byte length is one byte longer than the actual
            // file, so the observed read count can never match.
            PngJsonCapturePublicationPlanEntry entry = MakeEntry(
                10, png.LongLength, sidecar.LongLength + 1, pngHash, Sha256(sidecar));
            PngJsonCapturePublicationPlan plan = MakePlan(1, manifestHash, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(plan, layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.json.stage"), sidecar);

            PngJsonCapturePublicationArtifactInspector inspector = new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            Assert.That(snapshot.GetEntry(0).StagingSidecarStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.Mismatch));
        }

        [Test]
        public void Inspect_PngProbe_StopsAfterLimitPlusOne()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            // A 20-byte PNG with a declared length of 5: the bounded probe must
            // stop after exactly 6 bytes (limit + 1) instead of requesting the
            // whole buffer.
            byte[] png = new byte[20];
            for (int i = 0; i < png.Length; i++)
            {
                png[i] = (byte)(i + 1);
            }

            string pngPath = Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage");
            WriteBytes(pngPath, png);

            PngJsonCapturePublicationPlanEntry entry = MakeEntry(10, 5, 32, HashA, HashA);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(MakePlan(1, HashA, new[] { entry }), layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            RecordingFileStream stream = new RecordingFileStream(pngPath);
            StreamInjectingNoFollowOpener opener = new StreamInjectingNoFollowOpener(pngPath, stream);
            PngJsonCapturePublicationArtifactInspector inspector =
                new PngJsonCapturePublicationArtifactInspector(
                    Path.Combine(sandbox, "bundle"), opener, new CaptureArtifactVerificationBufferPool(4096));

            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            PngJsonCapturePublicationArtifactEntryObservation observation = snapshot.GetEntry(0);
            Assert.That(observation.StagingPngStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.LimitExceeded));
            Assert.That(observation.StagingPngProbedByteCount, Is.EqualTo(6));
            Assert.That(stream.MaxReadRequest, Is.LessThanOrEqualTo(6));
            Assert.That(stream.TotalRead, Is.EqualTo(6));
        }

        [Test]
        public void Inspect_AllArtifactsAbsent()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            PngJsonCapturePublicationPlanEntry entry = MakeEntry(10, 16, 32, HashA, HashA);
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(MakePlan(1, manifestHash, new[] { entry }), layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            PngJsonCapturePublicationArtifactInspector inspector = new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            PngJsonCapturePublicationArtifactEntryObservation observation = snapshot.GetEntry(0);
            Assert.That(observation.StagingPngStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.Absent));
            Assert.That(observation.StagingSidecarStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.Absent));
            Assert.That(observation.FinalPngStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.Absent));
            Assert.That(observation.FinalSidecarStatus, Is.EqualTo(CaptureRunPublicationEvidenceStatus.Absent));
        }

        [Test]
        public void Inspect_MultiEntry_AscendingOrder_SingleBufferRent()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            PngJsonCapturePublicationPlanEntry[] entries =
            {
                MakeEntry(10, 16, 32, HashA, HashA),
                MakeEntry(20, 16, 32, HashA, HashA),
                MakeEntry(30, 16, 32, HashA, HashA)
            };
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(MakePlan(1, manifestHash, entries), layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(4096);
            AbsentNoFollowOpener opener = new AbsentNoFollowOpener();
            PngJsonCapturePublicationArtifactInspector inspector =
                new PngJsonCapturePublicationArtifactInspector(bundleDirectory, opener, pool);

            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            Assert.That(snapshot.Count, Is.EqualTo(3));
            Assert.That(snapshot.GetEntry(0).CaptureFrameId, Is.EqualTo(10));
            Assert.That(snapshot.GetEntry(1).CaptureFrameId, Is.EqualTo(20));
            Assert.That(snapshot.GetEntry(2).CaptureFrameId, Is.EqualTo(30));

            // 1 manifest open + 3 entries x 4 artifacts.
            Assert.That(opener.OpenCount, Is.EqualTo(13));
            Assert.That(pool.OutstandingRentCount, Is.Zero);
        }

        [Test]
        public void Inspect_OpenFailure_NoSnapshot_BufferReturned()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            PngJsonCapturePublicationPlanEntry entry = MakeEntry(10, 16, 32, HashA, HashA);
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(MakePlan(1, HashA, new[] { entry }), layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(4096);
            ThrowingNoFollowOpener opener = new ThrowingNoFollowOpener();
            PngJsonCapturePublicationArtifactInspector inspector =
                new PngJsonCapturePublicationArtifactInspector(Path.Combine(sandbox, "bundle"), opener, pool);

            Assert.Throws<IOException>(() => inspector.Inspect(operation));
            Assert.That(pool.OutstandingRentCount, Is.Zero);
        }

        [Test]
        public void Inspect_SnapshotIssuedByAndOperationCorrelate()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            PngJsonCapturePublicationPlanEntry entry = MakeEntry(10, 16, 32, HashA, HashA);
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(MakePlan(1, manifestHash, new[] { entry }), layout, out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            PngJsonCapturePublicationArtifactInspector inspector = new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = inspector.Inspect(operation);

            Assert.That(ReferenceEquals(snapshot.IssuedBy, inspector), Is.True);
            Assert.That(ReferenceEquals(snapshot.Operation, operation), Is.True);
            Assert.That(snapshot.IsIssuedFor(inspector, operation), Is.True);
        }
    }
}
