using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinatorTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const long SubmitTimestamp = 1000;
        private const long SubmitUnityFrameId = 200;
        private const long SubmitFixedStepId = 300;
        private const int SubmitThreadId = 4;
        private const long SubmitOpenXRFrameId = 500;
        private const long SubmitSlashId = 600;
        private const long SubmitFrontEdgeId = 700;
        private const long SubmitObjectId = 800;
        private const uint SubmitObjectGeneration = 9;
        private const long SubmitTaskId = 1000;

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

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
                logger.Enqueue(Event(1));
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

        private static CaptureFrameProfile MakeProfile()
        {
            return CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
        }

        private static CaptureFrameTiming MakeTiming(double predictedDisplayTimeSeconds, bool shouldRender)
        {
            return new CaptureFrameTiming(predictedDisplayTimeSeconds, 1.0 / 90.0, shouldRender, 0.0, 0.0, 0L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId = 42, long testRunId = 1)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, captureFrameId, 30, testRunId, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(
                context,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRequest MakeAcceptedRequest(long captureFrameId, long testRunId)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                SubmitTimestamp,
                SubmitUnityFrameId,
                SubmitFixedStepId,
                SubmitThreadId,
                captureFrameId,
                SubmitOpenXRFrameId,
                testRunId,
                SubmitSlashId,
                SubmitFrontEdgeId,
                SubmitObjectId,
                SubmitObjectGeneration,
                SubmitTaskId);

            return new CaptureFrameRequest(
                context,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static void AssertRequestIdentical(in CaptureFrameRequest expected, in CaptureFrameRequest actual)
        {
            CaptureFrameTraceContext e = expected.TraceContext;
            CaptureFrameTraceContext a = actual.TraceContext;

            Assert.That(a.Timestamp, Is.EqualTo(e.Timestamp));
            Assert.That(a.UnityFrameId, Is.EqualTo(e.UnityFrameId));
            Assert.That(a.FixedStepId, Is.EqualTo(e.FixedStepId));
            Assert.That(a.ThreadId, Is.EqualTo(e.ThreadId));
            Assert.That(a.CaptureFrameId, Is.EqualTo(e.CaptureFrameId));
            Assert.That(a.OpenXRFrameId, Is.EqualTo(e.OpenXRFrameId));
            Assert.That(a.TestRunId, Is.EqualTo(e.TestRunId));
            Assert.That(a.SlashId, Is.EqualTo(e.SlashId));
            Assert.That(a.FrontEdgeId, Is.EqualTo(e.FrontEdgeId));
            Assert.That(a.ObjectId, Is.EqualTo(e.ObjectId));
            Assert.That(a.ObjectGeneration, Is.EqualTo(e.ObjectGeneration));
            Assert.That(a.TaskId, Is.EqualTo(e.TaskId));

            Assert.That(actual.Source, Is.EqualTo(expected.Source));
            Assert.That(actual.Eye, Is.EqualTo(expected.Eye));
            Assert.That(actual.ImageRect.X, Is.EqualTo(expected.ImageRect.X));
            Assert.That(actual.ImageRect.Y, Is.EqualTo(expected.ImageRect.Y));
            Assert.That(actual.ImageRect.Width, Is.EqualTo(expected.ImageRect.Width));
            Assert.That(actual.ImageRect.Height, Is.EqualTo(expected.ImageRect.Height));
            Assert.That(actual.ArrayIndex, Is.EqualTo(expected.ArrayIndex));
            Assert.That(actual.PixelLayout.Format, Is.EqualTo(expected.PixelLayout.Format));
            Assert.That(actual.PixelLayout.Width, Is.EqualTo(expected.PixelLayout.Width));
            Assert.That(actual.PixelLayout.Height, Is.EqualTo(expected.PixelLayout.Height));
            Assert.That(actual.PixelLayout.BytesPerPixel, Is.EqualTo(expected.PixelLayout.BytesPerPixel));
            Assert.That(actual.PixelLayout.RowStrideBytes, Is.EqualTo(expected.PixelLayout.RowStrideBytes));
            Assert.That(actual.PixelLayout.ByteCount, Is.EqualTo(expected.PixelLayout.ByteCount));
            Assert.That(actual.RequiredByteCount, Is.EqualTo(expected.RequiredByteCount));
        }

        private static void FillSolidColor(RenderTexture rt, Color32 color)
        {
            int width = rt.width;
            int height = rt.height;
            Texture2D temp = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                Color32[] pixels = new Color32[width * height];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = color;
                }

                temp.SetPixels32(pixels);
                temp.Apply();
                Graphics.CopyTexture(temp, rt);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-rt-cadenced-pipeline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static Exception DeleteTempDir(string dir)
        {
            if (dir == null || !Directory.Exists(dir))
            {
                return null;
            }

            try
            {
                Directory.Delete(dir, true);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static Exception[] AppendCleanupException(Exception[] cleanupExceptions, Exception ex)
        {
            if (ex == null)
            {
                return cleanupExceptions;
            }

            if (cleanupExceptions == null || cleanupExceptions.Length == 0)
            {
                return new[] { ex };
            }

            Exception[] combined = new Exception[cleanupExceptions.Length + 1];
            Array.Copy(cleanupExceptions, combined, cleanupExceptions.Length);
            combined[cleanupExceptions.Length] = ex;
            return combined;
        }

        private static Exception[] ConcatExceptions(Exception[] first, Exception[] second)
        {
            if (first == null || first.Length == 0)
            {
                return second ?? new Exception[0];
            }

            if (second == null || second.Length == 0)
            {
                return first;
            }

            Exception[] combined = new Exception[first.Length + second.Length];
            Array.Copy(first, combined, first.Length);
            Array.Copy(second, 0, combined, first.Length, second.Length);
            return combined;
        }

        private static void ThrowCleanupAndBody(ExceptionDispatchInfo bodyException, Exception[] cleanupExceptions)
        {
            bool hasBody = bodyException != null;
            bool hasCleanup = cleanupExceptions != null && cleanupExceptions.Length > 0;

            if (hasBody && hasCleanup)
            {
                Exception[] all = new Exception[cleanupExceptions.Length + 1];
                all[0] = bodyException.SourceException;
                Array.Copy(cleanupExceptions, 0, all, 1, cleanupExceptions.Length);
                throw new AggregateException(all);
            }

            if (hasBody)
            {
                bodyException.Throw();
            }
            else if (hasCleanup)
            {
                if (cleanupExceptions.Length == 1)
                {
                    ExceptionDispatchInfo.Capture(cleanupExceptions[0]).Throw();
                }
                else
                {
                    throw new AggregateException(cleanupExceptions);
                }
            }
        }

        private static CaptureFramePngArtifactPersistenceCoordinator MakePersistenceCoordinator(CaptureFrameRecordRegistry registry, string dir)
        {
            CaptureFramePngArtifactQueuePreparer queuePreparer = new CaptureFramePngArtifactQueuePreparer(
                new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore())));

            CaptureFramePngArtifactQueueCompletionWriter queueCompletionWriter = new CaptureFramePngArtifactQueueCompletionWriter(
                new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore())));

            CaptureFramePngArtifactPersistencePump pump = new CaptureFramePngArtifactPersistencePump(queuePreparer, queueCompletionWriter);
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory(dir);

            return new CaptureFramePngArtifactPersistenceCoordinator(pump, factory);
        }

        private static string ExpectedPngName(long captureFrameId)
        {
            return "capture-00000000000000000001-" + captureFrameId.ToString("D20", CultureInfo.InvariantCulture) + ".png";
        }

        private static string ExpectedSidecarName(long captureFrameId)
        {
            return "capture-00000000000000000001-" + captureFrameId.ToString("D20", CultureInfo.InvariantCulture) + ".json";
        }

        private static void PointSchedulerLeaseRegistry(CaptureFrameRenderTargetRecordScheduler scheduler, CaptureFrameRenderTargetLeaseRegistry registry)
        {
            FieldInfo field = typeof(CaptureFrameRenderTargetRecordScheduler).GetField("_leaseRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(scheduler, registry);
        }

        private static void PointPumpLeaseRegistry(CaptureFrameRenderTargetReadbackPump pump, CaptureFrameRenderTargetLeaseRegistry registry)
        {
            FieldInfo field = typeof(CaptureFrameRenderTargetReadbackPump).GetField("_leaseRegistry", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(pump, registry);
        }

        private sealed class RegisteredEntry
        {
            public readonly CaptureFrameRequest Request;
            public readonly CaptureFrameRenderTargetLease Lease;

            public RegisteredEntry(CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
            {
                Request = request;
                Lease = lease;
            }
        }

        private sealed class Harness
        {
            public string Dir;
            public TraceRunManifest Manifest;
            public TraceLogger Logger;
            public CaptureFrameTraceObserver Observer;
            public CaptureFrameRequestQueue Queue;
            public CaptureFrameRequestScheduler RequestScheduler;
            public CaptureFrameRecordRegistry RecordRegistry;
            public CaptureFrameRecordScheduler RecordScheduler;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameRenderTargetLeaseRegistry LeaseRegistry;
            public CaptureFrameRenderTargetRecordScheduler LeaseScheduler;
            public CaptureFrameIdSequence Sequence;
            public CaptureFrameRecordFactory Factory;
            public CaptureFrameRenderTargetRecordSubmissionCoordinator Submission;
            public CaptureFrameCadenceSelector Selector;
            public CaptureFrameRenderTargetCadencedSubmissionCoordinator CadencedSubmission;
            public CaptureFrameReadbackBufferPool BufferPool;
            public UnityRenderTextureReadbackDispatcher Dispatcher;
            public PngJsonCaptureFrameReadbackCompletionRouter Router;
            public CaptureFrameRenderTargetReadbackPump Pump;
            public PngJsonCaptureFrameRenderTargetPipelineCoordinator Pipeline;
            public CaptureFramePngQueue PngQueue;
            public CaptureFramePngArtifactQueue ArtifactQueue;
            public CaptureFramePngArtifactPersistenceCoordinator Persistence;
            public PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator Coordinator;
            public readonly List<CaptureFrameRenderTargetLease> Held = new List<CaptureFrameRenderTargetLease>();
            public readonly List<RegisteredEntry> Registered = new List<RegisteredEntry>();

            public CaptureFrameRenderTargetLease RentHeld()
            {
                Assert.That(Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                Held.Add(lease);
                return lease;
            }

            public void RemoveHeld(CaptureFrameRenderTargetLease lease)
            {
                for (int i = Held.Count - 1; i >= 0; i--)
                {
                    if (Held[i].SlotIndex == lease.SlotIndex)
                    {
                        Held.RemoveAt(i);
                        return;
                    }
                }
            }

            public void TrackRegistered(CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
            {
                RemoveHeld(lease);
                Registered.Add(new RegisteredEntry(request, lease));
            }
        }

        private static Harness MakeHarness(
            double targetFps,
            int poolCapacity,
            int leaseCapacity,
            int recordCapacity,
            int queueCapacity,
            int bufferPoolCapacity,
            int pngQueueCapacity = 4)
        {
            Harness h = new Harness();
            h.Dir = CreateTempDir();

            TraceRunManifest manifest = MakeManifest(1);
            h.Manifest = manifest;
            CaptureRunReference run = MakeRun(manifest);

            h.Logger = new TraceLogger(16);
            h.Observer = new CaptureFrameTraceObserver(h.Logger);

            h.Queue = new CaptureFrameRequestQueue(queueCapacity);
            h.RequestScheduler = new CaptureFrameRequestScheduler(h.Queue, h.Observer);
            h.RecordRegistry = new CaptureFrameRecordRegistry(recordCapacity);
            h.RecordScheduler = new CaptureFrameRecordScheduler(h.RequestScheduler, h.RecordRegistry, h.Observer);
            h.Pool = new CaptureFrameRenderTargetPool(poolCapacity, MakeProfile());
            h.LeaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(leaseCapacity, h.Pool);
            h.LeaseScheduler = new CaptureFrameRenderTargetRecordScheduler(h.RecordScheduler, h.LeaseRegistry);

            h.Sequence = new CaptureFrameIdSequence();
            h.Factory = new CaptureFrameRecordFactory(
                run,
                h.Sequence,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
            h.Submission = new CaptureFrameRenderTargetRecordSubmissionCoordinator(h.Factory, h.LeaseScheduler);
            h.Selector = new CaptureFrameCadenceSelector(targetFps);
            h.CadencedSubmission = new CaptureFrameRenderTargetCadencedSubmissionCoordinator(h.Selector, h.Submission);

            h.BufferPool = new CaptureFrameReadbackBufferPool(bufferPoolCapacity, 64);
            h.Dispatcher = new UnityRenderTextureReadbackDispatcher(h.BufferPool);
            h.Router = new PngJsonCaptureFrameReadbackCompletionRouter(h.Dispatcher, h.Observer);
            h.Pump = new CaptureFrameRenderTargetReadbackPump(h.Queue, h.Dispatcher, h.LeaseRegistry, h.Pool);

            h.PngQueue = new CaptureFramePngQueue(pngQueueCapacity);
            h.ArtifactQueue = new CaptureFramePngArtifactQueue(4);
            h.Persistence = MakePersistenceCoordinator(h.RecordRegistry, h.Dir);

            h.Pipeline = new PngJsonCaptureFrameRenderTargetPipelineCoordinator(
                h.Pump,
                h.Router,
                h.Persistence,
                h.PngQueue,
                h.ArtifactQueue,
                h.RecordRegistry,
                h.LeaseRegistry,
                h.Pool);

            h.Coordinator = new PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator(
                h.Pipeline,
                h.CadencedSubmission,
                h.Queue,
                h.LeaseRegistry);

            return h;
        }

        private static Exception[] CleanupHarness(Harness h)
        {
            Exception[] errors = null;
            bool gpuSafe = true;

            try
            {
                AsyncGPUReadback.WaitAllRequests();
            }
            catch (Exception ex)
            {
                gpuSafe = false;
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (h.Dispatcher.IsCreated)
                {
                    while (h.Dispatcher.TryCollect(out CaptureFrameReadbackResult result))
                    {
                        h.Dispatcher.Release(result);
                    }
                }
            }
            catch (Exception ex)
            {
                gpuSafe = false;
                errors = AppendCleanupException(errors, ex);
            }

            if (gpuSafe)
            {
                for (int i = h.Registered.Count - 1; i >= 0; i--)
                {
                    RegisteredEntry entry = h.Registered[i];
                    h.Registered.RemoveAt(i);
                    try
                    {
                        if (h.LeaseRegistry.TryRemove(entry.Request, out CaptureFrameRenderTargetLease lease))
                        {
                            h.Pool.Return(lease);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

                for (int i = h.Held.Count - 1; i >= 0; i--)
                {
                    CaptureFrameRenderTargetLease lease = h.Held[i];
                    h.Held.RemoveAt(i);
                    try
                    {
                        h.Pool.Return(lease);
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }
            }

            try
            {
                if (h.PngQueue != null && h.PngQueue.IsCreated)
                {
                    h.PngQueue.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (h.Dispatcher.IsCreated)
                {
                    h.Dispatcher.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (h.BufferPool.IsCreated)
                {
                    h.BufferPool.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                h.Pool.Dispose();
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (h.Logger != null && h.Logger.IsCreated)
                {
                    h.Logger.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            if (h.Dir != null)
            {
                errors = AppendCleanupException(errors, DeleteTempDir(h.Dir));
            }

            return errors;
        }

        private static void RunHarnessBody(Harness h, Action body)
        {
            ExceptionDispatchInfo bodyException = null;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }

            Exception[] errors = CleanupHarness(h);
            ThrowCleanupAndBody(bodyException, errors);
        }

        private static CaptureFrameCadencedPipelineResult Submit(
            PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator coordinator,
            in CaptureFrameRenderTargetLease lease,
            double predictedDisplayTimeSeconds,
            bool shouldRender,
            int commitPathId = 1)
        {
            return coordinator.TrySubmit(
                SubmitTimestamp,
                SubmitUnityFrameId,
                SubmitFixedStepId,
                SubmitThreadId,
                SubmitOpenXRFrameId,
                SubmitSlashId,
                SubmitFrontEdgeId,
                SubmitObjectId,
                SubmitObjectGeneration,
                SubmitTaskId,
                MakeTiming(predictedDisplayTimeSeconds, shouldRender),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                commitPathId,
                lease);
        }

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 2);
            RunHarnessBody(h, () =>
            {
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator(null, h.CadencedSubmission, h.Queue, h.LeaseRegistry));
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator(h.Pipeline, null, h.Queue, h.LeaseRegistry));
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator(h.Pipeline, h.CadencedSubmission, null, h.LeaseRegistry));
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator(h.Pipeline, h.CadencedSubmission, h.Queue, null));
            });
        }

        [Test]
        public void NotSelected_DefaultAndStaleLease_NoValidation_CallerCanReturn()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 2);
            RunHarnessBody(h, () =>
            {
                CaptureFrameCadencedPipelineResult r1 = Submit(h.Coordinator, default, 0.0, false);
                Assert.That(r1.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(r1.ReadbackStarted, Is.False);
                Assert.That(r1.AcceptedRecord, Is.Null);

                CaptureFrameRenderTargetLease stale = h.RentHeld();
                h.Pool.Return(stale);
                h.RemoveHeld(stale);

                CaptureFrameCadencedPipelineResult r2 = Submit(h.Coordinator, stale, 1.0 / 90.0, false);
                Assert.That(r2.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(r2.ReadbackStarted, Is.False);
                Assert.That(r2.AcceptedRecord, Is.Null);

                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(h.Queue.Count, Is.EqualTo(0));
                Assert.That(h.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(h.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(h.Sequence.LastIssued, Is.EqualTo(0));
            });
        }

        [Test]
        public void Backpressured_NoRecord_NoStart_LeaseReturnable()
        {
            Harness h = MakeHarness(45.0, 4, 1, 4, 4, 2);
            RunHarnessBody(h, () =>
            {
                CaptureFrameRenderTargetLease fill = h.RentHeld();
                Assert.That(h.LeaseRegistry.TryRegister(MakeRequest(99), fill), Is.True);
                h.TrackRegistered(MakeRequest(99), fill);

                CaptureFrameRenderTargetLease lease = h.RentHeld();
                CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, lease, 0.0, true);

                Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Backpressured));
                Assert.That(result.AcceptedRecord, Is.Null);
                Assert.That(result.ReadbackStarted, Is.False);
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(h.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(h.RecordRegistry.Count, Is.EqualTo(0));

                h.Pool.Return(lease);
                h.RemoveHeld(lease);
                Assert.That(h.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void Submitted_RecordQueueLeaseMatch_StartFalseKeepsState()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 1);
            RunHarnessBody(h, () =>
            {
                Assert.That(h.BufferPool.TryRent(out int reservedSlot), Is.True);
                try
                {
                    CaptureFrameRenderTargetLease lease = h.RentHeld();
                    CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, lease, 0.0, true);
                    if (result.SubmissionStatus == CaptureFrameCadencedSubmissionStatus.Submitted)
                    {
                        h.TrackRegistered(result.AcceptedRecord.Request, lease);
                    }

                    Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                    Assert.That(result.ReadbackStarted, Is.False);
                    Assert.That(result.AcceptedRecord, Is.Not.Null);

                    Assert.That(h.RecordRegistry.Count, Is.EqualTo(1));
                    Assert.That(h.Queue.Count, Is.EqualTo(1));
                    Assert.That(h.Queue.TryPeek(out CaptureFrameRequest head), Is.True);
                    AssertRequestIdentical(head, result.AcceptedRecord.Request);

                    Assert.That(h.LeaseRegistry.Count, Is.EqualTo(1));
                    Assert.That(h.LeaseRegistry.TryGet(result.AcceptedRecord.Request, out CaptureFrameRenderTargetLease registeredLease), Is.True);
                    Assert.That(registeredLease.SlotIndex, Is.EqualTo(lease.SlotIndex));
                    Assert.That(h.Pool.RentedCount, Is.EqualTo(1));
                    Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(0));
                }
                finally
                {
                    h.BufferPool.Return(reservedSlot);
                }
            });
        }

        [Test]
        public void StartSuccess_QueueEmpty_RecordLeasePoolKept()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 2);
            RunHarnessBody(h, () =>
            {
                CaptureFrameRenderTargetLease lease = h.RentHeld();
                CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, lease, 0.0, true);
                if (result.SubmissionStatus == CaptureFrameCadencedSubmissionStatus.Submitted)
                {
                    h.TrackRegistered(result.AcceptedRecord.Request, lease);
                }

                Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(result.ReadbackStarted, Is.True);

                Assert.That(h.Queue.Count, Is.EqualTo(0));
                Assert.That(h.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(h.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(h.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void RetryStart_AfterBufferReturn_StartsSameRegisteredTarget()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 1);
            RunHarnessBody(h, () =>
            {
                Assert.That(h.BufferPool.TryRent(out int reservedSlot), Is.True);
                bool slotHeld = true;
                try
                {
                    CaptureFrameRenderTargetLease lease = h.RentHeld();
                    CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, lease, 0.0, true);
                    if (result.SubmissionStatus == CaptureFrameCadencedSubmissionStatus.Submitted)
                    {
                        h.TrackRegistered(result.AcceptedRecord.Request, lease);
                    }

                    Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                    Assert.That(result.ReadbackStarted, Is.False);
                    Assert.That(h.Queue.Count, Is.EqualTo(1));
                    Assert.That(h.LeaseRegistry.Count, Is.EqualTo(1));
                    Assert.That(h.RecordRegistry.Count, Is.EqualTo(1));

                    h.BufferPool.Return(reservedSlot);
                    slotHeld = false;

                    Assert.That(h.Pipeline.TryStartNextReadback(), Is.True);
                    Assert.That(h.Queue.Count, Is.EqualTo(0));
                    Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(1));
                    Assert.That(h.LeaseRegistry.Count, Is.EqualTo(1));
                    Assert.That(h.Pool.RentedCount, Is.EqualTo(1));
                }
                finally
                {
                    if (slotHeld)
                    {
                        h.BufferPool.Return(reservedSlot);
                    }
                }
            });
        }

        [Test]
        public void PreviousPendingRequest_FailClosed_NoCadenceIdLeaseTouch()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 2);
            RunHarnessBody(h, () =>
            {
                Assert.That(h.Queue.TryEnqueue(MakeRequest(99)), Is.True);

                CaptureFrameRenderTargetLease lease = h.RentHeld();
                Assert.Throws<InvalidOperationException>(() => Submit(h.Coordinator, lease, 0.0, true));

                Assert.That(h.Sequence.LastIssued, Is.EqualTo(0));
                Assert.That(h.Selector.HasObservedTimestamp, Is.False);
                Assert.That(h.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(h.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(h.Queue.Count, Is.EqualTo(1));

                h.Pool.Return(lease);
                h.RemoveHeld(lease);
            });
        }

        [Test]
        public void AdvanceRunsFirst_CollectsCompletedBeforeSubmit()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 1);
            RunHarnessBody(h, () =>
            {
                CaptureFrameRenderTargetLease l1 = h.RentHeld();
                CaptureFrameCadencedPipelineResult r1 = Submit(h.Coordinator, l1, 0.0, true);
                if (r1.SubmissionStatus == CaptureFrameCadencedSubmissionStatus.Submitted)
                {
                    h.TrackRegistered(r1.AcceptedRecord.Request, l1);
                }

                Assert.That(r1.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(r1.ReadbackStarted, Is.True);

                AsyncGPUReadback.WaitAllRequests();
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(1));

                CaptureFrameRenderTargetLease l2 = h.RentHeld();
                CaptureFrameCadencedPipelineResult r2 = Submit(h.Coordinator, l2, 0.03, true);
                if (r2.SubmissionStatus == CaptureFrameCadencedSubmissionStatus.Submitted)
                {
                    h.TrackRegistered(r2.AcceptedRecord.Request, l2);
                }

                // With a single dispatcher slot, the second readback can only
                // start if the advance inside the second submit collected frame
                // 1's completed readback first.
                Assert.That(r2.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(r2.ReadbackStarted, Is.True);
                Assert.That(h.PngQueue.Count, Is.EqualTo(1));
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(1));
                Assert.That(h.LeaseRegistry.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void QueueInvariantViolated_FailClosed_BeforeStart()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 2);
            RunHarnessBody(h, () =>
            {
                CaptureFrameRequestQueue altQueue = new CaptureFrameRequestQueue(4);
                PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator coordinator =
                    new PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator(h.Pipeline, h.CadencedSubmission, altQueue, h.LeaseRegistry);

                CaptureFrameRenderTargetLease lease = h.RentHeld();
                Assert.Throws<InvalidOperationException>(() => Submit(coordinator, lease, 0.0, true));

                Assert.That(h.Queue.TryPeek(out CaptureFrameRequest queued), Is.True);
                h.TrackRegistered(queued, lease);

                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void LeaseNotRegistered_FailClosed_BeforeStart()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 2);
            RunHarnessBody(h, () =>
            {
                CaptureFrameRenderTargetLeaseRegistry alt = new CaptureFrameRenderTargetLeaseRegistry(4, h.Pool);
                PointSchedulerLeaseRegistry(h.LeaseScheduler, alt);

                CaptureFrameRenderTargetLease lease = h.RentHeld();
                Assert.Throws<InvalidOperationException>(() => Submit(h.Coordinator, lease, 0.0, true));

                Assert.That(h.Queue.TryPeek(out CaptureFrameRequest queued), Is.True);
                Assert.That(alt.TryRemove(queued, out CaptureFrameRenderTargetLease reclaimed), Is.True);
                h.RemoveHeld(lease);
                h.Pool.Return(reclaimed);

                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(h.LeaseRegistry.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void LeaseMismatch_FailClosed_BeforeStart()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 2);
            RunHarnessBody(h, () =>
            {
                CaptureFrameRenderTargetLease otherLease = h.RentHeld();
                CaptureFrameRequest firstRequest = MakeAcceptedRequest(1, 1);
                Assert.That(h.LeaseRegistry.TryRegister(firstRequest, otherLease), Is.True);
                h.TrackRegistered(firstRequest, otherLease);

                CaptureFrameRenderTargetLeaseRegistry alt = new CaptureFrameRenderTargetLeaseRegistry(4, h.Pool);
                PointSchedulerLeaseRegistry(h.LeaseScheduler, alt);

                CaptureFrameRenderTargetLease lease = h.RentHeld();
                Assert.Throws<InvalidOperationException>(() => Submit(h.Coordinator, lease, 0.0, true));

                Assert.That(h.Queue.TryPeek(out CaptureFrameRequest queued), Is.True);
                Assert.That(alt.TryRemove(queued, out CaptureFrameRenderTargetLease reclaimed), Is.True);
                h.RemoveHeld(lease);
                h.Pool.Return(reclaimed);

                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void StartException_PreviousSideEffectsNotRolledBack()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 2);
            RunHarnessBody(h, () =>
            {
                CaptureFrameRenderTargetLeaseRegistry alt = new CaptureFrameRenderTargetLeaseRegistry(4, h.Pool);
                PointPumpLeaseRegistry(h.Pump, alt);

                CaptureFrameRenderTargetLease lease = h.RentHeld();
                Assert.Throws<InvalidOperationException>(() => Submit(h.Coordinator, lease, 0.0, true));

                Assert.That(h.Queue.TryPeek(out CaptureFrameRequest queued), Is.True);
                h.TrackRegistered(queued, lease);

                // The start exception does not roll back the submission's side effects.
                Assert.That(h.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(h.Queue.Count, Is.EqualTo(1));
                Assert.That(h.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(h.Sequence.LastIssued, Is.EqualTo(1));
                Assert.That(h.Selector.HasSelectedTimestamp, Is.True);
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void SingleCall_AdvanceSubmitStartAtMostOnce()
        {
            Harness h = MakeHarness(45.0, 4, 4, 4, 4, 2);
            RunHarnessBody(h, () =>
            {
                CaptureFrameRenderTargetLease lease = h.RentHeld();
                CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, lease, 0.0, true);
                if (result.SubmissionStatus == CaptureFrameCadencedSubmissionStatus.Submitted)
                {
                    h.TrackRegistered(result.AcceptedRecord.Request, lease);
                }

                Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(result.ReadbackStarted, Is.True);

                Assert.That(h.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(1));
                Assert.That(h.Sequence.LastIssued, Is.EqualTo(1));
                Assert.That(h.LeaseRegistry.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void TypeShape_SealedNonDisposableNonMonoBehaviour()
        {
            Type type = typeof(PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void GpuIntegration_RentDrawSubmitStartCompletePrepareSidecarLoadable()
        {
            Harness h = MakeHarness(45.0, 2, 2, 2, 2, 1, pngQueueCapacity: 1);
            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                CaptureFrameRenderTargetLease lease = h.RentHeld();

                RenderTexture rt = h.Pool.GetRenderTexture(lease);
                Assert.That(rt, Is.Not.Null);
                Assert.That(rt.IsCreated(), Is.True);
                Color32 expectedColor = new Color32(41, 128, 185, 255);
                FillSolidColor(rt, expectedColor);

                CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, lease, 0.0, true);
                if (result.SubmissionStatus == CaptureFrameCadencedSubmissionStatus.Submitted)
                {
                    h.TrackRegistered(result.AcceptedRecord.Request, lease);
                }

                Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(result.ReadbackStarted, Is.True);

                AsyncGPUReadback.WaitAllRequests();

                PngJsonCaptureFramePipelineAdvanceResult collected = h.Pipeline.AdvancePendingWork();
                Assert.That(collected.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                Assert.That(h.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(h.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(h.PngQueue.Count, Is.EqualTo(1));

                h.Pipeline.AdvancePendingWork();

                // The persisted PNG now exists on disk; decode it and verify the
                // pixels reproduce the drawn content.
                string pngPath = Path.Combine(h.Dir, ExpectedPngName(1));
                Assert.That(File.Exists(pngPath), Is.True);
                byte[] pngBytes = File.ReadAllBytes(pngPath);
                Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    Assert.That(decoded.LoadImage(pngBytes), Is.True);
                    Color32[] pixels = decoded.GetPixels32();
                    Assert.That(pixels.Length, Is.EqualTo(4));
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        Assert.That(pixels[i].r, Is.EqualTo(expectedColor.r));
                        Assert.That(pixels[i].g, Is.EqualTo(expectedColor.g));
                        Assert.That(pixels[i].b, Is.EqualTo(expectedColor.b));
                        Assert.That(pixels[i].a, Is.EqualTo(expectedColor.a));
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(decoded);
                }

                PngJsonCaptureFramePipelineAdvanceResult completed = h.Pipeline.AdvancePendingWork();
                Assert.That(completed.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
                Assert.That(completed.CompletedArtifact, Is.Not.Null);
                Assert.That(completed.SidecarReceipt, Is.Not.Null);

                CaptureFramePngArtifact loaded = new CaptureFramePngArtifactLoader(
                    new CaptureFramePngArtifactFileStore(),
                    new CaptureFramePngArtifactVerifier()).LoadVerified(
                        Path.Combine(h.Dir, ExpectedSidecarName(1)), h.Manifest);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.CaptureFrameId, Is.EqualTo(1));
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            errors = ConcatExceptions(errors, CleanupHarness(h));
            errors = AppendCleanupException(errors, DeleteTempDir(h.Dir));

            ThrowCleanupAndBody(body, errors);
        }
    }
}
