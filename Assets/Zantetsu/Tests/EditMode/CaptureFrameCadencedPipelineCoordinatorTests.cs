using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameCadencedPipelineCoordinatorTests
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

        private static CaptureRunReference MakeRun(long testRunId = 1, long testCaseId = 100, int captureProfileId = 5)
        {
            TraceRunManifest manifest = MakeManifest(testRunId);
            return new CaptureRunReference(manifest, testCaseId, captureProfileId, TraceRunManifestCodec.ComputeContentSha256(manifest));
        }

        private static CaptureFrameIdSequence MakeSequence()
        {
            return new CaptureFrameIdSequence();
        }

        private static CaptureFrameTiming MakeTiming(double predictedDisplayTimeSeconds, bool shouldRender)
        {
            return new CaptureFrameTiming(predictedDisplayTimeSeconds, 1.0 / 90.0, shouldRender, 0.0, 0.0, 0L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId = 42)
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

        private static CaptureFrameRecordFactory MakeFactory(CaptureRunReference run = null, CaptureFrameIdSequence sequence = null)
        {
            return new CaptureFrameRecordFactory(
                run ?? MakeRun(),
                sequence ?? MakeSequence(),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRecord MakeRecordForTest()
        {
            return MakeFactory().Create(
                1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
                MakeTiming(0.0, true),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1);
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

        private static Exception[] CleanupGpuTest(
            UnityRenderTextureReadbackDispatcher dispatcher,
            RenderTexture rt,
            CaptureFramePngQueue pngQueue,
            CaptureFrameReadbackBufferPool pool,
            TraceLogger logger)
        {
            List<Exception> errors = new List<Exception>();

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

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-cadenced-pipeline-" + Guid.NewGuid().ToString("N"));
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

        private sealed class Harness
        {
            public string Dir;
            public TraceLogger Logger;
            public CaptureFrameRequestQueue Queue;
            public CaptureFrameRecordRegistry Registry;
            public UnityRenderTextureReadbackDispatcher Dispatcher;
            public CaptureFrameReadbackBufferPool Pool;
            public CaptureFramePngQueue PngQueue;
            public CaptureFramePngArtifactQueue ArtifactQueue;
            public CaptureFrameIdSequence Sequence;
            public CaptureFrameCadenceSelector Selector;
            public CaptureFramePipelineCoordinator PipelineCoordinator;
            public CaptureFrameCadencedSubmissionCoordinator SubmissionCoordinator;
            public CaptureFrameCadencedPipelineCoordinator Coordinator;
        }

        private static Harness MakeHarness(double targetFps, int queueCapacity, int registryCapacity, int poolSlotCount, int pngQueueCapacity = 4)
        {
            string dir = CreateTempDir();
            TraceLogger logger = new TraceLogger(16);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(queueCapacity);
            CaptureFrameRequestScheduler requestScheduler = new CaptureFrameRequestScheduler(queue, observer);
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(registryCapacity);
            CaptureFrameRecordScheduler recordScheduler = new CaptureFrameRecordScheduler(requestScheduler, registry, observer);

            CaptureFrameIdSequence sequence = MakeSequence();
            CaptureFrameRecordFactory factory = MakeFactory(sequence: sequence);
            CaptureFrameRecordSubmissionCoordinator submission = new CaptureFrameRecordSubmissionCoordinator(factory, recordScheduler);
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(targetFps);
            CaptureFrameCadencedSubmissionCoordinator cadencedSubmission = new CaptureFrameCadencedSubmissionCoordinator(selector, submission);

            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(poolSlotCount, 64);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
            CaptureFrameReadbackPump readbackPump = new CaptureFrameReadbackPump(queue, dispatcher);
            CaptureFrameReadbackCompletionRouter completionRouter = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);

            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(pngQueueCapacity);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
            CaptureFramePngArtifactPersistenceCoordinator persistenceCoordinator = MakePersistenceCoordinator(registry, dir);

            CaptureFramePipelineCoordinator pipeline = new CaptureFramePipelineCoordinator(readbackPump, completionRouter, persistenceCoordinator, pngQueue, artifactQueue, registry);
            CaptureFrameCadencedPipelineCoordinator coordinator = new CaptureFrameCadencedPipelineCoordinator(pipeline, cadencedSubmission, queue);

            return new Harness
            {
                Dir = dir,
                Logger = logger,
                Queue = queue,
                Registry = registry,
                Dispatcher = dispatcher,
                Pool = pool,
                PngQueue = pngQueue,
                ArtifactQueue = artifactQueue,
                Sequence = sequence,
                Selector = selector,
                PipelineCoordinator = pipeline,
                SubmissionCoordinator = cadencedSubmission,
                Coordinator = coordinator,
            };
        }

        private static CaptureFrameCadencedPipelineResult Submit(
            CaptureFrameCadencedPipelineCoordinator coordinator,
            double predictedDisplayTimeSeconds,
            RenderTexture source,
            bool shouldRender = true,
            int commitPathId = 1)
        {
            return coordinator.TrySubmit(
                1000,
                200,
                300,
                4,
                500,
                600,
                700,
                800,
                9,
                1000,
                MakeTiming(predictedDisplayTimeSeconds, shouldRender),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                commitPathId,
                source);
        }

        private static ConstructorInfo GetResultCtor()
        {
            ConstructorInfo ctor = typeof(CaptureFrameCadencedPipelineResult).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[]
                {
                    typeof(CaptureFramePipelineAdvanceResult),
                    typeof(CaptureFrameCadencedSubmissionStatus),
                    typeof(bool),
                    typeof(CaptureFrameRecord)
                },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor;
        }

        private static Exception GetResultCtorException(CaptureFrameCadencedSubmissionStatus status, bool readbackStarted, CaptureFrameRecord record)
        {
            try
            {
                GetResultCtor().Invoke(new object[] { default(CaptureFramePipelineAdvanceResult), status, readbackStarted, record });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static CaptureFrameCadencedPipelineResult MakeResult(CaptureFrameCadencedSubmissionStatus status, bool readbackStarted, CaptureFrameRecord record)
        {
            return (CaptureFrameCadencedPipelineResult)GetResultCtor().Invoke(
                new object[] { default(CaptureFramePipelineAdvanceResult), status, readbackStarted, record });
        }

        // ---- Result tests ----

        [Test]
        public void Result_DefaultValues()
        {
            CaptureFrameCadencedPipelineResult result = default;

            Assert.That(result.AdvanceResult.PersistenceStatus, Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
            Assert.That(result.AdvanceResult.ReadbackCompletionStatus, Is.EqualTo(CaptureFramePngQueueStatus.None));
            Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.None));
            Assert.That(result.ReadbackStarted, Is.False);
            Assert.That(result.AcceptedRecord, Is.Null);
            Assert.That(result.HasAcceptedRecord, Is.False);
        }

        [Test]
        public void Result_SubmittedRequiresRecord()
        {
            Assert.That(GetResultCtorException(CaptureFrameCadencedSubmissionStatus.Submitted, false, null), Is.InstanceOf<ArgumentNullException>());

            CaptureFrameRecord record = MakeRecordForTest();
            CaptureFrameCadencedPipelineResult result = MakeResult(CaptureFrameCadencedSubmissionStatus.Submitted, false, record);

            Assert.That(result.AcceptedRecord, Is.SameAs(record));
            Assert.That(result.HasAcceptedRecord, Is.True);
        }

        [Test]
        public void Result_NonSubmittedRejectsRecord()
        {
            CaptureFrameRecord record = MakeRecordForTest();

            Assert.That(GetResultCtorException(CaptureFrameCadencedSubmissionStatus.None, false, record), Is.InstanceOf<ArgumentException>());
            Assert.That(GetResultCtorException(CaptureFrameCadencedSubmissionStatus.NotSelected, false, record), Is.InstanceOf<ArgumentException>());
            Assert.That(GetResultCtorException(CaptureFrameCadencedSubmissionStatus.Backpressured, false, record), Is.InstanceOf<ArgumentException>());

            CaptureFrameCadencedPipelineResult result = MakeResult(CaptureFrameCadencedSubmissionStatus.NotSelected, false, null);
            Assert.That(result.AcceptedRecord, Is.Null);
            Assert.That(result.HasAcceptedRecord, Is.False);
        }

        [Test]
        public void Result_ReadbackStartedRequiresSubmitted()
        {
            Assert.That(GetResultCtorException(CaptureFrameCadencedSubmissionStatus.NotSelected, true, null), Is.InstanceOf<ArgumentException>());
            Assert.That(GetResultCtorException(CaptureFrameCadencedSubmissionStatus.Backpressured, true, null), Is.InstanceOf<ArgumentException>());

            CaptureFrameRecord record = MakeRecordForTest();
            CaptureFrameCadencedPipelineResult result = MakeResult(CaptureFrameCadencedSubmissionStatus.Submitted, true, record);
            Assert.That(result.ReadbackStarted, Is.True);
        }

        [Test]
        public void Result_UndefinedStatusRejected()
        {
            Assert.That(GetResultCtorException((CaptureFrameCadencedSubmissionStatus)999, false, null), Is.InstanceOf<ArgumentException>());
        }

        // ---- Coordinator tests ----

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            Harness h = MakeHarness(45.0, 4, 4, 2);
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameCadencedPipelineCoordinator(null, h.SubmissionCoordinator, h.Queue));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameCadencedPipelineCoordinator(h.PipelineCoordinator, null, h.Queue));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameCadencedPipelineCoordinator(h.PipelineCoordinator, h.SubmissionCoordinator, null));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(
                    CleanupGpuTest(h.Dispatcher, null, h.PngQueue, h.Pool, h.Logger),
                    DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void NormalFlow_SubmittedAndStarted()
        {
            Harness h = MakeHarness(45.0, 4, 4, 2);
            RenderTexture rt = CreateTex2D(2, 2);
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, 0.0, rt);

                Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(result.ReadbackStarted, Is.True);
                Assert.That(result.AcceptedRecord, Is.Not.Null);
                Assert.That(h.Registry.Count, Is.EqualTo(1));
                Assert.That(h.Queue.Count, Is.EqualTo(0));
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(1));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(
                    CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger),
                    DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void SubmittedRequest_MatchesCollected()
        {
            Harness h = MakeHarness(45.0, 4, 4, 2);
            RenderTexture rt = CreateTex2D(2, 2);
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, 0.0, rt);
                Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(result.ReadbackStarted, Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(h.Dispatcher.TryCollect(out CaptureFrameReadbackResult collected), Is.True);
                AssertRequestIdentical(result.AcceptedRecord.Request, collected.FrameRequest);
                h.Dispatcher.Release(collected);
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(
                    CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger),
                    DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void NotSelected_NullOrUncreatedSource_NoStart()
        {
            Harness h = MakeHarness(45.0, 4, 4, 2);
            RenderTexture uncreated = null;
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                CaptureFrameCadencedPipelineResult r1 = Submit(h.Coordinator, 0.0, null, shouldRender: false);
                Assert.That(r1.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(r1.ReadbackStarted, Is.False);
                Assert.That(r1.AcceptedRecord, Is.Null);

                uncreated = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32);
                CaptureFrameCadencedPipelineResult r2 = Submit(h.Coordinator, 0.0, uncreated, shouldRender: false);
                Assert.That(r2.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(r2.ReadbackStarted, Is.False);

                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(h.Queue.Count, Is.EqualTo(0));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Exception[] gpuErrors = CleanupGpuTest(h.Dispatcher, null, h.PngQueue, h.Pool, h.Logger);
                try
                {
                    DestroyTexture(uncreated);
                }
                catch (Exception ex)
                {
                    gpuErrors = AppendCleanupException(gpuErrors, ex);
                }

                cleanupExceptions = AppendCleanupException(gpuErrors, DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void Backpressured_SourceNotUsed_NoStart()
        {
            Harness h = MakeHarness(45.0, 4, 1, 2);
            RenderTexture rt = CreateTex2D(2, 2);
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                CaptureFrameCadencedPipelineResult first = Submit(h.Coordinator, 0.0, rt);
                Assert.That(first.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));

                // Registry full: backpressured without using (or validating) source.
                CaptureFrameCadencedPipelineResult second = Submit(h.Coordinator, 0.03, null);
                Assert.That(second.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Backpressured));
                Assert.That(second.ReadbackStarted, Is.False);
                Assert.That(second.AcceptedRecord, Is.Null);
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(
                    CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger),
                    DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void PreviousPendingRequest_FailClosed_NoCadenceIdTrace()
        {
            Harness h = MakeHarness(45.0, 4, 4, 2);
            RenderTexture rt = CreateTex2D(2, 2);
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                Assert.That(h.Queue.TryEnqueue(MakeRequest(99)), Is.True);

                Assert.Throws<InvalidOperationException>(() => Submit(h.Coordinator, 0.0, rt));

                Assert.That(h.Sequence.LastIssued, Is.EqualTo(0));
                Assert.That(h.Selector.HasObservedTimestamp, Is.False);
                Assert.That(h.Registry.Count, Is.EqualTo(0));
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(h.Queue.Count, Is.EqualTo(1));

                Assert.That(h.Logger.Drain(), Is.EqualTo(0));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(
                    CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger),
                    DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void PreviousPendingRequest_AdvanceNotRolledBack()
        {
            Harness h = MakeHarness(45.0, 4, 4, 2);
            RenderTexture rt = CreateTex2D(2, 2);
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                Submit(h.Coordinator, 0.0, rt);
                AsyncGPUReadback.WaitAllRequests();

                Assert.That(h.Queue.TryEnqueue(MakeRequest(99)), Is.True);

                Assert.Throws<InvalidOperationException>(() => Submit(h.Coordinator, 0.03, rt));

                // The advance that ran before the pending detection is kept.
                Assert.That(h.PngQueue.Count, Is.EqualTo(1));
                Assert.That(h.Queue.Count, Is.EqualTo(1));
                Assert.That(h.Sequence.LastIssued, Is.EqualTo(1));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(
                    CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger),
                    DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void PoolFull_StartFalse_RecordAndRequestKept()
        {
            Harness h = MakeHarness(45.0, 4, 4, 1);
            RenderTexture rt = CreateTex2D(2, 2);
            int reservedSlot = -1;
            bool reservedSlotHeld = false;
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                // Deterministically exhaust the single buffer pool slot.
                Assert.That(h.Pool.TryRent(out reservedSlot), Is.True);
                reservedSlotHeld = true;

                CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, 0.0, rt);

                Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(result.ReadbackStarted, Is.False);
                Assert.That(result.AcceptedRecord, Is.Not.Null);

                Assert.That(h.Registry.Count, Is.EqualTo(1));
                Assert.That(h.Queue.Count, Is.EqualTo(1));
                Assert.That(h.Sequence.LastIssued, Is.EqualTo(1));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Exception[] errors = null;
                if (reservedSlotHeld)
                {
                    reservedSlotHeld = false;
                    try
                    {
                        h.Pool.Return(reservedSlot);
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

                cleanupExceptions = ConcatExceptions(errors, CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger));
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void RetryStart_SameSource_StartsCorrectRequest()
        {
            Harness h = MakeHarness(45.0, 4, 4, 1);
            RenderTexture rt = CreateTex2D(2, 2);
            int reservedSlot = -1;
            bool reservedSlotHeld = false;
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                Assert.That(h.Pool.TryRent(out reservedSlot), Is.True);
                reservedSlotHeld = true;

                CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, 0.0, rt);
                Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(result.ReadbackStarted, Is.False);
                Assert.That(h.Queue.Count, Is.EqualTo(1));

                // Free the reserved slot and retry the start with the same source.
                h.Pool.Return(reservedSlot);
                reservedSlotHeld = false;

                Assert.That(h.PipelineCoordinator.TryStartNextReadback(rt), Is.True);
                Assert.That(h.Queue.Count, Is.EqualTo(0));
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(1));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Exception[] errors = null;
                if (reservedSlotHeld)
                {
                    reservedSlotHeld = false;
                    try
                    {
                        h.Pool.Return(reservedSlot);
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

                cleanupExceptions = ConcatExceptions(errors, CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger));
                cleanupExceptions = AppendCleanupException(cleanupExceptions, DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void StartException_KeepsRecordRequestAndCadence()
        {
            Harness h = MakeHarness(45.0, 4, 4, 2);
            RenderTexture rt = CreateTex2D(2, 2);
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                Submit(h.Coordinator, 0.0, rt);
                AsyncGPUReadback.WaitAllRequests();

                Assert.Throws<ArgumentNullException>(() => Submit(h.Coordinator, 0.03, null));

                // Advance, cadence selection, record, and ID are all kept.
                Assert.That(h.PngQueue.Count, Is.EqualTo(1));
                Assert.That(h.Selector.LastSelectedTimestampSeconds, Is.EqualTo(0.03));
                Assert.That(h.Registry.Count, Is.EqualTo(2));
                Assert.That(h.Queue.Count, Is.EqualTo(1));
                Assert.That(h.Sequence.LastIssued, Is.EqualTo(2));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(
                    CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger),
                    DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void SingleCall_SubmitsAndStartsAtMostOnce()
        {
            Harness h = MakeHarness(45.0, 4, 4, 2);
            RenderTexture rt = CreateTex2D(2, 2);
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                CaptureFrameCadencedPipelineResult result = Submit(h.Coordinator, 0.0, rt);

                Assert.That(result.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(result.ReadbackStarted, Is.True);

                Assert.That(h.Registry.Count, Is.EqualTo(1));
                Assert.That(h.Dispatcher.ActiveCount, Is.EqualTo(1));
                Assert.That(h.Sequence.LastIssued, Is.EqualTo(1));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(
                    CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger),
                    DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void DoesNotDisposeOrClearDependencies()
        {
            Harness h = MakeHarness(45.0, 4, 4, 2);
            RenderTexture rt = CreateTex2D(2, 2);
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                Submit(h.Coordinator, 0.0, rt);

                Assert.That(h.Logger.IsCreated, Is.True);
                Assert.That(h.PngQueue.IsCreated, Is.True);
                Assert.That(h.Dispatcher.IsCreated, Is.True);
                Assert.That(h.Registry.Count, Is.EqualTo(1));
                Assert.That(h.Sequence.LastIssued, Is.EqualTo(1));
                Assert.That(h.Sequence.Next(), Is.EqualTo(2));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(
                    CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger),
                    DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }

        [Test]
        public void HoldsNoRecordOrTexture()
        {
            foreach (FieldInfo field in typeof(CaptureFrameCadencedPipelineCoordinator).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRecord)), "Must not retain a produced record.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(RenderTexture)));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(TraceLogger)));
                Assert.That(typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType), Is.False);
            }
        }

        [Test]
        public void SealedNotIDisposableNotMonoBehaviour()
        {
            Assert.That(typeof(CaptureFrameCadencedPipelineCoordinator).IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFrameCadencedPipelineCoordinator)), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(typeof(CaptureFrameCadencedPipelineCoordinator)), Is.False);
        }

        [Test]
        public void EndToEnd_SubmittedRequestMatchesCollectedAndPng()
        {
            Harness h = MakeHarness(45.0, 4, 4, 2);
            RenderTexture rt = CreateTex2D(2, 2);
            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupExceptions = null;
            try
            {
                CaptureFrameCadencedPipelineResult first = Submit(h.Coordinator, 0.0, rt);
                Assert.That(first.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(first.ReadbackStarted, Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(h.Dispatcher.TryCollect(out CaptureFrameReadbackResult collected), Is.True);
                AssertRequestIdentical(first.AcceptedRecord.Request, collected.FrameRequest);
                h.Dispatcher.Release(collected);

                CaptureFrameCadencedPipelineResult second = Submit(h.Coordinator, 0.03, rt);
                Assert.That(second.SubmissionStatus, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(second.ReadbackStarted, Is.True);
                Assert.That(second.AcceptedRecord.CaptureFrameId, Is.EqualTo(2));

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(h.Dispatcher.TryCollect(out CaptureFrameReadbackResult secondCollected), Is.True);
                AssertRequestIdentical(second.AcceptedRecord.Request, secondCollected.FrameRequest);
                h.Dispatcher.Release(secondCollected);
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cleanupExceptions = AppendCleanupException(
                    CleanupGpuTest(h.Dispatcher, rt, h.PngQueue, h.Pool, h.Logger),
                    DeleteTempDir(h.Dir));
            }

            ThrowCleanupAndBody(bodyException, cleanupExceptions);
        }
    }
}
