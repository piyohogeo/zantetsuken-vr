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
    public class PngJsonCaptureFrameRenderTargetPipelineCoordinatorTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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

        private static CaptureRunReference MakeRun(long testRunId = 1)
        {
            TraceRunManifest manifest = MakeManifest(testRunId);
            return new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
        }

        private static CaptureFrameProfile MakeProfile()
        {
            return CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
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

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecord MakeRecord(TraceRunManifest manifest, long captureFrameId, out CaptureFrameRequest request)
        {
            CaptureRunReference run = new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
            request = MakeRequest(captureFrameId);
            return new CaptureFrameRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
        }

        private static void SetForceNextError(UnityRenderTextureReadbackDispatcher dispatcher)
        {
            FieldInfo field = typeof(UnityRenderTextureReadbackDispatcher).GetField("_forceNextError", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(dispatcher, true);
        }

        private static void SetForceNextEnqueueError(CaptureFramePngQueue queue)
        {
            FieldInfo field = typeof(CaptureFramePngQueue).GetField("_forceNextEnqueueError", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(queue, true);
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

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-rt-pipeline-" + Guid.NewGuid().ToString("N"));
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

        private sealed class PipelineScope
        {
            public TraceLogger Logger;
            public CaptureFrameReadbackBufferPool BufferPool;
            public UnityRenderTextureReadbackDispatcher Dispatcher;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameRenderTargetLeaseRegistry LeaseRegistry;
            public CaptureFrameRequestQueue RequestQueue;
            public CaptureFrameRecordRegistry RecordRegistry;
            public CaptureFramePngQueue PngQueue;
            public CaptureFramePngArtifactQueue ArtifactQueue;
            public PngJsonCaptureFrameRenderTargetPipelineCoordinator Pipeline;
            public string TempDir;
            public readonly List<RegisteredEntry> Registered = new List<RegisteredEntry>();
            public readonly List<CaptureFrameRenderTargetLease> Held = new List<CaptureFrameRenderTargetLease>();

            public CaptureFrameRenderTargetLease RentAndSchedule(TraceRunManifest manifest, long captureFrameId, out CaptureFrameRequest request)
            {
                CaptureFrameRecord record = MakeRecord(manifest, captureFrameId, out request);
                Assert.That(RecordRegistry.TryRegister(record), Is.True);
                Assert.That(Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                Held.Add(lease);
                Assert.That(LeaseRegistry.TryRegister(request, lease), Is.True);
                TrackRegistered(request, lease);
                Assert.That(RequestQueue.TryEnqueue(request), Is.True);
                return lease;
            }

            public CaptureFrameRenderTargetLease RentHeld()
            {
                Assert.That(Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                Held.Add(lease);
                return lease;
            }

            public void TrackRegistered(CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
            {
                for (int i = Held.Count - 1; i >= 0; i--)
                {
                    if (Held[i].SlotIndex == lease.SlotIndex)
                    {
                        Held.RemoveAt(i);
                        break;
                    }
                }

                Registered.Add(new RegisteredEntry(request, lease));
            }
        }

        private static PipelineScope MakePipeline(
            string dir,
            out CaptureFrameReadbackBufferPool bufferPool,
            out UnityRenderTextureReadbackDispatcher dispatcher,
            out CaptureFrameRequestQueue requestQueue,
            out CaptureFrameRecordRegistry recordRegistry,
            out CaptureFrameRenderTargetLeaseRegistry leaseRegistry,
            out CaptureFrameRenderTargetPool pool,
            out CaptureFramePngQueue pngQueue,
            out CaptureFramePngArtifactQueue artifactQueue,
            out TraceLogger logger,
            int pngQueueCapacity = 4,
            int bufferPoolCapacity = 2)
        {
            PipelineScope scope = new PipelineScope();
            scope.TempDir = dir;

            bufferPool = new CaptureFrameReadbackBufferPool(bufferPoolCapacity, 64);
            dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
            logger = new TraceLogger(16);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            PngJsonCaptureFrameReadbackCompletionRouter completionRouter = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);

            requestQueue = new CaptureFrameRequestQueue(4);
            pool = new CaptureFrameRenderTargetPool(4, MakeProfile());
            leaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(4, pool);
            CaptureFrameRenderTargetReadbackPump readbackPump = new CaptureFrameRenderTargetReadbackPump(requestQueue, dispatcher, leaseRegistry, pool);

            recordRegistry = new CaptureFrameRecordRegistry(4);
            pngQueue = new CaptureFramePngQueue(pngQueueCapacity);
            artifactQueue = new CaptureFramePngArtifactQueue(4);

            CaptureFramePngArtifactPersistenceCoordinator persistenceCoordinator = MakePersistenceCoordinator(recordRegistry, dir);

            scope.Logger = logger;
            scope.BufferPool = bufferPool;
            scope.Dispatcher = dispatcher;
            scope.Pool = pool;
            scope.LeaseRegistry = leaseRegistry;
            scope.RequestQueue = requestQueue;
            scope.RecordRegistry = recordRegistry;
            scope.PngQueue = pngQueue;
            scope.ArtifactQueue = artifactQueue;
            scope.Pipeline = new PngJsonCaptureFrameRenderTargetPipelineCoordinator(
                readbackPump,
                completionRouter,
                persistenceCoordinator,
                pngQueue,
                artifactQueue,
                recordRegistry,
                leaseRegistry,
                pool);

            return scope;
        }

        private static Exception[] CleanupPipelineScope(PipelineScope scope)
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
                if (scope.Dispatcher.IsCreated)
                {
                    while (scope.Dispatcher.TryCollect(out CaptureFrameReadbackResult result))
                    {
                        scope.Dispatcher.Release(result);
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
                for (int i = scope.Registered.Count - 1; i >= 0; i--)
                {
                    RegisteredEntry entry = scope.Registered[i];
                    scope.Registered.RemoveAt(i);
                    try
                    {
                        if (scope.LeaseRegistry.TryRemove(entry.Request, out CaptureFrameRenderTargetLease lease))
                        {
                            scope.Pool.Return(lease);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

                for (int i = scope.Held.Count - 1; i >= 0; i--)
                {
                    CaptureFrameRenderTargetLease lease = scope.Held[i];
                    scope.Held.RemoveAt(i);
                    try
                    {
                        scope.Pool.Return(lease);
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }
            }

            try
            {
                if (scope.PngQueue != null && scope.PngQueue.IsCreated)
                {
                    scope.PngQueue.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (scope.Dispatcher.IsCreated)
                {
                    scope.Dispatcher.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (scope.BufferPool.IsCreated)
                {
                    scope.BufferPool.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                scope.Pool.Dispose();
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (scope.Logger != null && scope.Logger.IsCreated)
                {
                    scope.Logger.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            if (scope.TempDir != null)
            {
                errors = AppendCleanupException(errors, DeleteTempDir(scope.TempDir));
            }

            return errors;
        }

        private static void RunPipelineBody(PipelineScope scope, Action body)
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

            Exception[] errors = CleanupPipelineScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _);

            RunPipelineBody(scope, () =>
            {
                CaptureFrameRenderTargetLeaseRegistry leaseRegistry = scope.LeaseRegistry;
                CaptureFrameRenderTargetPool pool = scope.Pool;
                CaptureFrameRequestQueue requestQueue = scope.RequestQueue;
                CaptureFrameRecordRegistry recordRegistry = scope.RecordRegistry;
                CaptureFramePngQueue pngQueue = scope.PngQueue;
                CaptureFramePngArtifactQueue artifactQueue = scope.ArtifactQueue;

                CaptureFrameRenderTargetReadbackPump pump = new CaptureFrameRenderTargetReadbackPump(requestQueue, scope.Dispatcher, leaseRegistry, pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(scope.Logger);
                PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(scope.Dispatcher, observer);
                CaptureFramePngArtifactPersistenceCoordinator persistence = MakePersistenceCoordinator(recordRegistry, scope.TempDir);

                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetPipelineCoordinator(null, router, persistence, pngQueue, artifactQueue, recordRegistry, leaseRegistry, pool));
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetPipelineCoordinator(pump, null, persistence, pngQueue, artifactQueue, recordRegistry, leaseRegistry, pool));
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetPipelineCoordinator(pump, router, null, pngQueue, artifactQueue, recordRegistry, leaseRegistry, pool));
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetPipelineCoordinator(pump, router, persistence, null, artifactQueue, recordRegistry, leaseRegistry, pool));
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetPipelineCoordinator(pump, router, persistence, pngQueue, null, recordRegistry, leaseRegistry, pool));
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetPipelineCoordinator(pump, router, persistence, pngQueue, artifactQueue, null, leaseRegistry, pool));
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetPipelineCoordinator(pump, router, persistence, pngQueue, artifactQueue, recordRegistry, null, pool));
                Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFrameRenderTargetPipelineCoordinator(pump, router, persistence, pngQueue, artifactQueue, recordRegistry, leaseRegistry, null));
            });
        }

        [Test]
        public void TryStartNextReadback_EmptyQueue_False()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _);
            RunPipelineBody(scope, () =>
            {
                Assert.That(scope.Pipeline.TryStartNextReadback(), Is.False);
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(scope.PngQueue.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void AdvancePendingWork_DoesNotStartPendingRequest()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _);
            RunPipelineBody(scope, () =>
            {
                TraceRunManifest manifest = MakeManifest();
                scope.RentAndSchedule(manifest, 42, out _);

                PngJsonCaptureFramePipelineAdvanceResult result = scope.Pipeline.AdvancePendingWork();

                Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.None));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void TryStartNextReadback_StartsOneRegisteredRequest()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _);
            RunPipelineBody(scope, () =>
            {
                TraceRunManifest manifest = MakeManifest();
                scope.RentAndSchedule(manifest, 42, out _);

                Assert.That(scope.Pipeline.TryStartNextReadback(), Is.True);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void LeaseNotRegistered_FailClosed()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _);
            RunPipelineBody(scope, () =>
            {
                TraceRunManifest manifest = MakeManifest();
                CaptureFrameRecord record = MakeRecord(manifest, 42, out CaptureFrameRequest request);
                Assert.That(scope.RecordRegistry.TryRegister(record), Is.True);
                Assert.That(scope.RequestQueue.TryEnqueue(request), Is.True);

                Assert.Throws<InvalidOperationException>(() => scope.Pipeline.TryStartNextReadback());

                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void DispatcherFull_False_StateUnchanged()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _);
            RunPipelineBody(scope, () =>
            {
                // Fill the dispatcher with two in-flight readbacks.
                CaptureFrameRenderTargetLease fill1 = scope.RentHeld();
                CaptureFrameRenderTargetLease fill2 = scope.RentHeld();
                Assert.That(scope.Dispatcher.TryStart(MakeRequest(101), scope.Pool.GetRenderTexture(fill1)), Is.True);
                Assert.That(scope.Dispatcher.TryStart(MakeRequest(102), scope.Pool.GetRenderTexture(fill2)), Is.True);

                TraceRunManifest manifest = MakeManifest();
                scope.RentAndSchedule(manifest, 42, out _);

                int queueCount = scope.RequestQueue.Count;
                int leaseCount = scope.LeaseRegistry.Count;
                int rented = scope.Pool.RentedCount;

                Assert.That(scope.Pipeline.TryStartNextReadback(), Is.False);

                Assert.That(scope.RequestQueue.Count, Is.EqualTo(queueCount));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(leaseCount));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(rented));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(2));
            });
        }

        [Test]
        public void AdvancePendingWork_CompletionSuccess_PngEnqueued_LeaseReturned()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _);
            RunPipelineBody(scope, () =>
            {
                TraceRunManifest manifest = MakeManifest();
                scope.RentAndSchedule(manifest, 42, out _);

                Assert.That(scope.Pipeline.TryStartNextReadback(), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                PngJsonCaptureFramePipelineAdvanceResult result = scope.Pipeline.AdvancePendingWork();

                Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                Assert.That(scope.PngQueue.Count, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void AdvancePendingWork_ReadbackError_LeaseReturned_RecordRemoved()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _);
            RunPipelineBody(scope, () =>
            {
                TraceRunManifest manifest = MakeManifest();
                scope.RentAndSchedule(manifest, 42, out _);

                Assert.That(scope.Pipeline.TryStartNextReadback(), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                SetForceNextError(scope.Dispatcher);

                PngJsonCaptureFramePipelineAdvanceResult result = scope.Pipeline.AdvancePendingWork();

                Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Dropped));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(scope.PngQueue.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void AdvancePendingWork_PngQueueFull_LeaseReturned_RecordRemoved()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _, pngQueueCapacity: 1);
            RunPipelineBody(scope, () =>
            {
                TraceRunManifest manifest = MakeManifest();

                // Frame 1: start frame 1's readback.
                scope.RentAndSchedule(manifest, 1, out _);
                Assert.That(scope.Pipeline.TryStartNextReadback(), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                // Frame 2: collect frame 1 into the PNG queue (now full).
                scope.RentAndSchedule(manifest, 2, out _);
                Assert.That(scope.Pipeline.TryStartNextReadback(), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                Assert.That(scope.Pipeline.AdvancePendingWork().ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                Assert.That(scope.PngQueue.Count, Is.EqualTo(1));

                // Frame 3: prepare frame 1 into the artifact queue (freeing the
                // PNG queue), then collect frame 2 back into the PNG queue
                // (full again).
                scope.RentAndSchedule(manifest, 3, out _);
                Assert.That(scope.Pipeline.TryStartNextReadback(), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                Assert.That(scope.Pipeline.AdvancePendingWork().ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                Assert.That(scope.PngQueue.Count, Is.EqualTo(1));

                // Frame 4: persistence completes frame 1's sidecar (leaving the
                // PNG queue full), then frame 3's completed readback is dropped
                // because the PNG queue is full.
                PngJsonCaptureFramePipelineAdvanceResult result = scope.Pipeline.AdvancePendingWork();

                Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Dropped));
                Assert.That(scope.PngQueue.Count, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void Tick_DoesNotCollectReadbackStartedSameTick()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _);
            RunPipelineBody(scope, () =>
            {
                TraceRunManifest manifest = MakeManifest();
                scope.RentAndSchedule(manifest, 42, out _);

                PngJsonCaptureFramePipelineTickResult result = scope.Pipeline.Tick();

                Assert.That(result.ReadbackStarted, Is.True);
                Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.None));
                Assert.That(result.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void CompletionFreesDispatcherSlot_SameTickStartsNext()
        {
            PipelineScope scope = MakePipeline(CreateTempDir(), out _, out _, out _, out _, out _, out _, out _, out _, out _, bufferPoolCapacity: 1);
            RunPipelineBody(scope, () =>
            {
                TraceRunManifest manifest = MakeManifest();

                // Frame 1: start, complete, leave it collected-pending by not advancing yet.
                scope.RentAndSchedule(manifest, 1, out _);
                Assert.That(scope.Pipeline.TryStartNextReadback(), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                // Enqueue frame 2; its readback cannot start until frame 1 is collected.
                scope.RentAndSchedule(manifest, 2, out _);

                // One tick: advance (collect frame 1, freeing the dispatcher slot), then start frame 2.
                PngJsonCaptureFramePipelineTickResult result = scope.Pipeline.Tick();

                Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                Assert.That(result.ReadbackStarted, Is.True);
                Assert.That(scope.PngQueue.Count, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void TypeShape_SealedNonDisposableNonMonoBehaviour()
        {
            Type type = typeof(PngJsonCaptureFrameRenderTargetPipelineCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void GpuIntegration_RentSubmitTickCompletePrepareSidecarLoadable()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            PipelineScope scope = MakePipeline(
                dir,
                out CaptureFrameReadbackBufferPool bufferPool,
                out UnityRenderTextureReadbackDispatcher dispatcher,
                out CaptureFrameRequestQueue requestQueue,
                out CaptureFrameRecordRegistry recordRegistry,
                out CaptureFrameRenderTargetLeaseRegistry leaseRegistry,
                out CaptureFrameRenderTargetPool pool,
                out CaptureFramePngQueue pngQueue,
                out CaptureFramePngArtifactQueue artifactQueue,
                out TraceLogger logger);

            // Build the lease-aware cadence submission path on top of the same queue/registries/pool.
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameRequestScheduler requestScheduler = new CaptureFrameRequestScheduler(requestQueue, observer);
            CaptureFrameRecordScheduler recordScheduler = new CaptureFrameRecordScheduler(requestScheduler, recordRegistry, observer);
            CaptureFrameRenderTargetRecordScheduler leaseScheduler = new CaptureFrameRenderTargetRecordScheduler(recordScheduler, leaseRegistry);

            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            CaptureFrameRecordFactory factory = new CaptureFrameRecordFactory(
                MakeRun(),
                sequence,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
            CaptureFrameRenderTargetRecordSubmissionCoordinator submission = new CaptureFrameRenderTargetRecordSubmissionCoordinator(factory, leaseScheduler);
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(CaptureFrameCadenceSelector.PhaseZeroTargetFramesPerSecond);
            CaptureFrameRenderTargetCadencedSubmissionCoordinator cadenced = new CaptureFrameRenderTargetCadencedSubmissionCoordinator(selector, submission);

            try
            {
                CaptureFrameRenderTargetLease lease = scope.RentHeld();

                Assert.That(cadenced.TrySubmit(
                    1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                    MakeTiming(),
                    MakePose(1f, 2f, 3f),
                    MakePose(4f, 5f, 6f),
                    MakePose(7f, 8f, 9f),
                    1,
                    lease,
                    out CaptureFrameRecord accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(accepted, Is.Not.Null);
                Assert.That(accepted.CaptureFrameId, Is.EqualTo(1));
                scope.TrackRegistered(accepted.Request, lease);

                // Tick: start the readback.
                PngJsonCaptureFramePipelineTickResult start = scope.Pipeline.Tick();
                Assert.That(start.ReadbackStarted, Is.True);

                AsyncGPUReadback.WaitAllRequests();

                // Advance: collect → PNG enqueue + lease return.
                PngJsonCaptureFramePipelineAdvanceResult collected = scope.Pipeline.AdvancePendingWork();
                Assert.That(collected.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                Assert.That(leaseRegistry.Count, Is.EqualTo(0));
                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(pngQueue.Count, Is.EqualTo(1));

                // Advance: PNG prepare.
                scope.Pipeline.AdvancePendingWork();

                // Advance: sidecar complete.
                PngJsonCaptureFramePipelineAdvanceResult completed = scope.Pipeline.AdvancePendingWork();
                Assert.That(completed.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
                Assert.That(completed.CompletedArtifact, Is.Not.Null);
                Assert.That(completed.SidecarReceipt, Is.Not.Null);

                CaptureFramePngArtifact loaded = new CaptureFramePngArtifactLoader(
                    new CaptureFramePngArtifactFileStore(),
                    new CaptureFramePngArtifactVerifier()).LoadVerified(Path.Combine(dir, ExpectedSidecarName(1)), manifest);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.CaptureFrameId, Is.EqualTo(1));

                Assert.That(leaseRegistry.Count, Is.EqualTo(0));
                Assert.That(pool.RentedCount, Is.EqualTo(0));
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            errors = ConcatExceptions(errors, CleanupPipelineScope(scope));
            errors = AppendCleanupException(errors, DeleteTempDir(dir));

            ThrowCleanupAndBody(body, errors);
        }
    }
}
