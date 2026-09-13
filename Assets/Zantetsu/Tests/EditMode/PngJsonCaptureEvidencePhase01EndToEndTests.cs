using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Phase 0.1 freeze / compatibility acceptance boundary. Wires the real
    /// production path — <see cref="PngJsonCaptureEvidenceBackend"/>,
    /// <see cref="CaptureEvidenceCoordinator"/>,
    /// <see cref="CaptureEvidenceDraftCoordinator"/>,
    /// <see cref="CaptureFrameFreezeTerminalCoordinator"/> — into one frame for
    /// the normal path, and pins backpressure, encode-failure, and deferred
    /// surface release without any new coordinator, token, proof, queue, or
    /// blocking wait API.
    /// </summary>
    public class PngJsonCaptureEvidencePhase01EndToEndTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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

        private static string RepositoryRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static void DrawAsymmetricPattern(RenderTexture target, int width, int height, out Color32[] pattern)
        {
            pattern = new Color32[width * height];
            // Texture2D.SetPixels32 indexes row-major from the bottom row, so
            // index (y * width + x) is the image pixel at column x, row y with
            // y == 0 the bottom row. Distinct corner and middle cells let the
            // decode step prove top/bottom and left/right orientation.
            pattern[0 * width + 0] = new Color32(0, 0, 255, 255); // bottom-left
            pattern[0 * width + 1] = new Color32(10, 20, 30, 255);
            pattern[0 * width + 2] = new Color32(40, 50, 60, 255);
            pattern[0 * width + 3] = new Color32(255, 255, 255, 255); // bottom-right
            pattern[1 * width + 0] = new Color32(255, 0, 0, 255); // top-left
            pattern[1 * width + 1] = new Color32(70, 80, 90, 255);
            pattern[1 * width + 2] = new Color32(100, 110, 120, 255);
            pattern[1 * width + 3] = new Color32(0, 255, 0, 255); // top-right

            Texture2D temp = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                temp.SetPixels32(pattern);
                temp.Apply();
                Graphics.CopyTexture(temp, target);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
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

        /// <summary>
        /// Instance-dependency encode seam: the first call fails, every later
        /// call delegates to the real production encoder. No static hook or
        /// runtime capability branch is used.
        /// </summary>
        private sealed class FailOnceEncoder : IPngJsonCaptureFrameEncoder
        {
            private int _calls;

            public NativeArray<byte> Encode(NativeArray<byte> rgbaBytes, in CaptureFramePixelLayout layout)
            {
                if (_calls++ == 0)
                {
                    throw new InvalidOperationException("fake encode failure");
                }

                return CaptureFramePngEncoder.Encode(rgbaBytes, layout);
            }
        }

        // ---- Scope ----

        private sealed class Scope
        {
            public long TestRunId = 3;
            public int ProfileId = 9;
            public int TestCaseId = 91;
            public int Width = 4;
            public int Height = 2;
            public int BackendCapacity = 1;
            public int ArtifactRegistryCapacity = 2;
            public IPngJsonCaptureFrameEncoder Encoder;

            public string Sandbox;
            public CaptureRunRootLayout Layout;
            public TraceLogger Logger;
            public TraceFlightRecorder Recorder;
            public CaptureDraftRunContext Run;
            public CaptureFrameDraftRegistry Registry;
            public FreezeTerminalTraceBufferBuilder Builder;
            public CaptureFrameFreezeTerminalCoordinator Freeze;
            public CaptureArtifactRegistry Artifacts;
            public CaptureFrameTraceObserver Trace;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameReadbackBufferPool Buffers;
            public UnityRenderTextureReadbackDispatcher Dispatcher;
            public CaptureArtifactFileStore Store;
            public PngJsonCaptureEvidenceBackend Backend;
            public CaptureEvidenceCoordinator Evidence;
            public CaptureEvidenceDraftCoordinator DraftCoordinator;
            public CaptureFrameDraftTerminalIntentQueue Queue;
            public CaptureFrameDraftFactory Factory;
            public CaptureRunInitializationSession Session;
            public CaptureRunLockIdentityEvidence Identity;
            public CaptureRunInitializationSessionOwnershipLease Owner;

            public void Dispose()
            {
                if (Backend != null)
                {
                    try { Backend.Dispose(); } catch (Exception) { }
                }

                if (Dispatcher != null)
                {
                    try { Dispatcher.Dispose(); } catch (Exception) { }
                }

                if (Buffers != null)
                {
                    try { Buffers.Dispose(); } catch (Exception) { }
                }

                if (Pool != null)
                {
                    try { Pool.Dispose(); } catch (Exception) { }
                }

                if (Queue != null && Queue.IsCreated)
                {
                    try { Queue.Dispose(); } catch (Exception) { }
                }

                if (Owner != null)
                {
                    try { Owner.Dispose(); } catch (Exception) { }
                }

                if (Logger != null)
                {
                    try { Logger.Dispose(); } catch (Exception) { }
                }

                if (Sandbox != null && Directory.Exists(Sandbox))
                {
                    try { Directory.Delete(Sandbox, true); } catch (Exception) { }
                }
            }
        }

        private Scope BuildScope(PngJsonCaptureEvidencePhase01EndToEndTests owner, IPngJsonCaptureFrameEncoder encoder = null, int artifactRegistryCapacity = 2)
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsuken-phase01-" + Guid.NewGuid().ToString("N"));
            string staging = Path.Combine(sandbox, "staging");
            string final = Path.Combine(sandbox, "final");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(final);
            _sandboxes.Add(sandbox);

            Scope scope = new Scope
            {
                Sandbox = sandbox,
                Layout = new CaptureRunRootLayout(staging, final, 3),
                Logger = new TraceLogger(16, 3),
                Encoder = encoder,
                ArtifactRegistryCapacity = artifactRegistryCapacity,
            };

            scope.Recorder = new TraceFlightRecorder(scope.Logger, 16, 2);

            TraceRunContext context = new TraceRunContext(
                3, 1000, "build-1", "6000.3.22f1", ValidSha256, "scene-1", 12345, 0.02, 3, "High", 1,
                new Vector3(0f, -4.9f, 0f));
            scope.Run = new CaptureDraftRunContext(context, 91, 9);

            CaptureTraceProfile traceProfile = new CaptureTraceProfile(9, 16, 2, 8);
            scope.Registry = new CaptureFrameDraftRegistry(scope.Run, traceProfile);
            scope.Builder = new FreezeTerminalTraceBufferBuilder(scope.Registry);
            scope.Freeze = new CaptureFrameFreezeTerminalCoordinator(scope.Recorder, scope.Builder);
            scope.Artifacts = new CaptureArtifactRegistry(artifactRegistryCapacity);
            scope.Trace = new CaptureFrameTraceObserver(scope.Logger);

            CaptureFrameProfile frameProfile =
                CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(9, new CaptureImageRect(0, 0, 4, 2));
            scope.Pool = new CaptureFrameRenderTargetPool(1, frameProfile);
            scope.Buffers = new CaptureFrameReadbackBufferPool(1, 64);
            scope.Dispatcher = new UnityRenderTextureReadbackDispatcher(scope.Buffers);
            scope.Store = new CaptureArtifactFileStore(scope.Layout);
            scope.Backend = encoder == null
                ? new PngJsonCaptureEvidenceBackend(1, scope.Dispatcher, scope.Store)
                : new PngJsonCaptureEvidenceBackend(1, scope.Dispatcher, scope.Store, encoder);
            scope.Evidence = new CaptureEvidenceCoordinator(scope.Backend);
            scope.DraftCoordinator = new CaptureEvidenceDraftCoordinator(
                1, scope.Evidence, scope.Registry, scope.Artifacts, scope.Trace);
            scope.Queue = new CaptureFrameDraftTerminalIntentQueue(scope.Registry, traceProfile);

            scope.Factory = new CaptureFrameDraftFactory(
                scope.Run,
                new CaptureFrameIdSequence(),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 4, 2),
                0,
                CapturePixelFormat.Rgba32);

            CaptureRunLockLease lease = MakeLease(scope.Layout);
            scope.Owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(scope.Owner);
            scope.Identity = CaptureRunLockIdentityEvidence.Create(scope.Owner, scope.Owner.LockPathSet);

            CaptureRunInitializationExecutionCoordinator execution =
                new CaptureRunInitializationExecutionCoordinator(new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationExecutionReceipt executionReceipt = execution.Execute(scope.Layout, InitId);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(executionReceipt);
            CaptureRunInitializationSessionIssue issue =
                CaptureRunInitializationSession.IssuanceProof.Mint(scope.Owner, scope.Identity, evidence);
            scope.Session = issue.Session;

            return scope;
        }

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            ICaptureRunLockHandle first = new FakeHandle(pathSet.FirstLockPath, true);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        // ---- Draft / submission helpers ----

        private static CaptureFrameDraft MakeDraft(Scope scope, long timestamp, long unityFrameId, long openXRFrameId)
        {
            return scope.Factory.Create(
                timestamp,
                unityFrameId,
                1,
                1,
                openXRFrameId,
                10,
                20,
                30,
                1,
                40,
                new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L),
                new CapturePoseSample(new Vector3(1, 2, 3), Quaternion.identity),
                CapturePoseSample.Unavailable,
                CapturePoseSample.Unavailable,
                1);
        }

        private static void CommitDraft(Scope scope, CaptureFrameDraft draft)
        {
            Assert.That(scope.Registry.TryReserve(out CaptureFrameDraftReservation reservation, out _), Is.True);
            scope.Registry.Commit(reservation, draft);
        }

        private static CaptureSurfaceLease SubmitDrawnFrame(
            Scope scope,
            CaptureFrameDraft draft,
            out Color32[] pattern)
        {
            Assert.That(scope.Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
            CaptureSurfaceLease surface = new CaptureSurfaceLease(scope.Pool, lease);
            DrawAsymmetricPattern(surface.GetSurfaceForCaller(), scope.Width, scope.Height, out pattern);
            Assert.That(
                scope.DraftCoordinator.TrySubmit(draft, surface, CaptureColorSpace.Srgb, out CaptureFrameWorkToken token),
                Is.EqualTo(CaptureSubmitStatus.Accepted));
            Assert.That(surface.IsBackendOwned, Is.True);
            return surface;
        }

        private static bool PumpAndWaitForWorkItem(
            PngJsonCaptureEvidenceBackend backend,
            Func<bool> pump,
            int timeoutMs = 5000)
        {
            WakeHint wake = new WakeHint();
            Action handler = wake.Signal;
            backend.CompletionEnqueued += handler;
            try
            {
                // Subscribe before pumping: the pump hands the readback to
                // the worker, which may enqueue a completion and fire the
                // event immediately. The non-blocking pump must still
                // report nothing ready before the worker completes.
                Assert.That(pump(), Is.False);
                return wake.WaitForSignal(timeoutMs);
            }
            finally
            {
                backend.CompletionEnqueued -= handler;
            }
        }

        private static bool WaitForBackendJoin(PngJsonCaptureEvidenceBackend backend, int timeoutMs = 5000)
        {
            WakeHint wake = new WakeHint();
            Action handler = wake.Signal;
            backend.WorkerStopped += handler;
            try
            {
                // Joining is what decides; the event only says when it is
                // worth asking again. One wall-clock deadline covers the
                // whole wait, and the last word is another join attempt.
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    if (backend.TryJoin())
                    {
                        return true;
                    }

                    long remaining = timeoutMs - clock.ElapsedMilliseconds;
                    if (remaining <= 0)
                    {
                        return backend.TryJoin();
                    }

                    wake.WaitForSignal((int)remaining);
                }
            }
            finally
            {
                backend.WorkerStopped -= handler;
            }
        }

        /// <summary>
        /// A wake hint from a backend event. The handler touches nothing but
        /// this object, which holds no disposable resource, so a delegate that
        /// arrives after it was unsubscribed - unsubscribing does not wait for
        /// one already running - has nothing left to break.
        /// </summary>
        private sealed class WakeHint
        {
            private readonly object _gate = new object();

            private bool _signalled;

            internal void Signal()
            {
                lock (_gate)
                {
                    _signalled = true;
                    Monitor.Pulse(_gate);
                }
            }

            /// <summary>
            /// Waits for one signal or the given milliseconds, whichever comes
            /// first, and says which it was. A signal already waiting is taken
            /// and cleared, so a caller that loops waits again rather than
            /// spinning.
            /// </summary>
            internal bool WaitForSignal(int milliseconds)
            {
                lock (_gate)
                {
                    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!_signalled)
                    {
                        long remaining = milliseconds - clock.ElapsedMilliseconds;
                        if (remaining <= 0)
                        {
                            return false;
                        }

                        Monitor.Wait(_gate, (int)remaining);
                    }

                    _signalled = false;
                    return true;
                }
            }
        }

        private static ForcedDropFrameIdSet IssueEmptyForcedDropSet(Scope scope)
        {
            scope.Queue.BeginProducerDrain();
            scope.Queue.CloseAfterProducerJoin();
            TerminalIntentOwnershipSnapshot snapshot = scope.Queue.CreateOwnershipSnapshot(0);
            return scope.Registry.ForceDropPendingForFreeze(scope.Queue, snapshot);
        }

        private static TraceRunSealReceipt Seal(Scope scope)
        {
            Assert.That(scope.Recorder.TryTrigger(), Is.True);
            return scope.Logger.SealAndDrainRunForFreeze(scope.TestRunId, scope.Recorder);
        }

        private static FreezeTerminalCheckpoint MakeCheckpoint(long testRunId)
        {
            return new FreezeTerminalCheckpoint(200, 201, 202, 203, testRunId);
        }

        // ---- Tests ----

        [Test]
        public void Phase01_FullPipeline_FreezeReceiptAndArtifactCompatibility()
        {
            Scope scope = BuildScope(this);
            try
            {
                CaptureFrameDraft draft = MakeDraft(scope, 100, 20, 30);
                CommitDraft(scope, draft);

                Color32[] pattern;
                SubmitDrawnFrame(scope, draft, out pattern);

                // BeginDrain + CancelQueued before the readback completes. The
                // in-flight readback still drains.
                scope.DraftCoordinator.BeginDrain();
                Assert.That(scope.DraftCoordinator.CancelQueued(), Is.Zero);

                // TryJoin stays false while the GPU readback is outstanding.
                Assert.That(scope.DraftCoordinator.TryJoin(), Is.False);

                AsyncGPUReadback.WaitAllRequests();

                // Non-blocking main-thread apply: the first call only collects
                // the readback and submits it to the worker, so nothing is
                // ready yet.
                Assert.That(
                    PumpAndWaitForWorkItem(scope.Backend, () => scope.DraftCoordinator.TryApplyNextCompletion()),
                    Is.True);
                Assert.That(scope.DraftCoordinator.TryJoin(), Is.False);

                // Frame completion is applied before either artifact completion.
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True);
                Assert.That(scope.Registry.GetEntryStatus(0), Is.EqualTo(CaptureFrameDraftStatus.Pending));
                Assert.That(scope.DraftCoordinator.TryJoin(), Is.False);

                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // image artifact
                Assert.That(scope.DraftCoordinator.TryJoin(), Is.False);

                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // metadata artifact
                Assert.That(scope.Registry.GetEntryStatus(0), Is.EqualTo(CaptureFrameDraftStatus.Staged));
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.False);

                Assert.That(WaitForBackendJoin(scope.Backend), Is.True);
                Assert.That(scope.DraftCoordinator.TryJoin(), Is.True);
                Assert.That(scope.DraftCoordinator.IsFullyDrained, Is.True);

                // Freeze terminal inputs, in production order: seal the trace
                // run, then issue the (empty) forced-drop set, then complete.
                TraceRunSealReceipt sealReceipt = Seal(scope);
                ForcedDropFrameIdSet set = IssueEmptyForcedDropSet(scope);
                FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(scope.TestRunId);

                Assert.That(
                    scope.Freeze.TryCompleteEvidenceRun(
                        scope.DraftCoordinator,
                        scope.Session,
                        scope.Identity,
                        sealReceipt,
                        set,
                        checkpoint,
                        out CaptureEvidenceRunFreezeReceipt receipt),
                    Is.True);
                Assert.That(receipt, Is.Not.Null);
                Assert.That(receipt.IsValid, Is.True);

                // Freeze Receipt correlates with the exact Draft and Artifact
                // registries.
                Assert.That(ReferenceEquals(receipt.Drafts, scope.Registry), Is.True);
                Assert.That(ReferenceEquals(receipt.Artifacts, scope.Artifacts), Is.True);
                Assert.That(ReferenceEquals(receipt.RunSession, scope.Session), Is.True);
                Assert.That(ReferenceEquals(receipt.LockIdentityEvidence, scope.Identity), Is.True);
                Assert.That(receipt.TestRunId, Is.EqualTo(scope.TestRunId));
                Assert.That(receipt.RunInitializationId, Is.EqualTo(InitId));
                Assert.That(receipt.TerminalBuffer.TestRunId, Is.EqualTo(scope.TestRunId));
                Assert.That(receipt.TerminalBuffer.Count, Is.EqualTo(1)); // zero forced drops + one ring-frozen
                Assert.That(receipt.TerminalBuffer.ForcedDropCount, Is.Zero);
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));

                // Every captured resource is recovered after freeze.
                Assert.That(scope.Pool.RentedCount, Is.Zero);
                Assert.That(scope.Buffers.RentedCount, Is.Zero);
                Assert.That(scope.Dispatcher.ActiveCount, Is.Zero);
                Assert.That(scope.Artifacts.ReservedArtifactCount, Is.Zero);
                Assert.That(scope.Registry.PendingCount, Is.Zero);
                Assert.That(scope.Registry.ReservationCount, Is.Zero);

                // Exactly one PNG and one canonical JSON are registered.
                Assert.That(scope.Artifacts.Count, Is.EqualTo(2));
                CaptureArtifactDescriptor image = null;
                CaptureArtifactDescriptor metadata = null;
                for (int i = 0; i < scope.Artifacts.Count; i++)
                {
                    CaptureArtifactDescriptor descriptor = scope.Artifacts.GetDescriptor(i);
                    if (descriptor.ArtifactKind == CaptureArtifactKind.FrameImage) image = descriptor;
                    else if (descriptor.ArtifactKind == CaptureArtifactKind.FrameMetadata) metadata = descriptor;
                }

                Assert.That(image, Is.Not.Null);
                Assert.That(metadata, Is.Not.Null);

                // Frame / TestRun / Trace correlation across Draft, Artifact
                // Registry, and metadata.
                long frameId = draft.CaptureFrameId;
                Assert.That(draft.TestRunId, Is.EqualTo(scope.TestRunId));
                Assert.That(frameId, Is.EqualTo(1));
                Assert.That(image.ArtifactId, Is.EqualTo("frame/" + frameId + "/image"));
                Assert.That(metadata.ArtifactId, Is.EqualTo("frame/" + frameId + "/metadata"));
                for (int i = 0; i < scope.Artifacts.Count; i++)
                {
                    Assert.That(scope.Artifacts.GetWorkToken(i).TestRunId, Is.EqualTo(scope.TestRunId));
                    Assert.That(scope.Artifacts.GetWorkToken(i).CaptureFrameId, Is.EqualTo(frameId));
                    Assert.That(scope.Artifacts.GetFrameRelation(i).Contains(frameId), Is.True);
                }

                // Staging content is verified without transformation.
                Assert.That(scope.Store.VerifyStaging(image).Status, Is.EqualTo(CaptureArtifactVerificationStatus.MatchesExpected));
                Assert.That(scope.Store.VerifyStaging(metadata).Status, Is.EqualTo(CaptureArtifactVerificationStatus.MatchesExpected));

                // PNG decode via the existing Unity decoder: dimensions and the
                // asymmetric cell placement prove top/bottom and left/right
                // orientation, and every RGBA byte round-trips losslessly.
                byte[] pngBytes = File.ReadAllBytes(Path.Combine(scope.Layout.StagingRunRoot, image.StagingRelativePath));
                Assert.That(pngBytes.Length, Is.EqualTo(image.ByteLength));
                Texture2D decoded = new Texture2D(scope.Width, scope.Height, TextureFormat.RGBA32, false);
                try
                {
                    Assert.That(decoded.LoadImage(pngBytes), Is.True);
                    Assert.That(decoded.width, Is.EqualTo(scope.Width));
                    Assert.That(decoded.height, Is.EqualTo(scope.Height));

                    Color32[] decodedPixels = decoded.GetPixels32();
                    Assert.That(decodedPixels.Length, Is.EqualTo(pattern.Length));
                    Assert.That(decodedPixels[0], Is.EqualTo(pattern[0])); // bottom-left
                    Assert.That(decodedPixels[scope.Width - 1], Is.EqualTo(pattern[scope.Width - 1])); // bottom-right
                    Assert.That(decodedPixels[(scope.Height - 1) * scope.Width], Is.EqualTo(pattern[(scope.Height - 1) * scope.Width])); // top-left
                    Assert.That(decodedPixels[scope.Height * scope.Width - 1], Is.EqualTo(pattern[scope.Height * scope.Width - 1])); // top-right

                    for (int i = 0; i < pattern.Length; i++)
                    {
                        Assert.That(decodedPixels[i].r, Is.EqualTo(pattern[i].r), "lossless R at pixel " + i);
                        Assert.That(decodedPixels[i].g, Is.EqualTo(pattern[i].g), "lossless G at pixel " + i);
                        Assert.That(decodedPixels[i].b, Is.EqualTo(pattern[i].b), "lossless B at pixel " + i);
                        Assert.That(decodedPixels[i].a, Is.EqualTo(pattern[i].a), "lossless A at pixel " + i);
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(decoded);
                }

                // Metadata canonical round-trip: re-serializing the exact
                // envelope and image descriptor reproduces the stored bytes.
                byte[] metadataBytes = File.ReadAllBytes(Path.Combine(scope.Layout.StagingRunRoot, metadata.StagingRelativePath));
                Assert.That(metadataBytes.Length, Is.EqualTo(metadata.ByteLength));
                byte[] canonical = PngJsonFrameMetadataCodec.SerializeCanonical(
                    CaptureFrameEnvelope.FromDraft(draft, CaptureColorSpace.Srgb),
                    image);
                Assert.That(metadataBytes, Is.EqualTo(canonical));

                // Image descriptor ByteLength / SHA-256 correlate with the
                // image fields inside the metadata JSON.
                string metadataJson = Encoding.UTF8.GetString(metadataBytes);
                Assert.That(metadataJson, Does.Contain("\"frameImageByteLength\":" + image.ByteLength));
                Assert.That(metadataJson, Does.Contain("\"frameImageContentHash\":\"" + image.ContentHash + "\""));
                Assert.That(metadataJson, Does.Contain("\"captureFrameId\":" + frameId));
                Assert.That(metadataJson, Does.Contain("\"testRunId\":" + scope.TestRunId));
                Assert.That(metadataJson, Does.Contain("\"testCaseId\":" + scope.TestCaseId));
                Assert.That(metadataJson, Does.Contain("\"buildId\":\"build-1\""));
                Assert.That(metadataJson, Does.Contain("\"sceneId\":\"scene-1\""));
            }
            finally
            {
                scope.Dispose();
            }
        }

        [Test]
        public void Phase01_Backpressure_CapacityOne_SecondSubmissionRejectedAndSlotReused()
        {
            Scope scope = BuildScope(this, artifactRegistryCapacity: 4);
            try
            {
                CaptureFrameDraft draftA = MakeDraft(scope, 100, 20, 30);
                CaptureFrameDraft draftB = MakeDraft(scope, 101, 21, 31);
                CommitDraft(scope, draftA);
                CommitDraft(scope, draftB);

                SubmitDrawnFrame(scope, draftA, out _);

                AsyncGPUReadback.WaitAllRequests();

                // The first pump collects A's readback and releases its render
                // target; the worker is still encoding, so nothing is ready.
                Assert.That(
                    PumpAndWaitForWorkItem(scope.Backend, () => scope.DraftCoordinator.TryApplyNextCompletion()),
                    Is.True);

                // A's frame completion is applied first; its two artifacts are
                // still pending, so the slot stays occupied.
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // frame A

                // The render target is free again, so the caller can rent a
                // second surface.
                Assert.That(scope.Pool.TryRent(out CaptureFrameRenderTargetLease leaseB), Is.True);
                CaptureSurfaceLease surfaceB = new CaptureSurfaceLease(scope.Pool, leaseB);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));

                // Capacity 1: the second submission is backpressured and the
                // caller keeps surface ownership.
                Assert.That(
                    scope.DraftCoordinator.TrySubmit(draftB, surfaceB, CaptureColorSpace.Srgb, out CaptureFrameWorkToken tokenB),
                    Is.EqualTo(CaptureSubmitStatus.Backpressured));
                Assert.That(tokenB.IsValid, Is.False);
                Assert.That(surfaceB.IsCallerOwned, Is.True);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));

                // Collect A's two artifacts to free the slot.
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // image A
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // metadata A
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.False);

                // Only now is the slot reusable by the second frame.
                Assert.That(
                    scope.DraftCoordinator.TrySubmit(draftB, surfaceB, CaptureColorSpace.Srgb, out tokenB),
                    Is.EqualTo(CaptureSubmitStatus.Accepted));
                Assert.That(surfaceB.IsBackendOwned, Is.True);

                // Drain only after the second frame is accepted.
                scope.DraftCoordinator.BeginDrain();

                AsyncGPUReadback.WaitAllRequests();
                Assert.That(
                    PumpAndWaitForWorkItem(scope.Backend, () => scope.DraftCoordinator.TryApplyNextCompletion()),
                    Is.True);
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // frame B
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // image B
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // metadata B
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.False);

                Assert.That(WaitForBackendJoin(scope.Backend), Is.True);
                Assert.That(scope.DraftCoordinator.TryJoin(), Is.True);
                Assert.That(scope.Registry.GetEntryStatus(0), Is.EqualTo(CaptureFrameDraftStatus.Staged));
                Assert.That(scope.Registry.GetEntryStatus(1), Is.EqualTo(CaptureFrameDraftStatus.Staged));
                Assert.That(scope.Artifacts.Count, Is.EqualTo(4));
                Assert.That(scope.Pool.RentedCount, Is.Zero);
                Assert.That(scope.Dispatcher.ActiveCount, Is.Zero);
            }
            finally
            {
                scope.Dispose();
            }
        }

        [Test]
        public void Phase01_EncodeFailure_NoMainThreadFallback_FailedFrameThenNextFrameProceeds()
        {
            Scope scope = BuildScope(this, new FailOnceEncoder());
            try
            {
                CaptureFrameDraft draftA = MakeDraft(scope, 100, 20, 30);
                CaptureFrameDraft draftB = MakeDraft(scope, 101, 21, 31);
                CommitDraft(scope, draftA);
                CommitDraft(scope, draftB);

                SubmitDrawnFrame(scope, draftA, out _);

                AsyncGPUReadback.WaitAllRequests();
                Assert.That(
                    PumpAndWaitForWorkItem(scope.Backend, () => scope.DraftCoordinator.TryApplyNextCompletion()),
                    Is.True);
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // failed frame A
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.False);

                // The failed frame registers no artifact and is dropped with
                // the media-processing reason; no main-thread fallback runs.
                Assert.That(scope.Artifacts.Count, Is.Zero);
                Assert.That(scope.Registry.GetEntryStatus(0), Is.EqualTo(CaptureFrameDraftStatus.Dropped));
                Assert.That(scope.Registry.GetEntryDropReason(0), Is.EqualTo(CaptureFrameDropReason.MediaProcessingFailed));

                // The next accepted frame still processes normally.
                Assert.That(scope.Pool.TryRent(out CaptureFrameRenderTargetLease leaseB), Is.True);
                CaptureSurfaceLease surfaceB = new CaptureSurfaceLease(scope.Pool, leaseB);
                DrawAsymmetricPattern(surfaceB.GetSurfaceForCaller(), scope.Width, scope.Height, out _);
                Assert.That(
                    scope.DraftCoordinator.TrySubmit(draftB, surfaceB, CaptureColorSpace.Srgb, out CaptureFrameWorkToken tokenB),
                    Is.EqualTo(CaptureSubmitStatus.Accepted));

                // Drain only after the second frame is accepted.
                scope.DraftCoordinator.BeginDrain();

                AsyncGPUReadback.WaitAllRequests();
                Assert.That(
                    PumpAndWaitForWorkItem(scope.Backend, () => scope.DraftCoordinator.TryApplyNextCompletion()),
                    Is.True);
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // frame B
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // image B
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // metadata B
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.False);

                Assert.That(scope.Registry.GetEntryStatus(1), Is.EqualTo(CaptureFrameDraftStatus.Staged));
                Assert.That(scope.Artifacts.Count, Is.EqualTo(2));

                Assert.That(WaitForBackendJoin(scope.Backend), Is.True);
                Assert.That(scope.DraftCoordinator.TryJoin(), Is.True);

                // Freeze and drain still converge with the dropped frame.
                TraceRunSealReceipt sealReceipt = Seal(scope);
                ForcedDropFrameIdSet set = IssueEmptyForcedDropSet(scope);
                FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(scope.TestRunId);

                Assert.That(
                    scope.Freeze.TryCompleteEvidenceRun(
                        scope.DraftCoordinator,
                        scope.Session,
                        scope.Identity,
                        sealReceipt,
                        set,
                        checkpoint,
                        out CaptureEvidenceRunFreezeReceipt receipt),
                    Is.True);
                Assert.That(receipt, Is.Not.Null);
                Assert.That(receipt.IsValid, Is.True);
                Assert.That(receipt.Artifacts.Count, Is.EqualTo(2));
                Assert.That(scope.Artifacts.ReservedArtifactCount, Is.Zero);
                Assert.That(scope.Pool.RentedCount, Is.Zero);
            }
            finally
            {
                scope.Dispose();
            }
        }

        [Test]
        public void Phase01_DeferredSurfaceRelease_BlocksJoinAndFreezeUntilRecovered()
        {
            Scope scope = BuildScope(this);
            try
            {
                CaptureFrameDraft draft = MakeDraft(scope, 100, 20, 30);
                CommitDraft(scope, draft);

                SubmitDrawnFrame(scope, draft, out _);

                // Corrupt the render target pool so the surface release fails
                // before the pool side effect (the slot stays rented).
                FieldInfo disposedField = typeof(CaptureFrameRenderTargetPool)
                    .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(disposedField, Is.Not.Null);
                disposedField.SetValue(scope.Pool, true);

                scope.DraftCoordinator.BeginDrain();
                AsyncGPUReadback.WaitAllRequests();
                Assert.That(
                    PumpAndWaitForWorkItem(scope.Backend, () => scope.DraftCoordinator.TryApplyNextCompletion()),
                    Is.True);
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // frame
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // image
                Assert.That(scope.DraftCoordinator.TryApplyNextCompletion(), Is.True); // metadata
                Assert.That(scope.Registry.GetEntryStatus(0), Is.EqualTo(CaptureFrameDraftStatus.Staged));

                // The deferred surface release keeps join false even though
                // every completion has been applied.
                Assert.That(scope.DraftCoordinator.TryJoin(), Is.False);

                TraceRunSealReceipt sealReceipt = Seal(scope);
                ForcedDropFrameIdSet set = IssueEmptyForcedDropSet(scope);
                FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(scope.TestRunId);

                // Freeze must not succeed before the deferred release completes.
                Assert.That(
                    scope.Freeze.TryCompleteEvidenceRun(
                        scope.DraftCoordinator,
                        scope.Session,
                        scope.Identity,
                        sealReceipt,
                        set,
                        checkpoint,
                        out CaptureEvidenceRunFreezeReceipt beforeRecovery),
                    Is.False);
                Assert.That(beforeRecovery, Is.Null);

                // Recover the pool and advance the backend once more; the pump
                // retries the deferred surface release.
                disposedField.SetValue(scope.Pool, false);
                scope.DraftCoordinator.TryApplyNextCompletion();
                Assert.That(WaitForBackendJoin(scope.Backend), Is.True);
                Assert.That(scope.DraftCoordinator.TryJoin(), Is.True);

                // Now freeze succeeds without any manual pool.Return.
                Assert.That(
                    scope.Freeze.TryCompleteEvidenceRun(
                        scope.DraftCoordinator,
                        scope.Session,
                        scope.Identity,
                        sealReceipt,
                        set,
                        checkpoint,
                        out CaptureEvidenceRunFreezeReceipt receipt),
                    Is.True);
                Assert.That(receipt, Is.Not.Null);
                Assert.That(receipt.IsValid, Is.True);
                Assert.That(scope.Pool.RentedCount, Is.Zero);
                Assert.That(scope.Dispatcher.ActiveCount, Is.Zero);
                Assert.That(scope.Artifacts.ReservedArtifactCount, Is.Zero);
            }
            finally
            {
                scope.Dispose();
            }
        }

        [Test]
        public void Phase01_BackendPath_HasNoSynchronousFallback()
        {
            string source = File.ReadAllText(Path.Combine(
                RepositoryRoot(),
                "Assets/Zantetsu/Runtime/Observability/PngJsonCaptureEvidenceBackend.cs"));

            // The formal Phase 0.1 backend delegates encode, hash, JSON, and
            // staging to the dedicated worker and has no main-thread fallback.
            Assert.That(source, Does.Contain("PngJsonCaptureEvidenceWorkerService"));
            Assert.That(source, Does.Not.Contain("PngJsonSynchronousCaptureFrameEncodeService"));
            Assert.That(source, Does.Not.Contain("CaptureFramePngEncoder.Encode"));
            Assert.That(source, Does.Not.Contain("PngJsonFrameMetadataCodec"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("ImageConversion"));
            Assert.That(source, Does.Not.Contain("EncodeNativeArrayToPNG"));
        }
    }
}
