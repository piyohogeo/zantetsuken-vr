using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureEvidenceBackendAbstractionTests
    {
        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string InitializationId = "0123456789abcdef0123456789abcdef";

        [Test]
        public void CommonBoundary_IsBeforeReadbackAndFormatNeutral()
        {
            Type session = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability.ICaptureEvidenceSession");
            Assert.That(session, Is.Not.Null);
            MethodInfo submit = session.GetMethod("TrySubmit");
            Assert.That(submit, Is.Not.Null);
            ParameterInfo[] parameters = submit.GetParameters();
            Assert.That(parameters[0].ParameterType.Name, Is.EqualTo("CaptureFrameEnvelope"));
            Assert.That(parameters[1].ParameterType.Name, Is.EqualTo("CaptureSurfaceLease"));
            Assert.That(submit.ToString(), Does.Not.Contain("NativeArray"));

            string root = RepositoryRoot();
            string coordinator = File.ReadAllText(Path.Combine(root, "Assets/Zantetsu/Runtime/Observability/CaptureEvidenceCoordinator.cs"));
            string contract = File.ReadAllText(Path.Combine(root, "Assets/Zantetsu/Runtime/Observability/ICaptureEvidenceSession.cs"));
            string common = coordinator + contract;
            Assert.That(common, Does.Not.Contain("AsyncGPUReadback"));
            Assert.That(common, Does.Not.Contain("NativeArray"));
            Assert.That(common, Does.Not.Contain("Png"));
            Assert.That(common, Does.Not.Contain("PNG"));
            Assert.That(common, Does.Not.Contain("Json"));
            Assert.That(common, Does.Not.Contain("JSON"));
        }

        [Test]
        public void LegacyEncodeBoundary_IsolatedBehindPngJsonNames()
        {
            Assembly assembly = typeof(TraceRunContext).Assembly;
            Assert.That(assembly.GetType("Zantetsu.Observability.ICaptureFrameEncodeService"), Is.Null);
            Assert.That(assembly.GetType("Zantetsu.Observability.CaptureFrameEncodeSubmission"), Is.Null);
            Assert.That(assembly.GetType("Zantetsu.Observability.CaptureFrameEncodeCompletion"), Is.Null);
            Assert.That(assembly.GetType("Zantetsu.Observability.CaptureFrameEncodeCompletionCoordinator"), Is.Null);
            Assert.That(assembly.GetType("Zantetsu.Observability.IPngJsonCaptureFrameEncodeService"), Is.Not.Null);
        }

        [Test]
        public void Envelope_PreservesSemanticValuesWithoutEncodedOutputFields()
        {
            CaptureFrameEnvelope envelope = MakeEnvelope(17);
            Assert.That(envelope.TestRunId, Is.EqualTo(3));
            Assert.That(envelope.CaptureFrameId, Is.EqualTo(17));
            Assert.That(envelope.UnityFrameId, Is.EqualTo(20));
            Assert.That(envelope.OpenXRFrameId, Is.EqualTo(30));
            Assert.That(envelope.TestCaseId, Is.EqualTo(91));
            Assert.That(envelope.BuildId, Is.EqualTo("build-a"));
            Assert.That(envelope.SceneId, Is.EqualTo("scene-a"));
            Assert.That(envelope.RandomSeed, Is.EqualTo(123));
            Assert.That(envelope.HeadPose.IsAvailable, Is.True);
            Assert.That(envelope.LeftControllerPose.IsAvailable, Is.False);

            string[] names = Array.ConvertAll(
                typeof(CaptureFrameEnvelope).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                property => property.Name);
            Assert.That(names, Does.Not.Contain("PngPath"));
            Assert.That(names, Does.Not.Contain("JsonPath"));
            Assert.That(names, Does.Not.Contain("ContentHash"));
            Assert.That(names, Does.Not.Contain("EncodedBytes"));
        }

        [Test]
        public void EvidenceCoordinator_BackpressurePreservesSurfaceOwnership()
        {
            using (CaptureFrameRenderTargetPool pool = MakePool())
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                using (CaptureSurfaceLease surface = new CaptureSurfaceLease(pool, lease))
                using (FakeEvidenceSession session = new FakeEvidenceSession(CaptureSubmitStatus.Backpressured))
                {
                    CaptureEvidenceCoordinator coordinator = new CaptureEvidenceCoordinator(session);
                    Assert.That(coordinator.TrySubmit(MakeEnvelope(1), surface, out CaptureFrameWorkToken token), Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(pool.RentedCount, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void EvidenceCoordinator_AcceptedTransfersUntilExactlyOneFrameCompletion()
        {
            using (CaptureFrameRenderTargetPool pool = MakePool())
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                CaptureSurfaceLease surface = new CaptureSurfaceLease(pool, lease);
                using (FakeEvidenceSession session = new FakeEvidenceSession(CaptureSubmitStatus.Accepted))
                {
                    CaptureEvidenceCoordinator coordinator = new CaptureEvidenceCoordinator(session);
                    Assert.That(coordinator.TrySubmit(MakeEnvelope(2), surface, out CaptureFrameWorkToken token), Is.EqualTo(CaptureSubmitStatus.Accepted));
                    Assert.That(surface.IsBackendOwned, Is.True);
                    session.Complete(surface, token);
                    Assert.That(coordinator.TryCollectFrameCompletion(out CaptureFrameCompletion completion), Is.True);
                    Assert.That(completion.IsValid, Is.True);
                    Assert.That(coordinator.TryCollectFrameCompletion(out _), Is.False);
                    Assert.That(surface.IsCreated, Is.False);
                    Assert.That(pool.RentedCount, Is.Zero);
                }
            }
        }

        [Test]
        public void GenericPublicationPlan_AllowsZeroOneAndMultipleArtifactsPerFrame()
        {
            CaptureArtifactDescriptor first = Descriptor("a", "frames/1.a.stage", "frames/1.a", HashA);
            CaptureArtifactDescriptor second = Descriptor("b", "frames/1.b.stage", "frames/1.b", HashB);
            CapturePublicationPlan plan = new CapturePublicationPlan(
                3,
                InitializationId,
                HashA,
                new[] { first, second },
                new[]
                {
                    new CaptureFrameEvidenceEntry(1, Array.Empty<string>()),
                    new CaptureFrameEvidenceEntry(2, new[] { "a" }),
                    new CaptureFrameEvidenceEntry(3, new[] { "a", "b" })
                });
            Assert.That(plan.IsValid, Is.True);
            Assert.That(plan.GetCaptureFrameEvidence(0).ArtifactCount, Is.Zero);
            Assert.That(plan.GetCaptureFrameEvidence(1).ArtifactCount, Is.EqualTo(1));
            Assert.That(plan.GetCaptureFrameEvidence(2).ArtifactCount, Is.EqualTo(2));

            string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Assets/Zantetsu/Runtime/Observability/CapturePublicationPlan.cs"));
            Assert.That(source, Does.Not.Contain("PngStagingRelativePath"));
            Assert.That(source, Does.Not.Contain("SidecarStagingRelativePath"));
            Assert.That(source, Does.Not.Contain("PngByteLength"));
        }

        [Test]
        public void GenericArtifactStore_WritesPublishesVerifiesAndRecovers()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsu-artifact-store-" + Guid.NewGuid().ToString("N"));
            string stagingBase = Path.Combine(sandbox, "staging");
            string finalBase = Path.Combine(sandbox, "final");
            Directory.CreateDirectory(stagingBase);
            Directory.CreateDirectory(finalBase);
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes("format-neutral artifact");
                CaptureArtifactDescriptor descriptor = new CaptureArtifactDescriptor(
                    "artifact/1", CaptureArtifactKind.TraceBundle, "application/octet-stream", 1,
                    "artifacts/1.bin.stage", "artifacts/1.bin", payload.LongLength, Hash(payload));
                CaptureRunRootLayout layout = new CaptureRunRootLayout(stagingBase, finalBase, 3);
                CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
                CaptureArtifactWriteReceipt write = store.WriteStaging(new CaptureArtifactWriteRequest(descriptor, payload));
                Assert.That(write.IsIssuedFor(store, descriptor), Is.True);
                Assert.That(store.VerifyStaging(descriptor).Status, Is.EqualTo(CaptureArtifactVerificationStatus.MatchesExpected));
                Assert.That(store.Verify(descriptor).Status, Is.EqualTo(CaptureArtifactVerificationStatus.Absent));

                CapturePublicationPlan plan = new CapturePublicationPlan(3, InitializationId, HashA,
                    new[] { descriptor }, new[] { new CaptureFrameEvidenceEntry(1, new[] { "artifact/1" }) });
                CapturePublicationRecoveryCoordinator recovery = new CapturePublicationRecoveryCoordinator(store);
                CapturePublicationRecoverySnapshot snapshot = recovery.Inspect(plan);
                Assert.That(CapturePublicationRecoveryClassifier.Classify(snapshot), Is.EqualTo(CapturePublicationRecoveryDisposition.PublishMissingArtifacts));
                Assert.That(recovery.ExecuteMissing(snapshot), Is.EqualTo(CapturePublicationRecoveryDisposition.CaptureComplete));
                Assert.That(store.Verify(descriptor).Status, Is.EqualTo(CaptureArtifactVerificationStatus.MatchesExpected));
                Assert.That(store.VerifyStaging(descriptor).Status, Is.EqualTo(CaptureArtifactVerificationStatus.Absent));
            }
            finally
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void PngJsonMetadata_PreservesFrameSemanticsAndPoseAvailability()
        {
            CaptureFrameEnvelope frame = MakeEnvelope(7);
            CaptureArtifactDescriptor image = new CaptureArtifactDescriptor(
                "frame/7/image", CaptureArtifactKind.FrameImage, "image/png", 1,
                "frames/7.png.stage", "frames/7.png", 20, HashA);
            string json = Encoding.UTF8.GetString(PngJsonFrameMetadataCodec.SerializeCanonical(frame, image));
            Assert.That(json, Does.Contain("\"captureFrameId\":7"));
            Assert.That(json, Does.Contain("\"testCaseId\":91"));
            Assert.That(json, Does.Contain("\"buildId\":\"build-a\""));
            Assert.That(json, Does.Contain("\"sceneId\":\"scene-a\""));
            Assert.That(json, Does.Contain("\"headPose\":{\"available\":true"));
            Assert.That(json, Does.Contain("\"leftControllerPose\":{\"available\":false}"));
            Assert.That(json, Does.Contain("\"frameImageContentHash\":\"" + HashA + "\""));
        }

        [Test]
        public void PngJsonBackend_OwnsReadbackAndPngImplementationDetails()
        {
            string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Assets/Zantetsu/Runtime/Observability/PngJsonCaptureEvidenceBackend.cs"));
            Assert.That(source, Does.Contain("UnityRenderTextureReadbackDispatcher"));
            Assert.That(source, Does.Contain("CaptureFramePngEncoder.Encode"));
            Assert.That(source, Does.Contain("PngJsonFrameMetadataCodec.SerializeCanonical"));
            Assert.That(source, Does.Contain("CaptureArtifactKind.FrameImage"));
            Assert.That(source, Does.Contain("CaptureArtifactKind.FrameMetadata"));
        }

        [Test]
        public void CommonContracts_DoNotChooseFutureMediaOrBinaryDetails()
        {
            string root = Path.Combine(RepositoryRoot(), "Assets/Zantetsu/Runtime/Observability");
            string[] files =
            {
                "CaptureFrameEnvelope.cs", "ICaptureEvidenceSession.cs", "CaptureEvidenceCoordinator.cs",
                "CaptureArtifactDescriptor.cs", "ICaptureArtifactStore.cs", "CapturePublicationPlan.cs"
            };
            string text = string.Empty;
            foreach (string file in files) text += File.ReadAllText(Path.Combine(root, file));
            Assert.That(text, Does.Not.Contain("NVENC"));
            Assert.That(text, Does.Not.Contain("VideoCodec"));
            Assert.That(text, Does.Not.Contain("MessagePack"));
            Assert.That(text, Does.Not.Contain("Protobuf"));
        }

        private static CaptureFrameEnvelope MakeEnvelope(long frameId)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(10, 20, 4, 1, frameId, 30, 3, 40, 50, 60, 2, 70);
            CaptureFrameRequest request = new CaptureFrameRequest(
                context, CaptureSource.UnityRenderTexture, CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
            CaptureFrameTiming timing = new CaptureFrameTiming(1.0, 0.01, true, 2.0, 3.0, 4);
            CapturePoseSample head = new CapturePoseSample(new Vector3(1, 2, 3), Quaternion.identity);
            return new CaptureFrameEnvelope(
                request, timing, head, CapturePoseSample.Unavailable, CapturePoseSample.Unavailable,
                8, 9, CaptureColorSpace.Srgb, 91, "build-a", "scene-a", 123);
        }

        private static CaptureFrameRenderTargetPool MakePool()
        {
            return new CaptureFrameRenderTargetPool(
                1,
                CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(9, new CaptureImageRect(0, 0, 2, 2)));
        }

        private static CaptureArtifactDescriptor Descriptor(string id, string staging, string final, string hash)
        {
            return new CaptureArtifactDescriptor(id, CaptureArtifactKind.TraceBundle, "application/octet-stream", 1, staging, final, 1, hash);
        }

        private static string Hash(byte[] payload)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(payload);
                StringBuilder sb = new StringBuilder(64);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string RepositoryRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private sealed class FakeEvidenceSession : ICaptureEvidenceSession
        {
            private readonly Guid _owner = Guid.NewGuid();
            private readonly CaptureSubmitStatus _status;
            private CaptureFrameCompletion _completion;
            private bool _hasCompletion;

            internal FakeEvidenceSession(CaptureSubmitStatus status) { _status = status; }

            public CaptureSubmitStatus TrySubmit(CaptureFrameEnvelope frame, CaptureSurfaceLease surface, out CaptureFrameWorkToken token)
            {
                if (_status != CaptureSubmitStatus.Accepted) { token = default; return _status; }
                token = new CaptureFrameWorkToken(_owner, surface.SlotIndex, 1, frame.TestRunId, frame.CaptureFrameId);
                surface.TransferToBackend(_owner, token);
                return _status;
            }

            internal void Complete(CaptureSurfaceLease surface, in CaptureFrameWorkToken token)
            {
                surface.ReleaseFromBackend(_owner, token);
                _completion = new CaptureFrameCompletion(token, token.CaptureFrameId, CaptureFrameCompletionStatus.Succeeded, true, 0, null);
                _hasCompletion = true;
            }

            public bool TryCollectFrameCompletion(out CaptureFrameCompletion completion)
            {
                completion = _completion;
                bool result = _hasCompletion;
                _hasCompletion = false;
                _completion = default;
                return result;
            }

            public bool TryCollectArtifactCompletion(out CaptureArtifactCompletion completion) { completion = null; return false; }
            public void BeginDrain() { }
            public int CancelQueued() => 0;
            public bool TryJoin() => true;
            public void Dispose() { }
        }
    }
}
