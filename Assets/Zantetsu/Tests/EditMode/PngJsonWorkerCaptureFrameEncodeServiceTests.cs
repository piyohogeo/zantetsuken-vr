using System;
using System.Collections.Generic;
using System.IO;
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
    public class PngJsonWorkerCaptureFrameEncodeServiceTests
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

                return new NativeArray<byte>(5, Allocator.Persistent);
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

            public PngJsonCaptureFrameEncodeSubmission MakeSubmission(long captureFrameId)
            {
                return new PngJsonCaptureFrameEncodeSubmission(MakePayload(captureFrameId));
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

        private static bool WaitForJoin(PngJsonWorkerCaptureFrameEncodeService service, int timeoutMs = 5000)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (!service.TryJoin())
            {
                if (Environment.TickCount - deadline >= 0)
                {
                    return false;
                }

                Thread.Sleep(1);
            }

            return true;
        }

        private static bool WaitForCollect(
            PngJsonWorkerCaptureFrameEncodeService service,
            out PngJsonCaptureFrameEncodeCompletion completion,
            int timeoutMs = 5000)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (true)
            {
                if (service.TryCollect(out completion))
                {
                    return true;
                }

                if (Environment.TickCount - deadline >= 0)
                {
                    completion = default;
                    return false;
                }

                Thread.Sleep(1);
            }
        }

        private static void DrainAndAcknowledgeAll(PngJsonWorkerCaptureFrameEncodeService service)
        {
            service.BeginDrain();
            Assert.That(WaitForJoin(service), Is.True);
            while (WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completion, timeoutMs: 1000))
            {
                if (completion.Status == PngJsonCaptureFrameEncodeCompletionStatus.Succeeded)
                {
                    service.DisposeEncodedPng(completion.WorkToken);
                }

                service.ReleaseInput(completion.WorkToken);
                service.Acknowledge(completion.WorkToken);
            }
        }

        private static string RepositoryRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        // ---- Tests ----

        [Test]
        public void TrySubmit_ReturnsAcceptedBeforeEncodeCompletes()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            {
                FakeEncoder encoder = new FakeEncoder { Block = true };
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1, encoder);
                try
                {
                    PngJsonCaptureFrameEncodeSubmission submission = scope.MakeSubmission(1);
                    Assert.That(
                        service.TrySubmit(submission, out CaptureFrameWorkToken token),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));

                    Assert.That(token.IsValid, Is.True);
                    encoder.WaitForBlockEntered();
                    Assert.That(encoder.CallCount, Is.EqualTo(1));

                    encoder.ReleaseBlock();
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completion), Is.True);
                    Assert.That(completion.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Succeeded));
                    service.DisposeEncodedPng(completion.WorkToken);
                    service.ReleaseInput(completion.WorkToken);
                    service.Acknowledge(completion.WorkToken);
                    service.Dispose();
                }
                finally
                {
                    encoder.ReleaseBlock();
                }
            }
        }

        [Test]
        public void Encoder_RunsOnSingleDedicatedWorkerThread()
        {
            using (ReadbackScope scope = new ReadbackScope(2))
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(2, encoder);
                try
                {
                    int constructingThreadId = Environment.CurrentManagedThreadId;

                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(service.TrySubmit(scope.MakeSubmission(2), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));

                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    Assert.That(encoder.CallCount, Is.EqualTo(2));
                    int[] threadIds = encoder.CallThreadIds;
                    Assert.That(threadIds.Length, Is.EqualTo(2));
                    Assert.That(threadIds[0], Is.Not.EqualTo(constructingThreadId));
                    Assert.That(threadIds[1], Is.EqualTo(threadIds[0]));

                    DrainAndAcknowledgeAll(service);
                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void MultipleAcceptedWork_EncodesAndCompletesInFifoOrder()
        {
            using (ReadbackScope scope = new ReadbackScope(3))
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(3, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(11), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(service.TrySubmit(scope.MakeSubmission(22), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(service.TrySubmit(scope.MakeSubmission(33), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));

                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    long[] collectedFrameIds = new long[3];
                    CaptureFrameWorkToken[] tokens = new CaptureFrameWorkToken[3];
                    for (int i = 0; i < 3; i++)
                    {
                        Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completion), Is.True);
                        Assert.That(completion.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Succeeded));
                        tokens[i] = completion.WorkToken;
                        collectedFrameIds[i] = completion.WorkToken.CaptureFrameId;
                    }

                    Assert.That(collectedFrameIds, Is.EqualTo(new long[] { 11, 22, 33 }));

                    for (int i = 0; i < 3; i++)
                    {
                        service.DisposeEncodedPng(tokens[i]);
                        service.ReleaseInput(tokens[i]);
                        service.Acknowledge(tokens[i]);
                    }

                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void CapacityFull_BackpressuredAndPayloadCallerOwned()
        {
            using (ReadbackScope scope = new ReadbackScope(3))
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(2, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(service.TrySubmit(scope.MakeSubmission(2), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));

                    CaptureFrameReadbackPayloadLease overflowPayload = scope.MakePayload(3);
                    PngJsonCaptureFrameEncodeSubmission overflow =
                        new PngJsonCaptureFrameEncodeSubmission(overflowPayload);
                    Assert.That(service.TrySubmit(overflow, out CaptureFrameWorkToken rejectedToken),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Backpressured));
                    Assert.That(rejectedToken.IsValid, Is.False);
                    Assert.That(overflow.HasPayload, Is.True);

                    overflowPayload.ReleaseByCaller();

                    DrainAndAcknowledgeAll(service);
                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void BeginDrain_NotAcceptingAndPayloadOwnershipRetained()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1, encoder);
                try
                {
                    service.BeginDrain();

                    CaptureFrameReadbackPayloadLease payload = scope.MakePayload(1);
                    PngJsonCaptureFrameEncodeSubmission submission =
                        new PngJsonCaptureFrameEncodeSubmission(payload);
                    Assert.That(service.TrySubmit(submission, out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.NotAccepting));
                    Assert.That(submission.HasPayload, Is.True);

                    payload.ReleaseByCaller();

                    Assert.That(WaitForJoin(service), Is.True);
                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void TryCollect_BeforeCompletion_ReturnsFalse()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            {
                FakeEncoder encoder = new FakeEncoder { Block = true };
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    encoder.WaitForBlockEntered();

                    Assert.That(service.TryCollect(out _), Is.False);

                    encoder.ReleaseBlock();
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);
                    Assert.That(service.TryCollect(out PngJsonCaptureFrameEncodeCompletion completion), Is.True);
                    Assert.That(completion.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Succeeded));
                    service.DisposeEncodedPng(completion.WorkToken);
                    service.ReleaseInput(completion.WorkToken);
                    service.Acknowledge(completion.WorkToken);
                    service.Dispose();
                }
                finally
                {
                    encoder.ReleaseBlock();
                }
            }
        }

        [Test]
        public void SucceededCompletion_AppliesToCoordinator_OnceEach()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            using (TraceLogger logger = new TraceLogger(8))
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completion), Is.True);
                    Assert.That(completion.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Succeeded));

                    PngJsonCaptureFrameEncodeCompletionCoordinator coordinator =
                        new PngJsonCaptureFrameEncodeCompletionCoordinator(service, new CaptureFrameTraceObserver(logger));

                    PngJsonCaptureFrameEncodeApplyResult applied = coordinator.Apply(completion);
                    NativeArray<byte> png = applied.PngBytes;
                    Assert.That(png.IsCreated, Is.True);
                    Assert.That(png.Length, Is.GreaterThan(0));
                    png.Dispose();

                    Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                    Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(1));
                    Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));

                    Assert.Throws<InvalidOperationException>(() => coordinator.Apply(completion));

                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void EncoderException_FailedCompletion_SameExceptionPropagates()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            using (TraceLogger logger = new TraceLogger(8))
            {
                FakeEncoder encoder = new FakeEncoder { FailOnCall = 0 };
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completion), Is.True);
                    Assert.That(completion.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Failed));

                    PngJsonCaptureFrameEncodeCompletionCoordinator coordinator =
                        new PngJsonCaptureFrameEncodeCompletionCoordinator(service, new CaptureFrameTraceObserver(logger));

                    InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                        () => coordinator.Apply(completion));
                    Assert.That(ex.Message, Is.EqualTo("fake encode failure"));

                    Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                    Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));

                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void AfterEncodeFailure_WorkerProcessesNextAcceptedWork()
        {
            using (ReadbackScope scope = new ReadbackScope(2))
            {
                FakeEncoder encoder = new FakeEncoder { FailOnCall = 0 };
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(2, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(service.TrySubmit(scope.MakeSubmission(2), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion first), Is.True);
                    Assert.That(first.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Failed));
                    Assert.That(first.WorkToken.CaptureFrameId, Is.EqualTo(1));

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion second), Is.True);
                    Assert.That(second.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Succeeded));
                    Assert.That(second.WorkToken.CaptureFrameId, Is.EqualTo(2));

                    service.ReleaseInput(first.WorkToken);
                    service.Acknowledge(first.WorkToken);
                    service.DisposeEncodedPng(second.WorkToken);
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
        public void CancelQueued_CancelsQueuedOnly_NotEncoding()
        {
            using (ReadbackScope scope = new ReadbackScope(2))
            {
                FakeEncoder encoder = new FakeEncoder { Block = true };
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(2, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(service.TrySubmit(scope.MakeSubmission(2), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));

                    encoder.WaitForBlockEntered();
                    Assert.That(service.CancelQueued(), Is.EqualTo(1));

                    encoder.ReleaseBlock();
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion first), Is.True);
                    Assert.That(first.WorkToken.CaptureFrameId, Is.EqualTo(1));
                    Assert.That(first.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Succeeded));

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion second), Is.True);
                    Assert.That(second.WorkToken.CaptureFrameId, Is.EqualTo(2));
                    Assert.That(second.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Cancelled));

                    service.DisposeEncodedPng(first.WorkToken);
                    service.ReleaseInput(first.WorkToken);
                    service.Acknowledge(first.WorkToken);
                    service.ReleaseInput(second.WorkToken);
                    service.Acknowledge(second.WorkToken);

                    service.Dispose();
                }
                finally
                {
                    encoder.ReleaseBlock();
                }
            }
        }

        [Test]
        public void CancelledWork_CompletionExactlyOnce_InputReleasedOnApply()
        {
            using (ReadbackScope scope = new ReadbackScope(2))
            using (TraceLogger logger = new TraceLogger(8))
            {
                FakeEncoder encoder = new FakeEncoder { Block = true };
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(2, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(service.TrySubmit(scope.MakeSubmission(2), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    encoder.WaitForBlockEntered();
                    Assert.That(service.CancelQueued(), Is.EqualTo(1));
                    encoder.ReleaseBlock();
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion first), Is.True);
                    Assert.That(first.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Succeeded));
                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion cancelled), Is.True);
                    Assert.That(cancelled.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Cancelled));

                    PngJsonCaptureFrameEncodeCompletionCoordinator coordinator =
                        new PngJsonCaptureFrameEncodeCompletionCoordinator(service, new CaptureFrameTraceObserver(logger));

                    Assert.That(scope.Pool.RentedCount, Is.EqualTo(2));

                    Assert.Throws<OperationCanceledException>(() => coordinator.Apply(cancelled));
                    Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));

                    PngJsonCaptureFrameEncodeApplyResult applied = coordinator.Apply(first);
                    applied.PngBytes.Dispose();
                    Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));

                    service.Dispose();
                }
                finally
                {
                    encoder.ReleaseBlock();
                }
            }
        }

        [Test]
        public void BlockedEncoder_TryJoinFalse_UntilDrained()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            {
                FakeEncoder encoder = new FakeEncoder { Block = true };
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    encoder.WaitForBlockEntered();

                    service.BeginDrain();
                    Assert.That(service.TryJoin(), Is.False);

                    encoder.ReleaseBlock();
                    Assert.That(WaitForJoin(service), Is.True);
                    Assert.That(service.TryJoin(), Is.True);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completion), Is.True);
                    service.DisposeEncodedPng(completion.WorkToken);
                    service.ReleaseInput(completion.WorkToken);
                    service.Acknowledge(completion.WorkToken);
                    service.Dispose();
                }
                finally
                {
                    encoder.ReleaseBlock();
                }
            }
        }

        [Test]
        public void UncollectedCompletion_DoesNotPreventJoin()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    Assert.That(service.TryJoin(), Is.True);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completion), Is.True);
                    service.DisposeEncodedPng(completion.WorkToken);
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
        public void SlotReuse_BeforeAcknowledgeUnavailable_AfterAcknowledgeAvailable()
        {
            using (ReadbackScope scope = new ReadbackScope(3))
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1, encoder);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completion), Is.True);

                    // Before acknowledgement the slot stays Collected: a new
                    // submission is backpressured.
                    CaptureFrameReadbackPayloadLease overflowPayload = scope.MakePayload(2);
                    PngJsonCaptureFrameEncodeSubmission overflow =
                        new PngJsonCaptureFrameEncodeSubmission(overflowPayload);
                    Assert.That(service.TrySubmit(overflow, out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Backpressured));
                    overflowPayload.ReleaseByCaller();

                    service.DisposeEncodedPng(completion.WorkToken);
                    service.ReleaseInput(completion.WorkToken);
                    service.Acknowledge(completion.WorkToken);

                    // After acknowledgement the slot is free again.
                    Assert.That(service.TrySubmit(scope.MakeSubmission(3), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));

                    DrainAndAcknowledgeAll(service);
                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void StaleForeignDuplicateToken_DoesNotAffectCurrentWork()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            using (TraceLogger logger = new TraceLogger(8))
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1, encoder);
                try
                {
                    PngJsonCaptureFrameEncodeCompletionCoordinator coordinator =
                        new PngJsonCaptureFrameEncodeCompletionCoordinator(service, new CaptureFrameTraceObserver(logger));

                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completionA), Is.True);
                    PngJsonCaptureFrameEncodeApplyResult appliedA = coordinator.Apply(completionA);
                    appliedA.PngBytes.Dispose();

                    // Work B reuses the same slot with the next generation.
                    Assert.That(service.TrySubmit(scope.MakeSubmission(2), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completionB), Is.True);

                    // Stale completion A must be rejected without touching B.
                    Assert.Throws<InvalidOperationException>(() => coordinator.Apply(completionA));

                    PngJsonCaptureFrameEncodeApplyResult appliedB = coordinator.Apply(completionB);
                    appliedB.PngBytes.Dispose();
                    Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));

                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);
                    service.Dispose();
                }
                finally
                {
                }
            }
        }

        [Test]
        public void Dispose_RejectsLiveWorkerAndOutstandingSlots_ThenIdempotent()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            {
                FakeEncoder encoder = new FakeEncoder();
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1, encoder);

                Assert.Throws<InvalidOperationException>(() => service.Dispose());

                Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                    Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                service.BeginDrain();
                Assert.That(WaitForJoin(service), Is.True);

                Assert.Throws<InvalidOperationException>(() => service.Dispose());

                Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completion), Is.True);
                service.DisposeEncodedPng(completion.WorkToken);
                service.ReleaseInput(completion.WorkToken);
                service.Acknowledge(completion.WorkToken);

                service.Dispose();
                service.Dispose();
            }
        }

        [Test]
        public void Worker_DoesNotReferenceDispatcherApi()
        {
            foreach (FieldInfo field in typeof(PngJsonWorkerCaptureFrameEncodeService)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(UnityRenderTextureReadbackDispatcher)));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameReadbackBufferPool)));
            }

            string source = File.ReadAllText(Path.Combine(
                RepositoryRoot(),
                "Assets", "Zantetsu", "Runtime", "Observability",
                "PngJsonWorkerCaptureFrameEncodeService.cs"));
            Assert.That(source, Does.Not.Contain("UnityRenderTextureReadbackDispatcher"));
            Assert.That(source, Does.Not.Contain(".GetBuffer("));
        }

        [Test]
        public void ProductionEncoder_WorkerProducesPngSentinel()
        {
            using (ReadbackScope scope = new ReadbackScope(1))
            {
                PngJsonWorkerCaptureFrameEncodeService service =
                    new PngJsonWorkerCaptureFrameEncodeService(1);
                try
                {
                    Assert.That(service.TrySubmit(scope.MakeSubmission(1), out _),
                        Is.EqualTo(PngJsonCaptureFrameEncodeSubmitStatus.Accepted));
                    service.BeginDrain();
                    Assert.That(WaitForJoin(service), Is.True);

                    Assert.That(WaitForCollect(service, out PngJsonCaptureFrameEncodeCompletion completion), Is.True);
                    Assert.That(completion.Status, Is.EqualTo(PngJsonCaptureFrameEncodeCompletionStatus.Succeeded));
                    Assert.That(completion.EncodedByteCount, Is.GreaterThan(0));

                    NativeArray<byte> png = service.GetEncodedPng(completion.WorkToken);
                    Assert.That(png.Length, Is.GreaterThanOrEqualTo(8));
                    Assert.That(png[0], Is.EqualTo((byte)0x89));
                    Assert.That(png[1], Is.EqualTo((byte)'P'));
                    Assert.That(png[2], Is.EqualTo((byte)'N'));
                    Assert.That(png[3], Is.EqualTo((byte)'G'));

                    service.DisposeEncodedPng(completion.WorkToken);
                    service.ReleaseInput(completion.WorkToken);
                    service.Acknowledge(completion.WorkToken);
                    service.Dispose();
                }
                finally
                {
                }
            }
        }
    }
}
