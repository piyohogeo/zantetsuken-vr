using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameEncodeServiceBoundaryTests
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type RuntimeType(string name)
        {
            Type type = typeof(CaptureFramePngEncoder).Assembly.GetType("Zantetsu.Observability." + name, throwOnError: false);
            Assert.That(type, Is.Not.Null, name + " type not found.");
            return type;
        }

        private static object NewService(int capacity)
        {
            return Activator.CreateInstance(
                RuntimeType("PngJsonSynchronousCaptureFrameEncodeService"),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { capacity },
                culture: null);
        }

        private static object NewPayload(
            UnityRenderTextureReadbackDispatcher dispatcher,
            in CaptureFrameReadbackResult result)
        {
            return Activator.CreateInstance(
                RuntimeType("CaptureFrameReadbackPayloadLease"),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { dispatcher, result },
                culture: null);
        }

        private static object NewSubmission(object payload)
        {
            return Activator.CreateInstance(
                RuntimeType("PngJsonCaptureFrameEncodeSubmission"),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new[] { payload },
                culture: null);
        }

        private static object NewCoordinator(object service, CaptureFrameTraceObserver observer)
        {
            return Activator.CreateInstance(
                RuntimeType("PngJsonCaptureFrameEncodeCompletionCoordinator"),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { service, observer },
                culture: null);
        }

        private static int Submit(object service, object submission, out object token)
        {
            MethodInfo method = service.GetType().GetMethod("TrySubmit", InstanceFlags);
            Assert.That(method, Is.Not.Null);
            object[] args = { submission, null };
            object status = method.Invoke(service, args);
            token = args[1];
            return Convert.ToInt32(status);
        }

        private static bool Collect(object service, out object completion)
        {
            MethodInfo method = service.GetType().GetMethod("TryCollect", InstanceFlags);
            Assert.That(method, Is.Not.Null);
            object[] args = { null };
            bool collected = (bool)method.Invoke(service, args);
            completion = args[0];
            return collected;
        }

        private static object Apply(object coordinator, object completion)
        {
            MethodInfo method = coordinator.GetType().GetMethod("Apply", InstanceFlags);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(coordinator, new[] { completion });
        }

        private static Exception ApplyException(object coordinator, object completion)
        {
            try
            {
                Apply(coordinator, completion);
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static T Property<T>(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(name, InstanceFlags);
            Assert.That(property, Is.Not.Null, name + " property not found.");
            return (T)property.GetValue(target);
        }

        private static void Invoke(object target, string name)
        {
            MethodInfo method = target.GetType().GetMethod(name, InstanceFlags);
            Assert.That(method, Is.Not.Null, name + " method not found.");
            method.Invoke(target, null);
        }

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

        private static RenderTexture CreateTexture()
        {
            RenderTexture texture = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32);
            texture.Create();
            return texture;
        }

        private static void DestroyTexture(RenderTexture texture)
        {
            if (texture == null)
            {
                return;
            }

            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
        }

        [Test]
        public void WorkStage_IsIndependentAppendOnlyShape()
        {
            Type type = RuntimeType("CaptureFrameWorkStage");
            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetNames(type), Is.EqualTo(new[]
            {
                "ReadbackCompleted", "EncodeQueued", "Encoding", "Encoded",
                "SaveQueued", "Saving", "DurableStaged", "Published", "Dropped"
            }));
            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(9));
            for (int i = 0; i < values.Length; i++)
            {
                Assert.That(Convert.ToInt32(values.GetValue(i)), Is.EqualTo(i));
            }
            Assert.That(type, Is.Not.EqualTo(RuntimeType("CaptureFrameDraftStatus")));
        }

        [Test]
        public void ServiceContract_HasBoundedLifecycleAndNoDraftRegistryTraceFields()
        {
            Type serviceType = RuntimeType("PngJsonSynchronousCaptureFrameEncodeService");
            Assert.That(serviceType.GetMethod("TrySubmit", InstanceFlags), Is.Not.Null);
            Assert.That(serviceType.GetMethod("TryCollect", InstanceFlags), Is.Not.Null);
            Assert.That(serviceType.GetMethod("BeginDrain", InstanceFlags), Is.Not.Null);
            Assert.That(serviceType.GetMethod("CancelQueued", InstanceFlags), Is.Not.Null);
            Assert.That(serviceType.GetMethod("TryJoin", InstanceFlags), Is.Not.Null);
            Assert.That(typeof(IDisposable).IsAssignableFrom(serviceType), Is.True);

            foreach (FieldInfo field in serviceType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                Assert.That(field.FieldType.Name, Does.Not.Contain("Draft"));
                Assert.That(field.FieldType.Name, Does.Not.Contain("Registry"));
                Assert.That(field.FieldType.Name, Does.Not.Contain("Trace"));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(System.Threading.Thread)));
            }

            Type completionType = RuntimeType("PngJsonCaptureFrameEncodeCompletion");
            Assert.That(completionType.IsValueType, Is.True);
            Assert.That(completionType.IsByRefLike, Is.False);
            foreach (FieldInfo field in completionType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(NativeArray<byte>)));
                Assert.That(field.FieldType.Name, Does.Not.Contain("PayloadLease"));
                Assert.That(field.FieldType.Name, Does.Not.Contain("Draft"));
                Assert.That(field.FieldType.Name, Does.Not.Contain("Registry"));
                Assert.That(field.FieldType.Name, Does.Not.Contain("Trace"));
            }
        }

        [Test]
        public void ServiceDispose_IsIdempotentAndNormalApisFailClosed()
        {
            object service = NewService(1);
            Invoke(service, "Dispose");
            Invoke(service, "Dispose");

            TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() => Invoke(service, "BeginDrain"));
            Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>());
        }

        [Test]
        public void SlotGeneration_ExhaustedSlotSkipped_AllExhaustedThrows()
        {
            object service = NewService(2);
            long[] generations = (long[])service.GetType()
                .GetField("_generations", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(service);
            MethodInfo find = service.GetType().GetMethod("FindReusableSlot", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(find, Is.Not.Null);

            generations[0] = long.MaxValue;
            Assert.That(find.Invoke(service, null), Is.EqualTo(1));

            generations[1] = long.MaxValue;
            Assert.Throws<OverflowException>(() => find.Invoke(service, null));
            Invoke(service, "Dispose");
        }

        [Test]
        public void Accepted_Success_OwnershipMovesAndApplyReleasesOnce()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                RenderTexture texture = CreateTexture();
                NativeArray<byte> png = default;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), texture), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.True);

                    object service = NewService(1);
                    object payload = NewPayload(dispatcher, result);
                    object submission = NewSubmission(payload);
                    object coordinator = NewCoordinator(service, new CaptureFrameTraceObserver(logger));

                    Assert.That(Submit(service, submission, out object token), Is.EqualTo(0));
                    Assert.That(Property<bool>(submission, "HasPayload"), Is.False);
                    Assert.That(Property<bool>(payload, "IsCallerOwned"), Is.False);
                    Assert.That(pool.RentedCount, Is.EqualTo(1));
                    Assert.That(Collect(service, out object completion), Is.True);

                    object applied = Apply(coordinator, completion);
                    png = Property<NativeArray<byte>>(applied, "PngBytes");
                    Assert.That(png.IsCreated, Is.True);
                    Assert.That(Property<bool>(payload, "ReleaseSucceeded"), Is.True);
                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(1));
                    Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                    Assert.That(logger.GetHistoryEvent(0).CaptureFrameId, Is.EqualTo(1));

                    Exception duplicate = ApplyException(coordinator, completion);
                    Assert.That(duplicate, Is.TypeOf<InvalidOperationException>());
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                }
                finally
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }

                    AsyncGPUReadback.WaitAllRequests();
                    DestroyTexture(texture);
                }
            }
        }

        [Test]
        public void Backpressured_SubmissionRetainsPayloadOwnership()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                RenderTexture texture = CreateTexture();
                NativeArray<byte> png = default;
                CaptureFrameReadbackResult secondResult = default;
                object secondPayload = null;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), texture), Is.True);
                    Assert.That(dispatcher.TryStart(MakeRequest(2), texture), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult firstResult), Is.True);
                    Assert.That(dispatcher.TryCollect(out secondResult), Is.True);

                    object service = NewService(1);
                    object firstSubmission = NewSubmission(NewPayload(dispatcher, firstResult));
                    Assert.That(Submit(service, firstSubmission, out _), Is.EqualTo(0));

                    secondPayload = NewPayload(dispatcher, secondResult);
                    object secondSubmission = NewSubmission(secondPayload);
                    Assert.That(Submit(service, secondSubmission, out object rejectedToken), Is.EqualTo(1));
                    Assert.That(rejectedToken, Is.Not.Null);
                    Assert.That(Property<bool>(secondSubmission, "HasPayload"), Is.True);
                    Assert.That(Property<bool>(secondPayload, "IsCallerOwned"), Is.True);

                    Assert.That(Collect(service, out object firstCompletion), Is.True);
                    object coordinator = NewCoordinator(service, new CaptureFrameTraceObserver(logger));
                    object applied = Apply(coordinator, firstCompletion);
                    png = Property<NativeArray<byte>>(applied, "PngBytes");

                    Invoke(secondPayload, "ReleaseByCaller");
                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                }
                finally
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }

                    if (secondPayload != null && Property<bool>(secondPayload, "IsCallerOwned"))
                    {
                        Invoke(secondPayload, "ReleaseByCaller");
                    }

                    AsyncGPUReadback.WaitAllRequests();
                    DestroyTexture(texture);
                }
            }
        }

        [Test]
        public void BeginDrain_NotAcceptingRetainsPayload_CancelAndJoinAreSynchronous()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture texture = CreateTexture();
                object payload = null;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), texture), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.True);
                    object service = NewService(1);
                    payload = NewPayload(dispatcher, result);
                    object submission = NewSubmission(payload);

                    Invoke(service, "BeginDrain");
                    Assert.That(Submit(service, submission, out _), Is.EqualTo(2));
                    Assert.That(Property<bool>(submission, "HasPayload"), Is.True);
                    Assert.That(Property<bool>(payload, "IsCallerOwned"), Is.True);

                    MethodInfo cancel = service.GetType().GetMethod("CancelQueued", InstanceFlags);
                    MethodInfo join = service.GetType().GetMethod("TryJoin", InstanceFlags);
                    Assert.That(cancel.Invoke(service, null), Is.EqualTo(0));
                    Assert.That(join.Invoke(service, null), Is.EqualTo(true));

                    Invoke(payload, "ReleaseByCaller");
                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                }
                finally
                {
                    if (payload != null && Property<bool>(payload, "IsCallerOwned"))
                    {
                        Invoke(payload, "ReleaseByCaller");
                    }

                    AsyncGPUReadback.WaitAllRequests();
                    DestroyTexture(texture);
                }
            }
        }

        [Test]
        public void StaleCompletionAfterSlotReuse_RejectedWithoutTouchingCurrentWork()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                RenderTexture texture = CreateTexture();
                NativeArray<byte> png1 = default;
                NativeArray<byte> png2 = default;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), texture), Is.True);
                    Assert.That(dispatcher.TryStart(MakeRequest(2), texture), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult result1), Is.True);
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult result2), Is.True);

                    object service = NewService(1);
                    object coordinator = NewCoordinator(service, new CaptureFrameTraceObserver(logger));
                    Assert.That(Submit(service, NewSubmission(NewPayload(dispatcher, result1)), out _), Is.EqualTo(0));
                    Assert.That(Collect(service, out object completion1), Is.True);
                    object applied1 = Apply(coordinator, completion1);
                    png1 = Property<NativeArray<byte>>(applied1, "PngBytes");

                    Assert.That(Submit(service, NewSubmission(NewPayload(dispatcher, result2)), out _), Is.EqualTo(0));
                    Assert.That(Collect(service, out object completion2), Is.True);

                    Exception stale = ApplyException(coordinator, completion1);
                    Assert.That(stale, Is.TypeOf<InvalidOperationException>());
                    Assert.That(pool.RentedCount, Is.EqualTo(1));

                    object applied2 = Apply(coordinator, completion2);
                    png2 = Property<NativeArray<byte>>(applied2, "PngBytes");
                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                }
                finally
                {
                    if (png1.IsCreated) png1.Dispose();
                    if (png2.IsCreated) png2.Dispose();
                    AsyncGPUReadback.WaitAllRequests();
                    DestroyTexture(texture);
                }
            }
        }

        [Test]
        public void TraceFailure_OriginalExceptionAndRawReleasePreserved()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
            TraceLogger logger = new TraceLogger(8);
            RenderTexture texture = CreateTexture();
            try
            {
                Assert.That(dispatcher.TryStart(MakeRequest(1), texture), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.True);
                object service = NewService(1);
                object coordinator = NewCoordinator(service, new CaptureFrameTraceObserver(logger));
                Assert.That(Submit(service, NewSubmission(NewPayload(dispatcher, result)), out _), Is.EqualTo(0));
                Assert.That(Collect(service, out object completion), Is.True);

                logger.Dispose();
                Exception failure = ApplyException(coordinator, completion);
                Assert.That(failure, Is.TypeOf<ObjectDisposedException>());
                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
            }
            finally
            {
                AsyncGPUReadback.WaitAllRequests();
                DestroyTexture(texture);
                dispatcher.Dispose();
                pool.Dispose();
                if (logger.IsCreated) logger.Dispose();
            }
        }

        [Test]
        public void EncodeExecutionFailure_CompletionIsAppliedOnceAndOriginalExceptionPropagates()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                RenderTexture texture = CreateTexture();
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), texture), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.True);

                    bool[] rented = (bool[])typeof(CaptureFrameReadbackBufferPool)
                        .GetField("_rented", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(pool);
                    rented[result.BufferSlotIndex] = false;

                    object service = NewService(1);
                    object coordinator = NewCoordinator(service, new CaptureFrameTraceObserver(logger));
                    Assert.That(Submit(service, NewSubmission(NewPayload(dispatcher, result)), out _), Is.EqualTo(0));

                    // Restore only the pool invariant needed for the completion
                    // applier's legacy Release path. The service has already
                    // captured the original GetBuffer failure.
                    rented[result.BufferSlotIndex] = true;
                    Assert.That(Collect(service, out object completion), Is.True);
                    Assert.That(Convert.ToInt32(Property<object>(completion, "Status")), Is.EqualTo(1));

                    Exception failure = ApplyException(coordinator, completion);
                    Assert.That(failure, Is.TypeOf<InvalidOperationException>());
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                    Assert.That(pool.RentedCount, Is.EqualTo(0));

                    Exception duplicate = ApplyException(coordinator, completion);
                    Assert.That(duplicate, Is.TypeOf<InvalidOperationException>());
                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                }
                finally
                {
                    AsyncGPUReadback.WaitAllRequests();
                    DestroyTexture(texture);
                }
            }
        }

        [Test]
        public void DispatcherReleaseFailure_IsAttemptedOnceAndCompletionCannotReplay()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
            TraceLogger logger = new TraceLogger(8);
            RenderTexture texture = CreateTexture();
            CaptureFrameReadbackResult result = default;
            FieldInfo disposedField = typeof(CaptureFrameReadbackBufferPool).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(disposedField, Is.Not.Null);
            try
            {
                Assert.That(dispatcher.TryStart(MakeRequest(1), texture), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                Assert.That(dispatcher.TryCollect(out result), Is.True);
                object service = NewService(1);
                object payload = NewPayload(dispatcher, result);
                object coordinator = NewCoordinator(service, new CaptureFrameTraceObserver(logger));
                Assert.That(Submit(service, NewSubmission(payload), out _), Is.EqualTo(0));
                Assert.That(Collect(service, out object completion), Is.True);

                disposedField.SetValue(pool, true);
                Exception failure = ApplyException(coordinator, completion);
                Assert.That(failure, Is.TypeOf<ObjectDisposedException>());
                Assert.That(Property<bool>(payload, "IsReleaseAttempted"), Is.True);
                Assert.That(Property<bool>(payload, "ReleaseSucceeded"), Is.False);
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                bool[] rented = (bool[])typeof(CaptureFrameReadbackBufferPool)
                    .GetField("_rented", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(pool);
                Assert.That(rented[result.BufferSlotIndex], Is.True);

                Exception duplicate = ApplyException(coordinator, completion);
                Assert.That(duplicate, Is.TypeOf<InvalidOperationException>());
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(rented[result.BufferSlotIndex], Is.True);
            }
            finally
            {
                disposedField.SetValue(pool, false);
                if (pool.RentedCount != 0 && result.IsValid)
                {
                    pool.Return(result.BufferSlotIndex);
                }

                AsyncGPUReadback.WaitAllRequests();
                DestroyTexture(texture);
                dispatcher.Dispose();
                pool.Dispose();
                logger.Dispose();
            }
        }
    }
}
