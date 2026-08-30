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
    public class PngJsonCaptureFramePipelineCoordinatorTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string FixedPngHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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

        private static CaptureFrameRequest MakeRequest(long testRunId = 1, long captureFrameId = 42)
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
            request = MakeRequest(1, captureFrameId);
            return new CaptureFrameRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
        }

        private static RenderTexture CreateTex2D(int width, int height)
        {
            RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            return rt;
        }

        private static void DestroyTexture(RenderTexture rt)
        {
            if (rt == null)
            {
                return;
            }

            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }

        private static void SetForceNextError(UnityRenderTextureReadbackDispatcher dispatcher)
        {
            FieldInfo field = typeof(UnityRenderTextureReadbackDispatcher).GetField("_forceNextError", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(dispatcher, true);
        }

        private static Exception[] CleanupGpuTest(
            UnityRenderTextureReadbackDispatcher dispatcher,
            RenderTexture rt,
            CaptureFramePngQueue pngQueue,
            CaptureFrameReadbackBufferPool pool,
            TraceLogger logger)
        {
            List<Exception> errors = new List<Exception>();

            // 1. Wait for pending GPU readbacks and release every collected result.
            try
            {
                AsyncGPUReadback.WaitAllRequests();
                if (dispatcher != null && dispatcher.IsCreated)
                {
                    while (dispatcher.TryCollect(out CaptureFrameReadbackResult result))
                    {
                        dispatcher.Release(result);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
            finally
            {
                // 2. Release and destroy the RenderTexture regardless of step 1.
                try
                {
                    DestroyTexture(rt);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
                finally
                {
                    // 3. Dispose the PNG queue regardless of prior steps.
                    try
                    {
                        if (pngQueue != null && pngQueue.IsCreated)
                        {
                            pngQueue.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }
                    finally
                    {
                        // 4. Dispose the dispatcher regardless of prior steps.
                        try
                        {
                            if (dispatcher != null && dispatcher.IsCreated)
                            {
                                dispatcher.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex);
                        }
                        finally
                        {
                            // 5. Dispose the buffer pool regardless of prior steps.
                            try
                            {
                                if (pool != null && pool.IsCreated)
                                {
                                    pool.Dispose();
                                }
                            }
                            catch (Exception ex)
                            {
                                errors.Add(ex);
                            }
                            finally
                            {
                                // 6. Dispose the logger last.
                                try
                                {
                                    if (logger != null && logger.IsCreated)
                                    {
                                        logger.Dispose();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    errors.Add(ex);
                                }
                            }
                        }
                    }
                }
            }

            return errors.ToArray();
        }

        private static void ThrowCleanupAndBody(ExceptionDispatchInfo bodyException, Exception[] cleanupExceptions)
        {
            bool hasBody = bodyException != null;
            bool hasCleanup = cleanupExceptions != null && cleanupExceptions.Length > 0;

            if (hasBody && hasCleanup)
            {
                List<Exception> all = new List<Exception>(cleanupExceptions.Length + 1);
                all.Add(bodyException.SourceException);
                all.AddRange(cleanupExceptions);
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

        private static CaptureFramePngSaveReceipt MakePngReceipt(string path, int byteCount, string hash)
        {
            ConstructorInfo ctor = typeof(CaptureFramePngSaveReceipt).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(int), typeof(string) }, null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFramePngSaveReceipt)ctor.Invoke(new object[] { path, byteCount, hash });
        }

        private static CaptureFramePngArtifactSaveReceipt MakeSidecarReceipt(string path, int byteCount, string hash)
        {
            ConstructorInfo ctor = typeof(CaptureFramePngArtifactSaveReceipt).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(int), typeof(string) }, null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFramePngArtifactSaveReceipt)ctor.Invoke(new object[] { path, byteCount, hash });
        }

        private static ConstructorInfo GetTickResultCtor()
        {
            ConstructorInfo ctor = typeof(PngJsonCaptureFramePipelineTickResult).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[]
                {
                    typeof(CaptureFramePngArtifactPersistenceStatus),
                    typeof(CaptureFramePngQueueStatus),
                    typeof(bool),
                    typeof(CaptureFramePngArtifact),
                    typeof(CaptureFramePngArtifactSaveReceipt)
                },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor;
        }

        private static Exception GetTickResultCtorException(
            CaptureFramePngArtifactPersistenceStatus persistenceStatus,
            CaptureFramePngQueueStatus readbackCompletionStatus,
            bool readbackStarted,
            CaptureFramePngArtifact completedArtifact,
            CaptureFramePngArtifactSaveReceipt sidecarReceipt)
        {
            try
            {
                GetTickResultCtor().Invoke(new object[] { persistenceStatus, readbackCompletionStatus, readbackStarted, completedArtifact, sidecarReceipt });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static PngJsonCaptureFramePipelineTickResult MakeTickResult(
            CaptureFramePngArtifactPersistenceStatus persistenceStatus,
            CaptureFramePngQueueStatus readbackCompletionStatus,
            bool readbackStarted,
            CaptureFramePngArtifact completedArtifact,
            CaptureFramePngArtifactSaveReceipt sidecarReceipt)
        {
            return (PngJsonCaptureFramePipelineTickResult)GetTickResultCtor().Invoke(
                new object[] { persistenceStatus, readbackCompletionStatus, readbackStarted, completedArtifact, sidecarReceipt });
        }

        private static ConstructorInfo GetAdvanceResultCtor()
        {
            ConstructorInfo ctor = typeof(PngJsonCaptureFramePipelineAdvanceResult).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[]
                {
                    typeof(CaptureFramePngArtifactPersistenceStatus),
                    typeof(CaptureFramePngQueueStatus),
                    typeof(CaptureFramePngArtifact),
                    typeof(CaptureFramePngArtifactSaveReceipt)
                },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor;
        }

        private static Exception GetAdvanceResultCtorException(
            CaptureFramePngArtifactPersistenceStatus persistenceStatus,
            CaptureFramePngQueueStatus readbackCompletionStatus,
            CaptureFramePngArtifact completedArtifact,
            CaptureFramePngArtifactSaveReceipt sidecarReceipt)
        {
            try
            {
                GetAdvanceResultCtor().Invoke(new object[] { persistenceStatus, readbackCompletionStatus, completedArtifact, sidecarReceipt });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static PngJsonCaptureFramePipelineAdvanceResult MakeAdvanceResult(
            CaptureFramePngArtifactPersistenceStatus persistenceStatus,
            CaptureFramePngQueueStatus readbackCompletionStatus,
            CaptureFramePngArtifact completedArtifact,
            CaptureFramePngArtifactSaveReceipt sidecarReceipt)
        {
            return (PngJsonCaptureFramePipelineAdvanceResult)GetAdvanceResultCtor().Invoke(
                new object[] { persistenceStatus, readbackCompletionStatus, completedArtifact, sidecarReceipt });
        }

        private static void RegisterAndSchedule(
            CaptureFrameRecordRegistry recordRegistry,
            CaptureFrameRequestQueue requestQueue,
            TraceRunManifest manifest,
            long captureFrameId,
            out CaptureFrameRequest request)
        {
            CaptureFrameRecord record = MakeRecord(manifest, captureFrameId, out request);
            Assert.That(recordRegistry.TryRegister(record), Is.True);
            Assert.That(requestQueue.TryEnqueue(request), Is.True);
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
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-pipeline-" + Guid.NewGuid().ToString("N"));
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

        private static PngJsonCaptureFramePipelineCoordinator MakePipeline(
            string dir,
            int poolSlotCount,
            out CaptureFrameReadbackBufferPool pool,
            out UnityRenderTextureReadbackDispatcher dispatcher,
            out CaptureFrameRequestQueue requestQueue,
            out CaptureFrameRecordRegistry recordRegistry,
            out CaptureFramePngQueue pngQueue,
            out CaptureFramePngArtifactQueue artifactQueue,
            out TraceLogger logger,
            int pngQueueCapacity = 4)
        {
            pool = new CaptureFrameReadbackBufferPool(poolSlotCount, 64);
            dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
            logger = new TraceLogger(16);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            PngJsonCaptureFrameReadbackCompletionRouter completionRouter = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);

            requestQueue = new CaptureFrameRequestQueue(4);
            CaptureFrameReadbackPump readbackPump = new CaptureFrameReadbackPump(requestQueue, dispatcher);

            recordRegistry = new CaptureFrameRecordRegistry(4);
            pngQueue = new CaptureFramePngQueue(pngQueueCapacity);
            artifactQueue = new CaptureFramePngArtifactQueue(4);

            CaptureFramePngArtifactPersistenceCoordinator persistenceCoordinator = MakePersistenceCoordinator(recordRegistry, dir);

            return new PngJsonCaptureFramePipelineCoordinator(readbackPump, completionRouter, persistenceCoordinator, pngQueue, artifactQueue, recordRegistry);
        }

        // ---- Result tests ----

        [Test]
        public void Result_DefaultValues()
        {
            PngJsonCaptureFramePipelineTickResult result = default;

            Assert.That(result.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
            Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.None));
            Assert.That(result.ReadbackStarted, Is.False);
            Assert.That(result.CompletedArtifact, Is.Null);
            Assert.That(result.SidecarReceipt, Is.Null);
            Assert.That(result.HasCompletedArtifact, Is.False);
        }

        [Test]
        public void Result_None_HasNullArtifactReceipt()
        {
            PngJsonCaptureFramePipelineTickResult result = MakeTickResult(CaptureFramePngArtifactPersistenceStatus.None, CaptureFramePngQueueStatus.None, false, null, null);

            Assert.That(result.CompletedArtifact, Is.Null);
            Assert.That(result.SidecarReceipt, Is.Null);
            Assert.That(result.HasCompletedArtifact, Is.False);
        }

        [Test]
        public void Result_PngPrepared_HasNullArtifactReceipt()
        {
            PngJsonCaptureFramePipelineTickResult result = MakeTickResult(CaptureFramePngArtifactPersistenceStatus.PngPrepared, CaptureFramePngQueueStatus.None, false, null, null);

            Assert.That(result.CompletedArtifact, Is.Null);
            Assert.That(result.SidecarReceipt, Is.Null);
            Assert.That(result.HasCompletedArtifact, Is.False);
        }

        [Test]
        public void Result_SidecarCompleted_RequiresArtifactReceipt()
        {
            TraceRunManifest manifest = MakeManifest();
            CaptureFrameRecord record = MakeRecord(manifest, 42, out CaptureFrameRequest request);
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakePngReceipt("C:\\x\\out.png", 32, FixedPngHash));
            CaptureFramePngArtifactSaveReceipt receipt = MakeSidecarReceipt("C:\\x\\out.json", 123, FixedPngHash);

            PngJsonCaptureFramePipelineTickResult result = MakeTickResult(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted, CaptureFramePngQueueStatus.None, false, artifact, receipt);

            Assert.That(result.CompletedArtifact, Is.Not.Null);
            Assert.That(result.SidecarReceipt, Is.Not.Null);
            Assert.That(result.HasCompletedArtifact, Is.True);
        }

        [Test]
        public void Result_HasCompletedArtifact_Invariant()
        {
            TraceRunManifest manifest = MakeManifest();
            CaptureFrameRecord record = MakeRecord(manifest, 42, out CaptureFrameRequest request);
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakePngReceipt("C:\\x\\out.png", 32, FixedPngHash));
            CaptureFramePngArtifactSaveReceipt receipt = MakeSidecarReceipt("C:\\x\\out.json", 123, FixedPngHash);

            PngJsonCaptureFramePipelineTickResult completed = MakeTickResult(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted, CaptureFramePngQueueStatus.None, false, artifact, receipt);
            PngJsonCaptureFramePipelineTickResult none = MakeTickResult(CaptureFramePngArtifactPersistenceStatus.None, CaptureFramePngQueueStatus.None, false, null, null);
            PngJsonCaptureFramePipelineTickResult prepared = MakeTickResult(CaptureFramePngArtifactPersistenceStatus.PngPrepared, CaptureFramePngQueueStatus.None, false, null, null);

            Assert.That(completed.HasCompletedArtifact, Is.EqualTo(completed.PersistenceStatus == CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
            Assert.That(none.HasCompletedArtifact, Is.EqualTo(none.PersistenceStatus == CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
            Assert.That(prepared.HasCompletedArtifact, Is.EqualTo(prepared.PersistenceStatus == CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
        }

        [Test]
        public void Result_UndefinedPersistenceStatus_Rejected()
        {
            Assert.That(GetTickResultCtorException((CaptureFramePngArtifactPersistenceStatus)999, CaptureFramePngQueueStatus.None, false, null, null), Is.InstanceOf<ArgumentException>());
        }

        [Test]
        public void Result_UndefinedReadbackStatus_Rejected()
        {
            Assert.That(GetTickResultCtorException(CaptureFramePngArtifactPersistenceStatus.None, (CaptureFramePngQueueStatus)999, false, null, null), Is.InstanceOf<ArgumentException>());
        }

        [Test]
        public void Result_HoldsSameReferences()
        {
            TraceRunManifest manifest = MakeManifest();
            CaptureFrameRecord record = MakeRecord(manifest, 42, out CaptureFrameRequest request);
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakePngReceipt("C:\\x\\out.png", 32, FixedPngHash));
            CaptureFramePngArtifactSaveReceipt receipt = MakeSidecarReceipt("C:\\x\\out.json", 123, FixedPngHash);

            PngJsonCaptureFramePipelineTickResult result = MakeTickResult(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted, CaptureFramePngQueueStatus.None, false, artifact, receipt);

            Assert.That(result.CompletedArtifact, Is.SameAs(artifact));
            Assert.That(result.SidecarReceipt, Is.SameAs(receipt));
        }

        // ---- Coordinator tests ----

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out CaptureFramePngArtifactQueue artifactQueue, out TraceLogger logger);
                try
                {
                    CaptureFrameReadbackPump readbackPump = new CaptureFrameReadbackPump(requestQueue, dispatcher);
                    PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, new CaptureFrameTraceObserver(logger));
                    CaptureFramePngArtifactPersistenceCoordinator persistence = MakePersistenceCoordinator(registry, dir);

                    Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFramePipelineCoordinator(null, router, persistence, pngQueue, artifactQueue, registry));
                    Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFramePipelineCoordinator(readbackPump, null, persistence, pngQueue, artifactQueue, registry));
                    Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFramePipelineCoordinator(readbackPump, router, null, pngQueue, artifactQueue, registry));
                    Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFramePipelineCoordinator(readbackPump, router, persistence, null, artifactQueue, registry));
                    Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFramePipelineCoordinator(readbackPump, router, persistence, pngQueue, null, registry));
                    Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureFramePipelineCoordinator(readbackPump, router, persistence, pngQueue, artifactQueue, null));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, null, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void NoWork_Tick_None_None_NotStarted_NullSourceAllowed()
        {
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out _, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                try
                {
                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(null);

                    Assert.That(result.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
                    Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.None));
                    Assert.That(result.ReadbackStarted, Is.False);
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, null, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void PendingRequest_Tick_StartsReadback()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);

                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.ReadbackStarted, Is.True);
                    Assert.That(requestQueue.Count, Is.EqualTo(0));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void StartTick_DoesNotCollectSameRequest()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);

                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.ReadbackStarted, Is.True);
                    Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.None));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void AfterGpuCompletion_Tick_EnqueuesPng()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);
                    pipeline.Tick(rt); // start readback
                    AsyncGPUReadback.WaitAllRequests();

                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void NextTick_PngPrepared()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out CaptureFramePngArtifactQueue artifactQueue, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);
                    pipeline.Tick(rt); // start
                    AsyncGPUReadback.WaitAllRequests();
                    pipeline.Tick(rt); // collect → PNG queued

                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void NextTick_SidecarCompleted()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);
                    pipeline.Tick(rt); // start
                    AsyncGPUReadback.WaitAllRequests();
                    pipeline.Tick(rt); // collect
                    pipeline.Tick(rt); // prepare

                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
                    Assert.That(result.HasCompletedArtifact, Is.True);
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void CompletedResult_ReturnsArtifactReceipt()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);
                    pipeline.Tick(rt);
                    AsyncGPUReadback.WaitAllRequests();
                    pipeline.Tick(rt);
                    pipeline.Tick(rt);

                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.CompletedArtifact, Is.Not.Null);
                    Assert.That(result.SidecarReceipt, Is.Not.Null);
                    Assert.That(result.CompletedArtifact.CaptureFrameId, Is.EqualTo(42));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void SavedPngAndSidecar_Loadable()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);
                    pipeline.Tick(rt);
                    AsyncGPUReadback.WaitAllRequests();
                    pipeline.Tick(rt);
                    pipeline.Tick(rt);
                    pipeline.Tick(rt); // sidecar completed

                    CaptureFramePngArtifact loaded = new CaptureFramePngArtifactLoader(
                        new CaptureFramePngArtifactFileStore(),
                        new CaptureFramePngArtifactVerifier()).LoadVerified(Path.Combine(dir, ExpectedSidecarName(42)), manifest);

                    Assert.That(loaded, Is.Not.Null);
                    Assert.That(loaded.CaptureFrameId, Is.EqualTo(42));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void SameTick_PersistenceFreesPng_CollectEnqueues()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out CaptureFramePngArtifactQueue artifactQueue, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    // Frame A started.
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    pipeline.Tick(rt); // start A
                    AsyncGPUReadback.WaitAllRequests();

                    // In the same tick that collects A into the PNG queue, B is started.
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);
                    pipeline.Tick(rt); // collect A → PNG queue 1; start B
                    AsyncGPUReadback.WaitAllRequests();

                    // One tick: persistence prepares A (frees the PNG slot), then
                    // collect enqueues B into that slot.
                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void SameTick_CollectFreesSlot_StartNextRequest()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 1, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    pipeline.Tick(rt); // start A (dispatcher now full)
                    AsyncGPUReadback.WaitAllRequests();
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);

                    // One tick: collect frees A's slot, then B starts in it.
                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                    Assert.That(result.ReadbackStarted, Is.True);
                    Assert.That(requestQueue.Count, Is.EqualTo(0));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void DispatcherFull_RequestQueueHeadKept()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 1, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    pipeline.Tick(rt); // start A (dispatcher full)
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);

                    // B cannot start because the dispatcher is full and A is not done.
                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.ReadbackStarted, Is.False);
                    Assert.That(requestQueue.Count, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void PngQueueFull_DropTrace_RegistryRemoved()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 1, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger, pngQueueCapacity: 1);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    // Frame A: collect into the PNG queue (now full).
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    pipeline.Tick(rt); // start A
                    AsyncGPUReadback.WaitAllRequests();
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);
                    pipeline.Tick(rt); // collect A → PNG queue full; start B
                    AsyncGPUReadback.WaitAllRequests();

                    // Frame C: prepare A into the artifact queue, collect B into the PNG queue.
                    RegisterAndSchedule(registry, requestQueue, manifest, 3, out CaptureFrameRequest request3);
                    pipeline.Tick(rt); // prepare A, collect B → PNG full; start C
                    AsyncGPUReadback.WaitAllRequests();

                    // Frame D: persistence completes A's sidecar (leaves the PNG queue
                    // full), then C's completed readback is dropped because the PNG
                    // queue is full.
                    RegisterAndSchedule(registry, requestQueue, manifest, 4, out _);
                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Dropped));
                    Assert.That(registry.TryGet(request3, out _), Is.False);

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.GreaterThan(0));
                    bool droppedTrace = false;
                    for (int i = 0; i < logger.HistoryCount; i++)
                    {
                        TraceEvent e = logger.GetHistoryEvent(i);
                        if (e.EventType == TraceEventType.CaptureFrameDropped && e.Value1 == (int)CaptureFrameDropReason.EncodedPngQueueFull)
                        {
                            droppedTrace = true;
                        }
                    }

                    Assert.That(droppedTrace, Is.True);
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void ReadbackError_ReadbackFailed_RegistryRemoved()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out CaptureFrameRequest request);
                    pipeline.Tick(rt);
                    AsyncGPUReadback.WaitAllRequests();
                    SetForceNextError(dispatcher);

                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Dropped));
                    Assert.That(registry.TryGet(request, out _), Is.False);

                    logger.Drain();
                    bool failedTrace = false;
                    for (int i = 0; i < logger.HistoryCount; i++)
                    {
                        TraceEvent e = logger.GetHistoryEvent(i);
                        if (e.EventType == TraceEventType.CaptureFrameDropped && e.Value1 == (int)CaptureFrameDropReason.ReadbackFailed)
                        {
                            failedTrace = true;
                        }
                    }

                    Assert.That(failedTrace, Is.True);
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void InvalidSource_StartFails_NoRollback()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out CaptureFramePngArtifactQueue artifactQueue, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    // A collected into the PNG queue.
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    pipeline.Tick(rt);
                    AsyncGPUReadback.WaitAllRequests();
                    pipeline.Tick(rt); // A → PNG queue 1

                    // B pending; next tick will prepare A then fail to start B.
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);

                    Assert.Throws<ArgumentNullException>(() => pipeline.Tick(null));

                    // A's persistence is not rolled back; B is still pending.
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(pngQueue.Count, Is.EqualTo(0));
                    Assert.That(requestQueue.Count, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void OneTick_EachStageAtMostOne()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);

                    // Two pending requests: one tick starts only one readback.
                    PngJsonCaptureFramePipelineTickResult result = pipeline.Tick(rt);

                    Assert.That(result.ReadbackStarted, Is.True);
                    Assert.That(requestQueue.Count, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void MultipleFrames_FifoOrder()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out CaptureFramePngArtifactQueue artifactQueue, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);

                    pipeline.Tick(rt); // start 1
                    pipeline.Tick(rt); // start 2
                    AsyncGPUReadback.WaitAllRequests();
                    pipeline.Tick(rt); // collect 1
                    pipeline.Tick(rt); // prepare 1, collect 2
                    pipeline.Tick(rt); // sidecar 1
                    pipeline.Tick(rt); // prepare 2
                    PngJsonCaptureFramePipelineTickResult last = pipeline.Tick(rt); // sidecar 2

                    Assert.That(last.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
                    Assert.That(File.Exists(Path.Combine(dir, ExpectedSidecarName(1))), Is.True);
                    Assert.That(File.Exists(Path.Combine(dir, ExpectedSidecarName(2))), Is.True);
                    Assert.That(pngQueue.Count, Is.EqualTo(0));
                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
                    Assert.That(registry.Count, Is.EqualTo(0));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void EndToEnd_RenderTexture()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);

                    // schedule → Tick: readback started
                    PngJsonCaptureFramePipelineTickResult r1 = pipeline.Tick(rt);
                    Assert.That(r1.ReadbackStarted, Is.True);

                    AsyncGPUReadback.WaitAllRequests();

                    // → Tick: PNG queued
                    PngJsonCaptureFramePipelineTickResult r2 = pipeline.Tick(rt);
                    Assert.That(r2.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                    // → Tick: PNG prepared
                    PngJsonCaptureFramePipelineTickResult r3 = pipeline.Tick(rt);
                    Assert.That(r3.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));

                    // → Tick: sidecar completed
                    PngJsonCaptureFramePipelineTickResult r4 = pipeline.Tick(rt);
                    Assert.That(r4.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
                    Assert.That(r4.HasCompletedArtifact, Is.True);
                    Assert.That(r4.CompletedArtifact, Is.Not.Null);
                    Assert.That(r4.SidecarReceipt, Is.Not.Null);

                    // Loaderでsidecar＋PNG検証
                    CaptureFramePngArtifact loaded = new CaptureFramePngArtifactLoader(
                        new CaptureFramePngArtifactFileStore(),
                        new CaptureFramePngArtifactVerifier()).LoadVerified(Path.Combine(dir, ExpectedSidecarName(42)), manifest);

                    Assert.That(loaded, Is.Not.Null);
                    Assert.That(loaded.CaptureFrameId, Is.EqualTo(42));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void DoesNotDisposeOrClearDependencies()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out CaptureFramePngArtifactQueue artifactQueue, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);

                    pipeline.Tick(rt);
                    AsyncGPUReadback.WaitAllRequests();
                    pipeline.Tick(rt);

                    // Dependencies remain usable and are not cleared or disposed.
                    Assert.That(pngQueue.IsCreated, Is.True);
                    Assert.That(dispatcher.IsCreated, Is.True);
                    Assert.That(requestQueue.Count, Is.EqualTo(0));
                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        // ---- Advance result tests ----

        [Test]
        public void AdvanceResult_DefaultValues()
        {
            PngJsonCaptureFramePipelineAdvanceResult result = default;

            Assert.That(result.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
            Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.None));
            Assert.That(result.CompletedArtifact, Is.Null);
            Assert.That(result.SidecarReceipt, Is.Null);
            Assert.That(result.HasCompletedArtifact, Is.False);
        }

        [Test]
        public void AdvanceResult_SidecarCompleted_RequiresArtifactReceipt()
        {
            TraceRunManifest manifest = MakeManifest();
            CaptureFrameRecord record = MakeRecord(manifest, 42, out CaptureFrameRequest request);
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakePngReceipt("C:\\x\\out.png", 32, FixedPngHash));
            CaptureFramePngArtifactSaveReceipt receipt = MakeSidecarReceipt("C:\\x\\out.json", 123, FixedPngHash);

            Assert.That(GetAdvanceResultCtorException(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted, CaptureFramePngQueueStatus.None, null, receipt), Is.InstanceOf<ArgumentNullException>());
            Assert.That(GetAdvanceResultCtorException(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted, CaptureFramePngQueueStatus.None, artifact, null), Is.InstanceOf<ArgumentNullException>());

            PngJsonCaptureFramePipelineAdvanceResult result = MakeAdvanceResult(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted, CaptureFramePngQueueStatus.None, artifact, receipt);

            Assert.That(result.CompletedArtifact, Is.SameAs(artifact));
            Assert.That(result.SidecarReceipt, Is.SameAs(receipt));
            Assert.That(result.HasCompletedArtifact, Is.True);
        }

        [Test]
        public void AdvanceResult_NonCompletedStatus_RejectsArtifactReceipt()
        {
            TraceRunManifest manifest = MakeManifest();
            CaptureFrameRecord record = MakeRecord(manifest, 42, out CaptureFrameRequest request);
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakePngReceipt("C:\\x\\out.png", 32, FixedPngHash));
            CaptureFramePngArtifactSaveReceipt receipt = MakeSidecarReceipt("C:\\x\\out.json", 123, FixedPngHash);

            Assert.That(GetAdvanceResultCtorException(CaptureFramePngArtifactPersistenceStatus.None, CaptureFramePngQueueStatus.None, artifact, null), Is.InstanceOf<ArgumentException>());
            Assert.That(GetAdvanceResultCtorException(CaptureFramePngArtifactPersistenceStatus.None, CaptureFramePngQueueStatus.None, null, receipt), Is.InstanceOf<ArgumentException>());
            Assert.That(GetAdvanceResultCtorException(CaptureFramePngArtifactPersistenceStatus.PngPrepared, CaptureFramePngQueueStatus.None, artifact, receipt), Is.InstanceOf<ArgumentException>());
        }

        [Test]
        public void AdvanceResult_UndefinedEnums_Rejected()
        {
            Assert.That(GetAdvanceResultCtorException((CaptureFramePngArtifactPersistenceStatus)999, CaptureFramePngQueueStatus.None, null, null), Is.InstanceOf<ArgumentException>());
            Assert.That(GetAdvanceResultCtorException(CaptureFramePngArtifactPersistenceStatus.None, (CaptureFramePngQueueStatus)999, null, null), Is.InstanceOf<ArgumentException>());
        }

        // ---- Split API tests ----

        [Test]
        public void AdvancePendingWork_PersistenceAtMostOnce()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out CaptureFramePngArtifactQueue artifactQueue, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);
                    pipeline.Tick(rt); // start
                    AsyncGPUReadback.WaitAllRequests();
                    pipeline.Tick(rt); // collect → PNG queue 1

                    PngJsonCaptureFramePipelineAdvanceResult result = pipeline.AdvancePendingWork();

                    Assert.That(result.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(pngQueue.Count, Is.EqualTo(0));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void AdvancePendingWork_CollectAtMostOnce()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);
                    pipeline.Tick(rt); // start 1
                    pipeline.Tick(rt); // start 2
                    AsyncGPUReadback.WaitAllRequests();

                    PngJsonCaptureFramePipelineAdvanceResult result = pipeline.AdvancePendingWork();

                    Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void AdvancePendingWork_DoesNotStartPendingRequest()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);

                    PngJsonCaptureFramePipelineAdvanceResult result = pipeline.AdvancePendingWork();

                    Assert.That(result.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
                    Assert.That(result.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.None));
                    Assert.That(requestQueue.Count, Is.EqualTo(1));
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void TryStartNextReadback_StartsAtMostOne()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);

                    bool started = pipeline.TryStartNextReadback(rt);

                    Assert.That(started, Is.True);
                    Assert.That(requestQueue.Count, Is.EqualTo(1));
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void TryStartNextReadback_DoesNotAdvancePersistence()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out CaptureFramePngArtifactQueue artifactQueue, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    pipeline.Tick(rt); // start 1
                    AsyncGPUReadback.WaitAllRequests();
                    pipeline.Tick(rt); // collect 1 → PNG queue 1
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _); // pending 2

                    bool started = pipeline.TryStartNextReadback(rt);

                    Assert.That(started, Is.True);
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void TryStartNextReadback_EmptyQueue_NullSource_False()
        {
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out _, out _, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                try
                {
                    Assert.That(pipeline.TryStartNextReadback(null), Is.False);
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, null, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void TryStartNextReadback_InvalidSource_ExceptionAndRequestKept()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out _, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                RenderTexture uncreated = null;
                RenderTexture small = null;
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);

                    Assert.Throws<ArgumentNullException>(() => pipeline.TryStartNextReadback(null));
                    Assert.That(requestQueue.Count, Is.EqualTo(1));

                    uncreated = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32);
                    Assert.Throws<ArgumentException>(() => pipeline.TryStartNextReadback(uncreated));
                    Assert.That(requestQueue.Count, Is.EqualTo(1));

                    small = CreateTex2D(1, 1);
                    Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.TryStartNextReadback(small));
                    Assert.That(requestQueue.Count, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    Exception[] gpuErrors = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);

                    try
                    {
                        DestroyTexture(uncreated);
                    }
                    catch (Exception ex)
                    {
                        gpuErrors = AppendCleanupException(gpuErrors, ex);
                    }

                    try
                    {
                        DestroyTexture(small);
                    }
                    catch (Exception ex)
                    {
                        gpuErrors = AppendCleanupException(gpuErrors, ex);
                    }

                    cleanupExceptions = gpuErrors;
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void SplitApi_StartException_DoesNotRollbackAdvance()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out CaptureFramePngArtifactQueue artifactQueue, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    // A collected into the PNG queue.
                    RegisterAndSchedule(registry, requestQueue, manifest, 1, out _);
                    pipeline.Tick(rt); // start A
                    AsyncGPUReadback.WaitAllRequests();
                    pipeline.Tick(rt); // collect A → PNG queue 1

                    // B pending.
                    RegisterAndSchedule(registry, requestQueue, manifest, 2, out _);

                    PngJsonCaptureFramePipelineAdvanceResult advance = pipeline.AdvancePendingWork();
                    Assert.That(advance.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));

                    Assert.Throws<ArgumentNullException>(() => pipeline.TryStartNextReadback(null));

                    // The prepared artifact is not rolled back and B is still pending.
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(pngQueue.Count, Is.EqualTo(0));
                    Assert.That(requestQueue.Count, Is.EqualTo(1));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void Tick_EqualsAdvanceThenStart()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir1 = CreateTempDir();
            string dir2 = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline1 = MakePipeline(dir1, 2, out CaptureFrameReadbackBufferPool pool1, out UnityRenderTextureReadbackDispatcher dispatcher1, out CaptureFrameRequestQueue requestQueue1, out CaptureFrameRecordRegistry registry1, out CaptureFramePngQueue pngQueue1, out CaptureFramePngArtifactQueue artifactQueue1, out TraceLogger logger1);
                PngJsonCaptureFramePipelineCoordinator pipeline2 = MakePipeline(dir2, 2, out CaptureFrameReadbackBufferPool pool2, out UnityRenderTextureReadbackDispatcher dispatcher2, out CaptureFrameRequestQueue requestQueue2, out CaptureFrameRecordRegistry registry2, out CaptureFramePngQueue pngQueue2, out CaptureFramePngArtifactQueue artifactQueue2, out TraceLogger logger2);
                RenderTexture rt1 = CreateTex2D(2, 2);
                RenderTexture rt2 = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry1, requestQueue1, manifest, 42, out _);
                    RegisterAndSchedule(registry2, requestQueue2, manifest, 42, out _);

                    PngJsonCaptureFramePipelineTickResult tick = pipeline1.Tick(rt1);

                    PngJsonCaptureFramePipelineAdvanceResult advance = pipeline2.AdvancePendingWork();
                    bool started = pipeline2.TryStartNextReadback(rt2);

                    Assert.That(advance.PersistenceStatus, Is.EqualTo(tick.PersistenceStatus));
                    Assert.That(advance.ReadbackCompletionStatus, Is.EqualTo(tick.ReadbackCompletionStatus));
                    Assert.That(started, Is.EqualTo(tick.ReadbackStarted));
                    Assert.That(advance.CompletedArtifact, Is.SameAs(tick.CompletedArtifact));
                    Assert.That(advance.SidecarReceipt, Is.SameAs(tick.SidecarReceipt));

                    Assert.That(requestQueue1.Count, Is.EqualTo(requestQueue2.Count));
                    Assert.That(pngQueue1.Count, Is.EqualTo(pngQueue2.Count));
                    Assert.That(artifactQueue1.Count, Is.EqualTo(artifactQueue2.Count));
                    Assert.That(dispatcher1.ActiveCount, Is.EqualTo(dispatcher2.ActiveCount));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = ConcatExceptions(
                        CleanupGpuTest(dispatcher1, rt1, pngQueue1, pool1, logger1),
                        CleanupGpuTest(dispatcher2, rt2, pngQueue2, pool2, logger2));
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir1));
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir2));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void SplitApi_DoesNotDisposeOrClearDependencies()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                PngJsonCaptureFramePipelineCoordinator pipeline = MakePipeline(dir, 2, out CaptureFrameReadbackBufferPool pool, out UnityRenderTextureReadbackDispatcher dispatcher, out CaptureFrameRequestQueue requestQueue, out CaptureFrameRecordRegistry registry, out CaptureFramePngQueue pngQueue, out CaptureFramePngArtifactQueue artifactQueue, out TraceLogger logger);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    RegisterAndSchedule(registry, requestQueue, manifest, 42, out _);

                    pipeline.AdvancePendingWork();
                    pipeline.TryStartNextReadback(rt);

                    Assert.That(pngQueue.IsCreated, Is.True);
                    Assert.That(dispatcher.IsCreated, Is.True);
                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
                    Assert.That(pngQueue.Count, Is.EqualTo(0));
                    Assert.That(requestQueue.Count, Is.EqualTo(0));
                }
                catch (Exception ex)
                {
                    bodyException = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    cleanupExceptions = CleanupGpuTest(dispatcher, rt, pngQueue, pool, logger);
                }
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void NotIDisposable()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(PngJsonCaptureFramePipelineCoordinator)), Is.False);
        }
    }
}
