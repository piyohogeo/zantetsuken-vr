using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 submission admission linearization
    /// boundary. Uses tiny render target fixtures; no real GPU completion,
    /// NVENC, worker, or native resource is used.
    /// </summary>
    public class NvencSubmissionAdmissionCoordinatorContractTests
    {
        [Test]
        public void Accept_ValidAndTransfersOwnership()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                CaptureFrameWorkToken token = default;
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(7), surface, out token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Accepted));
                    Assert.That(token.IsValid, Is.True);
                    Assert.That(surface.IsBackendOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(1));

                    Assert.That(coordinator.TryDequeue(out NvencSubmissionRecord record), Is.True);
                    Assert.That(record.WorkToken.IdenticalTo(token), Is.True);
                    Assert.That(ReferenceEquals(record.Surface, surface), Is.True);
                    Assert.That(record.IsValidFor(owner, workPool, samplePool), Is.True);
                }
                finally
                {
                    ReleaseSurface(surface, owner, token);
                }
            }
        }

        [Test]
        public void Accept_EighthSucceeds_NinthBackpressured()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(9))
            {
                CaptureSurfaceLease[] surfaces = new CaptureSurfaceLease[9];
                CaptureFrameWorkToken[] tokens = new CaptureFrameWorkToken[9];
                for (int i = 0; i < 9; i++)
                {
                    surfaces[i] = MakeCallerOwnedSurface(renderPool);
                }

                try
                {
                    for (int i = 0; i < 8; i++)
                    {
                        CaptureSubmitStatus accepted = coordinator.TryAccept(MakeFrame(i + 1), surfaces[i], out tokens[i]);
                        Assert.That(accepted, Is.EqualTo(CaptureSubmitStatus.Accepted));
                    }

                    Assert.That(workPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(8));

                    CaptureSubmitStatus ninth = coordinator.TryAccept(MakeFrame(9), surfaces[8], out CaptureFrameWorkToken ninthToken);

                    Assert.That(ninth, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(ninthToken.IsValid, Is.False);
                    Assert.That(surfaces[8].IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(8));
                }
                finally
                {
                    for (int i = 0; i < 9; i++)
                    {
                        ReleaseSurface(surfaces[i], owner, tokens[i]);
                    }
                }
            }
        }

        [Test]
        public void Accept_QueueFull_Backpressured()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryEnqueue(default), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_WorkPoolFull_Backpressured()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(workPool.TryRent(out NvencCaptureWorkSlotLease workLease), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_SamplePoolFull_RollsBackWorkSlot()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(samplePool.TryRent(out NvencEncodeSampleSlotLease sampleLease), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_InitialDraining_NotAccepting()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            Assert.That(state.TryBeginDrain(), Is.True);
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(state, workPool, samplePool, queue, owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.NotAccepting));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_InitialPoisoned_NotAccepting()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            Assert.That(state.TryPoison(), Is.True);
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(state, workPool, samplePool, queue, owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.NotAccepting));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_RejectsNullFrame()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    Assert.Throws<ArgumentNullException>(() => coordinator.TryAccept(null, surface, out CaptureFrameWorkToken token));
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_RejectsNullSurface()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            Assert.Throws<ArgumentNullException>(() => coordinator.TryAccept(MakeFrame(1), null, out CaptureFrameWorkToken token));
            Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void Accept_RejectsNonCallerOwnedSurface()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                CaptureFrameWorkToken token = new CaptureFrameWorkToken(owner, 0, 1, 3, 7);
                surface.TransferToBackend(owner, token);
                try
                {
                    Assert.Throws<ArgumentException>(() => coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken outToken));
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.ReleaseFromBackend(owner, token);
                }
            }
        }

        [Test]
        public void Dequeue_FifoOrder_NoSorting()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            long[] frameIds = { 5, 2, 9, 1 };

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(4))
            {
                CaptureSurfaceLease[] surfaces = new CaptureSurfaceLease[4];
                CaptureFrameWorkToken[] tokens = new CaptureFrameWorkToken[4];
                for (int i = 0; i < 4; i++)
                {
                    surfaces[i] = MakeCallerOwnedSurface(renderPool);
                }

                try
                {
                    for (int i = 0; i < 4; i++)
                    {
                        Assert.That(coordinator.TryAccept(MakeFrame(frameIds[i]), surfaces[i], out tokens[i]), Is.EqualTo(CaptureSubmitStatus.Accepted));
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        Assert.That(coordinator.TryDequeue(out NvencSubmissionRecord record), Is.True);
                        Assert.That(record.WorkToken.CaptureFrameId, Is.EqualTo(frameIds[i]));
                    }
                }
                finally
                {
                    for (int i = 0; i < 4; i++)
                    {
                        ReleaseSurface(surfaces[i], owner, tokens[i]);
                    }
                }
            }
        }

        [Test]
        public void Dequeue_EmptyFalseDefault()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            Assert.That(coordinator.TryDequeue(out NvencSubmissionRecord record), Is.False);
            Assert.That(record.Surface, Is.Null);
            Assert.That(record.WorkToken.IsValid, Is.False);
        }

        [Test]
        public void Accept_QueuePositionLimit_BackpressuredWithoutTransfer()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            FieldInfo writeField = typeof(NvencFixedSpscQueue<NvencSubmissionRecord>).GetField("_writePosition", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo readField = typeof(NvencFixedSpscQueue<NvencSubmissionRecord>).GetField("_readPosition", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(writeField, Is.Not.Null);
            Assert.That(readField, Is.Not.Null);
            writeField.SetValue(queue, long.MaxValue);
            readField.SetValue(queue, long.MaxValue - 5);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Coordinator_NoWaitNoAllocationNoGuidGeneration()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencSubmissionAdmissionCoordinator.cs"));

            Assert.That(source, Does.Not.Contain("Thread.Sleep"));
            Assert.That(source, Does.Not.Contain("new Thread"));
            Assert.That(source, Does.Not.Contain("ThreadPool"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("lock ("));
            Assert.That(source, Does.Not.Contain("Monitor"));
            Assert.That(source, Does.Not.Contain("Interlocked"));
            Assert.That(source, Does.Not.Contain("SpinWait"));
            Assert.That(source, Does.Not.Contain("WaitHandle"));
            Assert.That(source, Does.Not.Contain("ManualResetEvent"));
            Assert.That(source, Does.Not.Contain("AutoResetEvent"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("NvEnc"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("new []"));
            Assert.That(source, Does.Not.Contain("new List"));
            Assert.That(source, Does.Not.Contain("new Dictionary"));
            Assert.That(source, Does.Not.Contain("new byte["));
            Assert.That(source, Does.Not.Contain("Guid.NewGuid"));

            Type type = typeof(NvencSubmissionAdmissionCoordinator);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
        }

        private static NvencSubmissionAdmissionCoordinator MakeCoordinator(
            out NvencCaptureProcessState state,
            out NvencCaptureWorkSlotPool workPool,
            out NvencEncodeSampleSlotPool samplePool,
            out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
            out Guid owner)
        {
            state = new NvencCaptureProcessState();
            workPool = new NvencCaptureWorkSlotPool(state);
            samplePool = new NvencEncodeSampleSlotPool(state);
            queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            owner = Guid.NewGuid();
            return new NvencSubmissionAdmissionCoordinator(state, workPool, samplePool, queue, owner);
        }

        private static CaptureFrameRenderTargetPool MakeRenderPool(int capacity)
        {
            return new CaptureFrameRenderTargetPool(
                capacity, CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(9, new CaptureImageRect(0, 0, 2, 2)));
        }

        private static CaptureSurfaceLease MakeCallerOwnedSurface(CaptureFrameRenderTargetPool pool)
        {
            Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease rtLease), Is.True);
            return new CaptureSurfaceLease(pool, rtLease);
        }

        private static CaptureFrameEnvelope MakeFrame(long captureFrameId, long testRunId = 3)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                10, 20, 4, 1, captureFrameId, 30, testRunId, 40, 50, 60, 2, 70);
            CaptureFrameRequest request = new CaptureFrameRequest(
                context, CaptureSource.UnityRenderTexture, CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
            CaptureFrameTiming timing = new CaptureFrameTiming(1.0, 0.01, true, 2.0, 3.0, 4);
            CapturePoseSample head = new CapturePoseSample(new Vector3(1, 2, 3), Quaternion.identity);
            return new CaptureFrameEnvelope(
                request, timing, head, CapturePoseSample.Unavailable, CapturePoseSample.Unavailable,
                8, 9, CaptureColorSpace.Srgb, 91, "build-a", "scene-a", 123);
        }

        private static void ReleaseSurface(CaptureSurfaceLease surface, Guid owner, CaptureFrameWorkToken token)
        {
            if (surface == null || !surface.IsCreated)
            {
                return;
            }

            if (surface.IsBackendOwned)
            {
                surface.ReleaseFromBackend(owner, token);
            }
            else
            {
                surface.Dispose();
            }
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }
    }
}
