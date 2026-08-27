using System;
using System.Collections.Generic;
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
    public class CaptureFrameRenderTargetRecordSchedulerTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceRunManifest MakeManifest(long testRunId = 7)
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

        private static CaptureFrameProfile MakeProfile()
        {
            return CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId, long unityFrameId = 2)
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, unityFrameId, 3, 4, captureFrameId, 6, 7, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(1.0, 1.0 / 90.0, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecord MakeRecord(CaptureFrameRequest request)
        {
            TraceRunManifest manifest = MakeManifest(request.TraceContext.TestRunId);
            CaptureRunReference run = new CaptureRunReference(
                manifest,
                100,
                5,
                TraceRunManifestCodec.ComputeContentSha256(manifest));

            return new CaptureFrameRecord(
                run,
                request,
                MakeTiming(),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1);
        }

        private static void AssertPngSignature(NativeArray<byte> png)
        {
            Assert.That(png.Length, Is.GreaterThan(8));
            for (int i = 0; i < 8; i++)
            {
                Assert.That(png[i], Is.EqualTo(PngSignature[i]), "PNG signature mismatch at byte " + i);
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

        private sealed class SchedulerScope
        {
            public TraceLogger Logger;
            public CaptureFrameTraceObserver Observer;
            public CaptureFrameRequestQueue RequestQueue;
            public CaptureFrameRequestScheduler RequestScheduler;
            public CaptureFrameRecordRegistry RecordRegistry;
            public CaptureFrameRecordScheduler RecordScheduler;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameRenderTargetLeaseRegistry LeaseRegistry;
            public CaptureFrameRenderTargetRecordScheduler Scheduler;
            public readonly List<CaptureFrameRenderTargetLease> Held = new List<CaptureFrameRenderTargetLease>();
            public readonly List<RegisteredEntry> Registered = new List<RegisteredEntry>();

            public CaptureFrameRenderTargetLease Rent()
            {
                Assert.That(Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                Held.Add(lease);
                return lease;
            }

            public bool ScheduleAndTrack(CaptureFrameRecord record, CaptureFrameRenderTargetLease lease)
            {
                bool result = Scheduler.TrySchedule(record, lease);
                if (result)
                {
                    RemoveFromHeld(lease);
                    Registered.Add(new RegisteredEntry(record.Request, lease));
                }

                return result;
            }

            public void ReturnHeld(CaptureFrameRenderTargetLease lease)
            {
                Pool.Return(lease);
                RemoveFromHeld(lease);
            }

            private void RemoveFromHeld(CaptureFrameRenderTargetLease lease)
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
        }

        private static SchedulerScope NewScope(int poolCapacity, int leaseCapacity, int recordCapacity, int queueCapacity)
        {
            SchedulerScope scope = new SchedulerScope();
            scope.Logger = new TraceLogger(8);
            scope.Observer = new CaptureFrameTraceObserver(scope.Logger);
            scope.RequestQueue = new CaptureFrameRequestQueue(queueCapacity);
            scope.RequestScheduler = new CaptureFrameRequestScheduler(scope.RequestQueue, scope.Observer);
            scope.RecordRegistry = new CaptureFrameRecordRegistry(recordCapacity);
            scope.RecordScheduler = new CaptureFrameRecordScheduler(scope.RequestScheduler, scope.RecordRegistry, scope.Observer);
            scope.Pool = new CaptureFrameRenderTargetPool(poolCapacity, MakeProfile());
            scope.LeaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(leaseCapacity, scope.Pool);
            scope.Scheduler = new CaptureFrameRenderTargetRecordScheduler(scope.RecordScheduler, scope.LeaseRegistry);
            return scope;
        }

        private static Exception[] CleanupSchedulerScope(SchedulerScope scope)
        {
            Exception[] errors = null;

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

            return errors;
        }

        private static void RunSchedulerBody(SchedulerScope scope, Action body)
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

            Exception[] errors = CleanupSchedulerScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            SchedulerScope scope = NewScope(2, 2, 2, 2);
            RunSchedulerBody(scope, () =>
            {
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetRecordScheduler(null, scope.LeaseRegistry));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetRecordScheduler(scope.RecordScheduler, null));
            });
        }

        [Test]
        public void NullRecord_Rejected_AllUnchanged()
        {
            SchedulerScope scope = NewScope(2, 2, 2, 2);
            RunSchedulerBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = scope.Rent();

                Assert.Throws<ArgumentNullException>(() => scope.Scheduler.TrySchedule(null, lease));

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void Success_AllRegistered_LeaseOwnershipTransferred()
        {
            SchedulerScope scope = NewScope(2, 2, 2, 2);
            RunSchedulerBody(scope, () =>
            {
                CaptureFrameRecord record = MakeRecord(MakeRequest(42));
                CaptureFrameRenderTargetLease lease = scope.Rent();

                Assert.That(scope.ScheduleAndTrack(record, lease), Is.True);

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.TryGet(record.Request, out CaptureFrameRecord kept), Is.True);
                Assert.That(ReferenceEquals(kept, record), Is.True);

                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.TryGet(record.Request, out CaptureFrameRenderTargetLease registeredLease), Is.True);
                Assert.That(registeredLease.SlotIndex, Is.EqualTo(lease.SlotIndex));

                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void LeaseRegistryFull_ReturnsFalse_RecordSchedulerUntouched()
        {
            SchedulerScope scope = NewScope(2, 1, 2, 2);
            RunSchedulerBody(scope, () =>
            {
                CaptureFrameRecord otherRecord = MakeRecord(MakeRequest(1));
                CaptureFrameRenderTargetLease otherLease = scope.Rent();
                Assert.That(scope.ScheduleAndTrack(otherRecord, otherLease), Is.True);

                CaptureFrameRecord record = MakeRecord(MakeRequest(2));
                CaptureFrameRenderTargetLease lease = scope.Rent();

                Assert.That(scope.Scheduler.TrySchedule(record, lease), Is.False);

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.TotalAccepted, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.TotalRejected, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(2));
            });
        }

        [Test]
        public void RequestQueueFull_ExistingContract_LeaseRolledBack()
        {
            SchedulerScope scope = NewScope(2, 2, 2, 1);
            RunSchedulerBody(scope, () =>
            {
                Assert.That(scope.RequestQueue.TryEnqueue(MakeRequest(100)), Is.True);

                CaptureFrameRecord record = MakeRecord(MakeRequest(42));
                CaptureFrameRenderTargetLease lease = scope.Rent();

                Assert.That(scope.Scheduler.TrySchedule(record, lease), Is.False);

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));

                scope.ReturnHeld(lease);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));
                Assert.That(scope.Logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(scope.Logger.GetHistoryEvent(0).Value1, Is.EqualTo((int)CaptureFrameDropReason.RequestQueueFull));
            });
        }

        [Test]
        public void RecordRegistryFull_ExistingContract_LeaseRolledBack()
        {
            SchedulerScope scope = NewScope(2, 2, 1, 2);
            RunSchedulerBody(scope, () =>
            {
                CaptureFrameRecord existing = MakeRecord(MakeRequest(100));
                Assert.That(scope.RecordRegistry.TryRegister(existing), Is.True);

                CaptureFrameRecord record = MakeRecord(MakeRequest(42));
                CaptureFrameRenderTargetLease lease = scope.Rent();

                Assert.That(scope.Scheduler.TrySchedule(record, lease), Is.False);

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));
                Assert.That(scope.Logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(scope.Logger.GetHistoryEvent(0).Value1, Is.EqualTo((int)CaptureFrameDropReason.FrameRecordRegistryFull));
            });
        }

        [Test]
        public void DisposedLogger_SchedulerException_LeaseAndRecordRolledBack()
        {
            SchedulerScope scope = NewScope(2, 2, 2, 2);
            RunSchedulerBody(scope, () =>
            {
                CaptureFrameRecord record = MakeRecord(MakeRequest(42));
                CaptureFrameRenderTargetLease lease = scope.Rent();

                scope.Logger.Dispose();

                Assert.Throws<ObjectDisposedException>(() => scope.Scheduler.TrySchedule(record, lease));

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));

                scope.ReturnHeld(lease);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void DuplicateCaptureFrameId_LeaseNotLeftBehind()
        {
            SchedulerScope scope = NewScope(3, 3, 3, 3);
            RunSchedulerBody(scope, () =>
            {
                CaptureFrameRecord record1 = MakeRecord(MakeRequest(42));
                CaptureFrameRenderTargetLease lease1 = scope.Rent();
                Assert.That(scope.ScheduleAndTrack(record1, lease1), Is.True);

                CaptureFrameRequest otherRequest = new CaptureFrameRequest(
                    new CaptureFrameTraceContext(1, 99, 3, 4, 42, 6, 7, 8, 9, 10, 11, 12),
                    CaptureSource.UnityRenderTexture,
                    CaptureEye.Left,
                    new CaptureImageRect(0, 0, 2, 2),
                    0,
                    CapturePixelFormat.Rgba32);
                CaptureFrameRecord record2 = MakeRecord(otherRequest);
                CaptureFrameRenderTargetLease lease2 = scope.Rent();

                Assert.Throws<InvalidOperationException>(() => scope.Scheduler.TrySchedule(record2, lease2));

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void StaleLease_RejectedBeforeScheduler()
        {
            SchedulerScope scope = NewScope(2, 2, 2, 2);
            RunSchedulerBody(scope, () =>
            {
                CaptureFrameRecord record = MakeRecord(MakeRequest(42));

                CaptureFrameRenderTargetLease lease = scope.Rent();
                scope.ReturnHeld(lease);

                Assert.Throws<InvalidOperationException>(() => scope.Scheduler.TrySchedule(record, lease));

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void ForeignPoolLease_RejectedBeforeScheduler()
        {
            SchedulerScope scope = NewScope(2, 2, 2, 2);
            CaptureFrameRenderTargetPool foreignPool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            CaptureFrameRenderTargetLease foreignLease = default;
            bool foreignHeld = false;

            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                Assert.That(foreignPool.TryRent(out foreignLease), Is.True);
                foreignHeld = true;

                CaptureFrameRecord record = MakeRecord(MakeRequest(42));

                Assert.Throws<InvalidOperationException>(() => scope.Scheduler.TrySchedule(record, foreignLease));

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            if (foreignHeld)
            {
                foreignHeld = false;
                try { foreignPool.Return(foreignLease); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            }

            try { foreignPool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            errors = ConcatExceptions(errors, CleanupSchedulerScope(scope));
            ThrowCleanupAndBody(body, errors);
        }

        [Test]
        public void AfterRollback_SlotReRentable_OldLeaseStale()
        {
            SchedulerScope scope = NewScope(1, 1, 2, 1);
            RunSchedulerBody(scope, () =>
            {
                Assert.That(scope.RequestQueue.TryEnqueue(MakeRequest(100)), Is.True);

                CaptureFrameRecord record = MakeRecord(MakeRequest(42));
                CaptureFrameRenderTargetLease lease = scope.Rent();

                Assert.That(scope.Scheduler.TrySchedule(record, lease), Is.False);

                scope.ReturnHeld(lease);

                CaptureFrameRenderTargetLease newLease = scope.Rent();
                Assert.That(newLease.SlotIndex, Is.EqualTo(lease.SlotIndex));
                Assert.Throws<InvalidOperationException>(() => scope.Pool.GetRenderTexture(lease));
            });
        }

        [Test]
        public void DoesNotDisposeClearReturnDependencies()
        {
            SchedulerScope scope = NewScope(2, 2, 2, 2);
            RunSchedulerBody(scope, () =>
            {
                CaptureFrameRecord record = MakeRecord(MakeRequest(42));
                CaptureFrameRenderTargetLease lease = scope.Rent();

                Assert.That(scope.ScheduleAndTrack(record, lease), Is.True);

                Assert.That(scope.Pool.IsCreated, Is.True);
                Assert.That(scope.Logger.IsCreated, Is.True);
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void TypeShape_SealedNonDisposableNonMonoBehaviour()
        {
            Type type = typeof(CaptureFrameRenderTargetRecordScheduler);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void GpuIntegration_RentSchedulePumpCompleteEnqueueLeaseReturned()
        {
            CaptureFrameProfile profile = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11u, 12);
            CaptureFrameRequest request = new CaptureFrameRequest(context, profile.Source, profile.Eye, profile.ImageRect, profile.ArrayIndex, profile.PixelFormat);

            CaptureFrameRequestQueue requestQueue = new CaptureFrameRequestQueue(1);
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, profile);
            CaptureFrameReadbackBufferPool bufferPool = new CaptureFrameReadbackBufferPool(1, request.RequiredByteCount);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(1, pool);
            CaptureFrameRenderTargetReadbackPump pump = new CaptureFrameRenderTargetReadbackPump(requestQueue, dispatcher, leaseRegistry, pool);

            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameRequestScheduler requestScheduler = new CaptureFrameRequestScheduler(requestQueue, observer);
            CaptureFrameRecordRegistry recordRegistry = new CaptureFrameRecordRegistry(1);
            CaptureFrameRecordScheduler recordScheduler = new CaptureFrameRecordScheduler(requestScheduler, recordRegistry, observer);
            CaptureFrameRenderTargetRecordScheduler scheduler = new CaptureFrameRenderTargetRecordScheduler(recordScheduler, leaseRegistry);

            CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
            CaptureFramePngQueue queue = new CaptureFramePngQueue(1);

            CaptureFrameRecord record = MakeRecord(request);

            CaptureFrameRenderTargetLease lease = default;
            bool leaseHeld = false;
            bool registered = false;
            NativeArray<byte> png = default;
            bool pngHeld = false;

            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                Assert.That(pool.TryRent(out lease), Is.True);
                leaseHeld = true;

                Assert.That(scheduler.TrySchedule(record, lease), Is.True);
                registered = true;
                leaseHeld = false;

                Assert.That(pump.TryStartNext(), Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(router.TryCollectEncodeAndEnqueue(queue, recordRegistry, leaseRegistry, pool), Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                registered = false;

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(leaseRegistry.Count, Is.EqualTo(0));
                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(recordRegistry.Count, Is.EqualTo(1));

                Assert.That(queue.TryDequeue(out CaptureFrameRequest frameRequest, out png), Is.True);
                pngHeld = true;
                Assert.That(frameRequest.TraceContext.CaptureFrameId, Is.EqualTo(5));
                AssertPngSignature(png);
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            if (pngHeld)
            {
                pngHeld = false;
                try { png.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            }

            bool gpuSafe = true;

            try
            {
                AsyncGPUReadback.WaitAllRequests();
                if (dispatcher.IsCreated)
                {
                    while (dispatcher.TryCollect(out CaptureFrameReadbackResult extra))
                    {
                        dispatcher.Release(extra);
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
                if (registered)
                {
                    registered = false;
                    try
                    {
                        if (leaseRegistry.TryRemove(request, out CaptureFrameRenderTargetLease removed))
                        {
                            lease = removed;
                            leaseHeld = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

                if (leaseHeld)
                {
                    leaseHeld = false;
                    try { pool.Return(lease); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
                }
            }

            try { pool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (dispatcher.IsCreated) { dispatcher.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (bufferPool.IsCreated) { bufferPool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { queue.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { logger.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            ThrowCleanupAndBody(body, errors);
        }
    }
}
