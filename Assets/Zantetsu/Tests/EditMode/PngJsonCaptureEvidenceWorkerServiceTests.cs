using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class PngJsonCaptureEvidenceWorkerServiceTests
    {
        // ---- Fake encoder ----

        private sealed class FakeEncoder : IPngJsonCaptureFrameEncoder
        {
            private readonly object _gate = new object();
            private readonly List<int> _callThreadIds = new List<int>();
            private int _callCount;
            private readonly ManualResetEvent _blockEntered = new ManualResetEvent(false);
            private readonly ManualResetEvent _releaseBlock = new ManualResetEvent(false);

            public volatile bool Block;

            public bool ReturnEmpty { get; set; }

            public int FailOnCall { get; set; } = -1;

            public Exception ExceptionToThrow { get; set; }

            public int CallCount
            {
                get { lock (_gate) return _callCount; }
            }

            public int[] CallThreadIds
            {
                get { lock (_gate) return _callThreadIds.ToArray(); }
            }

            public void WaitForBlockEntered(int timeoutMs = 5000)
            {
                Assert.That(_blockEntered.WaitOne(timeoutMs), Is.True, "Encoder block was not entered.");
            }

            public void ReleaseBlock()
            {
                _releaseBlock.Set();
            }

            public NativeArray<byte> Encode(NativeArray<byte> rgbaBytes, in CaptureFramePixelLayout layout)
            {
                int call;
                lock (_gate)
                {
                    _callThreadIds.Add(Environment.CurrentManagedThreadId);
                    call = _callCount++;
                }

                if (call == FailOnCall)
                {
                    throw ExceptionToThrow ?? new InvalidOperationException("fake encode failure");
                }

                if (Block)
                {
                    _blockEntered.Set();
                    _releaseBlock.WaitOne();
                }

                if (ReturnEmpty)
                {
                    return default;
                }

                return new NativeArray<byte>(5, Allocator.Persistent);
            }
        }

        // ---- Fake store ----

        private sealed class FakeStore : ICaptureArtifactStore, IDisposable
        {
            private readonly object _gate = new object();
            private readonly List<int> _callThreadIds = new List<int>();
            private readonly List<CaptureArtifactDescriptor> _descriptors = new List<CaptureArtifactDescriptor>();

            public bool ReturnNullReceipt { get; set; }

            public bool ReturnForeignReceipt { get; set; }

            public bool ThrowOnWrite { get; set; }

            public int CallCount
            {
                get { lock (_gate) return _callThreadIds.Count; }
            }

            public int[] CallThreadIds
            {
                get { lock (_gate) return _callThreadIds.ToArray(); }
            }

            public CaptureArtifactDescriptor[] Descriptors
            {
                get { lock (_gate) return _descriptors.ToArray(); }
            }

            public CaptureArtifactWriteReceipt WriteStaging(CaptureArtifactWriteRequest request)
            {
                lock (_gate)
                {
                    _callThreadIds.Add(Environment.CurrentManagedThreadId);
                    _descriptors.Add(request.Descriptor);
                }

                if (ThrowOnWrite)
                {
                    throw new InvalidOperationException("fake store write failure");
                }

                if (ReturnNullReceipt)
                {
                    return null;
                }

                if (ReturnForeignReceipt)
                {
                    return new CaptureArtifactWriteReceipt(
                        new FakeStore(),
                        request.Descriptor,
                        "C:\\staging\\foreign");
                }

                return new CaptureArtifactWriteReceipt(this, request.Descriptor, "C:\\staging\\" + request.Descriptor.ArtifactId);
            }

            public CaptureArtifactPublishReceipt Publish(CaptureArtifactDescriptor descriptor)
            {
                throw new NotSupportedException();
            }

            public CaptureArtifactVerificationResult VerifyStaging(CaptureArtifactDescriptor descriptor)
            {
                return new CaptureArtifactVerificationResult(
                    descriptor,
                    CaptureArtifactVerificationExecutionDisposition.Completed,
                    CaptureArtifactVerificationStatus.MatchesExpected,
                    CaptureArtifactVerificationFailureReason.None,
                    descriptor.ByteLength);
            }

            public CaptureArtifactVerificationResult Verify(CaptureArtifactDescriptor descriptor)
            {
                return new CaptureArtifactVerificationResult(
                    descriptor,
                    CaptureArtifactVerificationExecutionDisposition.Completed,
                    CaptureArtifactVerificationStatus.Absent,
                    CaptureArtifactVerificationFailureReason.FileAbsent,
                    0);
            }

            public void Dispose()
            {
            }
        }

        // ---- Readback scope ----

        private sealed class ReadbackScope : IDisposable
        {
            public CaptureFrameReadbackBufferPool Pool;
            public UnityRenderTextureReadbackDispatcher Dispatcher;
            public RenderTexture Texture;

            public ReadbackScope(int slotCount, int bytesPerSlot = 64)
            {
                Pool = new CaptureFrameReadbackBufferPool(slotCount, bytesPerSlot);
                Dispatcher = new UnityRenderTextureReadbackDispatcher(Pool);
                Texture = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32);
                Texture.Create();
            }

            public CaptureFrameReadbackPayloadLease MakePayload(long captureFrameId)
            {
                Assert.That(Dispatcher.TryStart(MakeRequest(captureFrameId), Texture), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                Assert.That(Dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.True);
                return new CaptureFrameReadbackPayloadLease(Dispatcher, result);
            }

            public void Dispose()
            {
                AsyncGPUReadback.WaitAllRequests();
                if (Texture != null)
                {
                    Texture.Release();
                    UnityEngine.Object.DestroyImmediate(Texture);
                    Texture = null;
                }

                if (Dispatcher != null)
                {
                    Dispatcher.Dispose();
                    Dispatcher = null;
                }

                if (Pool != null)
                {
                    Pool.Dispose();
                    Pool = null;
                }
            }
        }

        // ---- Helpers ----

        private static CaptureFrameRequest MakeRequest(long captureFrameId)
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, 2, 3, 4, captureFrameId, 6, 7, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameEnvelope MakeEnvelope(long captureFrameId)
        {
            CaptureFrameRequest request = MakeRequest(captureFrameId);
            CaptureFrameTiming timing = new CaptureFrameTiming(1.0, 0.01, true, 2.0, 3.0, 4);
            CapturePoseSample head = new CapturePoseSample(new Vector3(1, 2, 3), Quaternion.identity);
            return new CaptureFrameEnvelope(
                request, timing, head, CapturePoseSample.Unavailable, CapturePoseSample.Unavailable,
                8, 9, CaptureColorSpace.Srgb, 91, "build-a", "scene-a", 123);
        }

        private static CaptureFrameWorkToken MakeCompletionToken(long captureFrameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 7, captureFrameId);
        }

        private static PngJsonCaptureEvidenceSubmission MakeSubmission(
            ReadbackScope scope,
            FakeStore store,
            long captureFrameId)
        {
            CaptureFrameEnvelope frame = MakeEnvelope(captureFrameId);
            CaptureFrameReadbackPayloadLease payload = scope.MakePayload(captureFrameId);
            return new PngJsonCaptureEvidenceSubmission(frame, payload, MakeCompletionToken(captureFrameId));
        }

        private static bool WaitForJoin(PngJsonCaptureEvidenceWorkerService service, int timeoutMs = 5000)
        {
            ManualResetEvent signal = new ManualResetEvent(false);
            Action handler = () => signal.Set();
            service.WorkerStopped += handler;
            try
            {
                if (service.TryJoin())
                {
                    return true;
                }

                return signal.WaitOne(timeoutMs);
            }
            finally
            {
                service.WorkerStopped -= handler;
            }
        }

        private static bool WaitForCollect(
            PngJsonCaptureEvidenceWorkerService service,
            out PngJsonCaptureEvidenceWorkCompletion completion,
            int timeoutMs = 5000)
        {
            ManualResetEvent signal = new ManualResetEvent(false);
            Action handler = () => signal.Set();
            service.CompletionEnqueued += handler;
            try
            {
                if (service.TryCollect(out completion))
                {
                    return true;
                }

                if (!signal.WaitOne(timeoutMs))
                {
                    completion = default;
                    return false;
                }

                return service.TryCollect(out completion);
            }
            finally
            {
                service.CompletionEnqueued -= handler;
            }
        }

        private static void ApplyAndAcknowledge(
            PngJsonCaptureEvidenceWorkerService service,
            in PngJsonCaptureEvidenceWorkCompletion completion)
        {
            service.ReleaseInput(completion.WorkToken);
            service.Acknowledge(completion.WorkToken);
        }

        // ---- Tests ----

        [Test]
        public void EncodeMetadataHashAndStore_RunOnDedicatedWorkerThread()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            using (FakeStore store = new FakeStore())
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonCaptureEvidenceWorkerService service =
                    new PngJsonCaptureEvidenceWorkerService(1, encoder, store);
                try
                {
                    int constructingThreadId = Environment.CurrentManagedThreadId;

                    Assert.That(
                        service.TrySubmit(MakeSubmission(scope, store, 1), out CaptureFrameWorkToken token),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(token.IsValid, Is.True);

                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);
                    Assert.That(WaitForCollect(service, out PngJsonCaptureEvidenceWorkCompletion completion), Is.True);

                    int[] encoderThreads = encoder.CallThreadIds;
                    int[] storeThreads = store.CallThreadIds;
                    Assert.That(encoderThreads.Length, Is.EqualTo(1));
                    Assert.That(storeThreads.Length, Is.EqualTo(2));
                    Assert.That(encoderThreads[0], Is.Not.EqualTo(constructingThreadId));
                    Assert.That(storeThreads[0], Is.EqualTo(encoderThreads[0]));
                    Assert.That(storeThreads[1], Is.EqualTo(encoderThreads[0]));

                    ApplyAndAcknowledge(service, completion);
                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void AcceptedWork_CompletesInFifoOrder_WithFrameAndArtifactCorrelation()
        {
            using (ReadbackScope scope = new ReadbackScope(3))
            using (FakeStore store = new FakeStore())
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonCaptureEvidenceWorkerService service =
                    new PngJsonCaptureEvidenceWorkerService(3, encoder, store);
                try
                {
                    Assert.That(service.TrySubmit(MakeSubmission(scope, store, 11), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(service.TrySubmit(MakeSubmission(scope, store, 22), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(service.TrySubmit(MakeSubmission(scope, store, 33), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));

                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    long[] frameIds = new long[3];
                    CaptureFrameWorkToken[] workerTokens = new CaptureFrameWorkToken[3];
                    for (int i = 0; i < 3; i++)
                    {
                        Assert.That(WaitForCollect(service, out PngJsonCaptureEvidenceWorkCompletion completion), Is.True);
                        Assert.That(completion.FrameCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                        Assert.That(completion.FrameCompletion.ProducedArtifactCount, Is.EqualTo(2));
                        Assert.That(completion.ImageArtifact, Is.Not.Null);
                        Assert.That(completion.MetadataArtifact, Is.Not.Null);
                        frameIds[i] = completion.FrameCompletion.CaptureFrameId;
                        workerTokens[i] = completion.WorkToken;
                    }

                    Assert.That(frameIds, Is.EqualTo(new long[] { 11, 22, 33 }));

                    for (int i = 0; i < 3; i++)
                    {
                        service.ReleaseInput(workerTokens[i]);
                        service.Acknowledge(workerTokens[i]);
                    }

                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void MainThreadTryCollect_DoesNotBlockOnEncoder()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            using (FakeStore store = new FakeStore())
            {
                FakeEncoder encoder = new FakeEncoder { Block = true };
                PngJsonCaptureEvidenceWorkerService service =
                    new PngJsonCaptureEvidenceWorkerService(1, encoder, store);
                try
                {
                    Assert.That(service.TrySubmit(MakeSubmission(scope, store, 1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));

                    // The worker has entered the encoder and is blocked. The
                    // main-thread poll must return immediately.
                    encoder.WaitForBlockEntered();
                    Assert.That(service.TryCollect(out _), Is.False);
                    Assert.That(service.TryJoin(), Is.False);

                    encoder.ReleaseBlock();
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);
                    Assert.That(WaitForCollect(service, out PngJsonCaptureEvidenceWorkCompletion completion), Is.True);
                    Assert.That(completion.FrameCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                    ApplyAndAcknowledge(service, completion);
                    service.Dispose();
                }
                finally
                {
                    encoder.ReleaseBlock();
                }
            }
        }

        [Test]
        public void NormalWork_ProducesStagedFrameAndTwoArtifacts_WithExactReceipts()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            using (FakeStore store = new FakeStore())
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonCaptureEvidenceWorkerService service =
                    new PngJsonCaptureEvidenceWorkerService(1, encoder, store);
                try
                {
                    Assert.That(service.TrySubmit(MakeSubmission(scope, store, 7), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);
                    Assert.That(WaitForCollect(service, out PngJsonCaptureEvidenceWorkCompletion completion), Is.True);

                    Assert.That(completion.FrameCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                    Assert.That(completion.FrameCompletion.ProducedArtifactCount, Is.EqualTo(2));
                    Assert.That(completion.FrameCompletion.CaptureFrameId, Is.EqualTo(7));

                    Assert.That(completion.ImageArtifact.Status, Is.EqualTo(CaptureArtifactCompletionStatus.Staged));
                    Assert.That(completion.ImageArtifact.Descriptor.ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameImage));
                    Assert.That(completion.ImageArtifact.StorageReceipt, Is.Not.Null);
                    CaptureArtifactWriteReceipt imageReceipt = (CaptureArtifactWriteReceipt)completion.ImageArtifact.StorageReceipt;
                    Assert.That(imageReceipt.IsIssuedFor(store, completion.ImageArtifact.Descriptor), Is.True);

                    Assert.That(completion.MetadataArtifact.Status, Is.EqualTo(CaptureArtifactCompletionStatus.Staged));
                    Assert.That(completion.MetadataArtifact.Descriptor.ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameMetadata));
                    Assert.That(completion.MetadataArtifact.StorageReceipt, Is.Not.Null);
                    CaptureArtifactWriteReceipt metadataReceipt = (CaptureArtifactWriteReceipt)completion.MetadataArtifact.StorageReceipt;
                    Assert.That(metadataReceipt.IsIssuedFor(store, completion.MetadataArtifact.Descriptor), Is.True);

                    Assert.That(store.Descriptors.Length, Is.EqualTo(2));

                    ApplyAndAcknowledge(service, completion);
                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void EncoderException_FailedFrame_ZeroArtifacts_ThenNextWorkProcessed()
        {
            using (ReadbackScope scope = new ReadbackScope(2))
            using (FakeStore store = new FakeStore())
            {
                FakeEncoder encoder = new FakeEncoder { FailOnCall = 0 };
                PngJsonCaptureEvidenceWorkerService service =
                    new PngJsonCaptureEvidenceWorkerService(2, encoder, store);
                try
                {
                    Assert.That(service.TrySubmit(MakeSubmission(scope, store, 1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(service.TrySubmit(MakeSubmission(scope, store, 2), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureEvidenceWorkCompletion first), Is.True);
                    Assert.That(first.FrameCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
                    Assert.That(first.FrameCompletion.ProducedArtifactCount, Is.EqualTo(0));
                    Assert.That(first.FrameCompletion.CaptureFrameId, Is.EqualTo(1));
                    Assert.That(first.ImageArtifact, Is.Null);
                    Assert.That(first.MetadataArtifact, Is.Null);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureEvidenceWorkCompletion second), Is.True);
                    Assert.That(second.FrameCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                    Assert.That(second.FrameCompletion.CaptureFrameId, Is.EqualTo(2));

                    service.ReleaseInput(first.WorkToken);
                    service.Acknowledge(first.WorkToken);
                    service.ReleaseInput(second.WorkToken);
                    service.Acknowledge(second.WorkToken);
                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void EmptyPng_FailedFrame_ZeroArtifacts()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            using (FakeStore store = new FakeStore())
            {
                FakeEncoder encoder = new FakeEncoder { ReturnEmpty = true };
                PngJsonCaptureEvidenceWorkerService service =
                    new PngJsonCaptureEvidenceWorkerService(1, encoder, store);
                try
                {
                    Assert.That(service.TrySubmit(MakeSubmission(scope, store, 5), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);
                    Assert.That(WaitForCollect(service, out PngJsonCaptureEvidenceWorkCompletion completion), Is.True);

                    Assert.That(completion.FrameCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
                    Assert.That(completion.FrameCompletion.ProducedArtifactCount, Is.EqualTo(0));
                    Assert.That(completion.FrameCompletion.Failure, Is.Not.Null);
                    Assert.That(completion.ImageArtifact, Is.Null);
                    Assert.That(completion.MetadataArtifact, Is.Null);
                    Assert.That(store.CallCount, Is.EqualTo(0));

                    service.ReleaseInput(completion.WorkToken);
                    service.Acknowledge(completion.WorkToken);
                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void StoreException_NullReceiptAndForeignReceipt_ConvergeToPerArtifactFailed_FrameSucceeded()
        {
            using (ReadbackScope scope = new ReadbackScope(3))
            using (FakeStore throwing = new FakeStore { ThrowOnWrite = true })
            using (FakeStore nullReceipt = new FakeStore { ReturnNullReceipt = true })
            using (FakeStore foreign = new FakeStore { ReturnForeignReceipt = true })
            {
                FakeEncoder encoder = new FakeEncoder();

                PngJsonCaptureEvidenceWorkerService throwingService =
                    new PngJsonCaptureEvidenceWorkerService(1, encoder, throwing);
                PngJsonCaptureEvidenceWorkerService nullService =
                    new PngJsonCaptureEvidenceWorkerService(1, encoder, nullReceipt);
                PngJsonCaptureEvidenceWorkerService foreignService =
                    new PngJsonCaptureEvidenceWorkerService(1, encoder, foreign);
                try
                {
                    Assert.That(throwingService.TrySubmit(MakeSubmission(scope, throwing, 1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(nullService.TrySubmit(MakeSubmission(scope, nullReceipt, 2), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(foreignService.TrySubmit(MakeSubmission(scope, foreign, 3), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));

                    throwingService.BeginDrain();
                    nullService.BeginDrain();
                    foreignService.BeginDrain();
                    Assert.That(WaitForJoin(throwingService), Is.True);
                    Assert.That(WaitForJoin(nullService), Is.True);
                    Assert.That(WaitForJoin(foreignService), Is.True);

                    Assert.That(WaitForCollect(throwingService, out PngJsonCaptureEvidenceWorkCompletion throwingCompletion), Is.True);
                    Assert.That(throwingCompletion.FrameCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                    Assert.That(throwingCompletion.FrameCompletion.ProducedArtifactCount, Is.EqualTo(2));
                    Assert.That(throwingCompletion.ImageArtifact.Status, Is.EqualTo(CaptureArtifactCompletionStatus.Failed));
                    Assert.That(throwingCompletion.MetadataArtifact.Status, Is.EqualTo(CaptureArtifactCompletionStatus.Failed));
                    Assert.That(throwingCompletion.ImageArtifact.StorageReceipt, Is.Null);
                    Assert.That(throwingCompletion.ImageArtifact.Failure, Is.Not.Null);

                    Assert.That(WaitForCollect(nullService, out PngJsonCaptureEvidenceWorkCompletion nullCompletion), Is.True);
                    Assert.That(nullCompletion.FrameCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                    Assert.That(nullCompletion.FrameCompletion.ProducedArtifactCount, Is.EqualTo(2));
                    Assert.That(nullCompletion.ImageArtifact.Status, Is.EqualTo(CaptureArtifactCompletionStatus.Failed));
                    Assert.That(nullCompletion.MetadataArtifact.Status, Is.EqualTo(CaptureArtifactCompletionStatus.Failed));

                    Assert.That(WaitForCollect(foreignService, out PngJsonCaptureEvidenceWorkCompletion foreignCompletion), Is.True);
                    Assert.That(foreignCompletion.FrameCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                    Assert.That(foreignCompletion.FrameCompletion.ProducedArtifactCount, Is.EqualTo(2));
                    Assert.That(foreignCompletion.ImageArtifact.Status, Is.EqualTo(CaptureArtifactCompletionStatus.Failed));
                    Assert.That(foreignCompletion.MetadataArtifact.Status, Is.EqualTo(CaptureArtifactCompletionStatus.Failed));

                    throwingService.ReleaseInput(throwingCompletion.WorkToken);
                    throwingService.Acknowledge(throwingCompletion.WorkToken);
                    nullService.ReleaseInput(nullCompletion.WorkToken);
                    nullService.Acknowledge(nullCompletion.WorkToken);
                    foreignService.ReleaseInput(foreignCompletion.WorkToken);
                    foreignService.Acknowledge(foreignCompletion.WorkToken);

                    throwingService.Dispose();
                    nullService.Dispose();
                    foreignService.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void CapacityBackpressure_RetainsSubmissionOwnership()
        {
            using (ReadbackScope scope = new ReadbackScope(3))
            using (FakeStore store = new FakeStore())
            {
                FakeEncoder encoder = new FakeEncoder { Block = true };
                PngJsonCaptureEvidenceWorkerService service =
                    new PngJsonCaptureEvidenceWorkerService(1, encoder, store);
                try
                {
                    Assert.That(service.TrySubmit(MakeSubmission(scope, store, 1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    encoder.WaitForBlockEntered();

                    CaptureFrameEnvelope overflowFrame = MakeEnvelope(2);
                    CaptureFrameReadbackPayloadLease overflowPayload = scope.MakePayload(2);
                    PngJsonCaptureEvidenceSubmission overflow =
                        new PngJsonCaptureEvidenceSubmission(overflowFrame, overflowPayload, MakeCompletionToken(2));
                    Assert.That(service.TrySubmit(overflow, out CaptureFrameWorkToken rejectedToken),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Backpressured));
                    Assert.That(rejectedToken.IsValid, Is.False);
                    Assert.That(overflow.HasPayload, Is.True);
                    overflowPayload.ReleaseByCaller();

                    encoder.ReleaseBlock();
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);
                    Assert.That(WaitForCollect(service, out PngJsonCaptureEvidenceWorkCompletion completion), Is.True);
                    ApplyAndAcknowledge(service, completion);

                    service.Dispose();
                }
                finally
                {
                    encoder.ReleaseBlock();
                }
            }
        }

        [Test]
        public void Worker_DoesNotReferenceDispatcherOrTraceApi()
        {
            string source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepositoryRoot(), "Assets", "Zantetsu", "Runtime", "Observability", "PngJsonCaptureEvidenceWorkerService.cs"));
            Assert.That(source, Does.Not.Contain("UnityRenderTextureReadbackDispatcher"));
            Assert.That(source, Does.Not.Contain("CaptureFrameTraceObserver"));
            Assert.That(source, Does.Not.Contain("CaptureFrameDraftRegistry"));
            Assert.That(source, Does.Not.Contain("CaptureArtifactRegistry"));
            Assert.That(source, Does.Not.Contain("List<"));
            Assert.That(source, Does.Not.Contain("Queue<"));
            Assert.That(source, Does.Not.Contain("Stack<"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("ThreadPool"));
        }

        private static string RepositoryRoot()
        {
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
        }
    }
}
