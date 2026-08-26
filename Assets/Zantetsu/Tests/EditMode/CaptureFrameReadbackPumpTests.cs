using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameReadbackPumpTests
    {
        private static CaptureFrameRequest MakeRequest(long captureFrameId)
        {
            return MakeRequest(2, 2, captureFrameId);
        }

        private static CaptureFrameRequest MakeRequest(int width, int height, long captureFrameId)
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, 2, 3, 4, captureFrameId, 6, 7, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, width, height),
                0,
                CapturePixelFormat.Rgba32);
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

        [Test]
        public void Pump_NullDependencies_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);

                Assert.Throws<ArgumentNullException>(() => new CaptureFrameReadbackPump(null, dispatcher));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameReadbackPump(queue, null));
            }
        }

        [Test]
        public void Pump_EmptyQueue_NullSource_ReturnsFalse()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameReadbackPump pump = new CaptureFrameReadbackPump(queue, dispatcher);

                Assert.That(pump.TryStartNext(null), Is.False);
                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(pump.PendingCount, Is.EqualTo(0));
                Assert.That(pump.ActiveReadbackCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Pump_StartsReadback_DequeuesHead()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameReadbackPump pump = new CaptureFrameReadbackPump(queue, dispatcher);
                queue.TryEnqueue(MakeRequest(7));

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(pump.PendingCount, Is.EqualTo(1));
                    Assert.That(pump.ActiveReadbackCount, Is.EqualTo(0));

                    Assert.That(pump.TryStartNext(rt), Is.True);

                    Assert.That(queue.Count, Is.EqualTo(0));
                    Assert.That(pump.PendingCount, Is.EqualTo(0));
                    Assert.That(pump.ActiveReadbackCount, Is.EqualTo(1));

                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r), Is.True);
                    Assert.That(r.FrameRequest.TraceContext.CaptureFrameId, Is.EqualTo(7));
                    dispatcher.Release(r);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Pump_FifoOrder_TwoRequests()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameReadbackPump pump = new CaptureFrameReadbackPump(queue, dispatcher);
                queue.TryEnqueue(MakeRequest(100));
                queue.TryEnqueue(MakeRequest(200));

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(pump.TryStartNext(rt), Is.True);
                    Assert.That(pump.TryStartNext(rt), Is.True);
                    Assert.That(queue.Count, Is.EqualTo(0));
                    Assert.That(pump.ActiveReadbackCount, Is.EqualTo(2));

                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r1), Is.True);
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r2), Is.True);
                    Assert.That(r1.FrameRequest.TraceContext.CaptureFrameId, Is.EqualTo(100));
                    Assert.That(r2.FrameRequest.TraceContext.CaptureFrameId, Is.EqualTo(200));

                    dispatcher.Release(r1);
                    dispatcher.Release(r2);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Pump_PoolExhausted_KeepsHead()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameReadbackPump pump = new CaptureFrameReadbackPump(queue, dispatcher);
                queue.TryEnqueue(MakeRequest(100));
                queue.TryEnqueue(MakeRequest(200));

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(pump.TryStartNext(rt), Is.True);
                    Assert.That(pump.TryStartNext(rt), Is.False);

                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(pump.PendingCount, Is.EqualTo(1));
                    Assert.That(queue.TryPeek(out CaptureFrameRequest head), Is.True);
                    Assert.That(head.TraceContext.CaptureFrameId, Is.EqualTo(200));

                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r), Is.True);
                    Assert.That(r.FrameRequest.TraceContext.CaptureFrameId, Is.EqualTo(100));
                    dispatcher.Release(r);

                    Assert.That(pump.TryStartNext(rt), Is.True);
                    Assert.That(queue.Count, Is.EqualTo(0));
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r2), Is.True);
                    Assert.That(r2.FrameRequest.TraceContext.CaptureFrameId, Is.EqualTo(200));
                    dispatcher.Release(r2);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Pump_NullSource_QueueUnchanged()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameReadbackPump pump = new CaptureFrameReadbackPump(queue, dispatcher);
                queue.TryEnqueue(MakeRequest(7));

                Assert.Throws<ArgumentNullException>(() => pump.TryStartNext(null));

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(pump.PendingCount, Is.EqualTo(1));
                Assert.That(pump.ActiveReadbackCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Pump_BoundsMismatch_QueueUnchanged()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameReadbackPump pump = new CaptureFrameReadbackPump(queue, dispatcher);
                queue.TryEnqueue(MakeRequest(4, 4, 7));

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => pump.TryStartNext(rt));

                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(pump.PendingCount, Is.EqualTo(1));
                    Assert.That(pump.ActiveReadbackCount, Is.EqualTo(0));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Pump_DispatcherDisposed_QueueUnchanged()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
            try
            {
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                dispatcher.Dispose();

                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameReadbackPump pump = new CaptureFrameReadbackPump(queue, dispatcher);
                queue.TryEnqueue(MakeRequest(7));

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.Throws<ObjectDisposedException>(() => pump.TryStartNext(rt));
                    Assert.That(queue.Count, Is.EqualTo(1));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
            finally
            {
                pool.Dispose();
            }
        }

        [Test]
        public void Pump_OwnerCanContinueUsingQueueAndDispatcher()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameReadbackPump pump = new CaptureFrameReadbackPump(queue, dispatcher);
                queue.TryEnqueue(MakeRequest(7));

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(pump.TryStartNext(rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r), Is.True);
                    dispatcher.Release(r);

                    queue.TryEnqueue(MakeRequest(99));
                    Assert.That(queue.TryDequeue(out CaptureFrameRequest d), Is.True);
                    Assert.That(d.TraceContext.CaptureFrameId, Is.EqualTo(99));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Pump_HasNoTraceLoggerDependency()
        {
            Type type = typeof(CaptureFrameReadbackPump);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(TraceLogger)), "Field references TraceLogger: " + field.Name);
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(TraceLogger)), "Method returns TraceLogger: " + method.Name);
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(TraceLogger)), "Method parameter references TraceLogger: " + method.Name + "." + parameter.Name);
                }
            }

            foreach (ConstructorInfo ctor in type.GetConstructors())
            {
                foreach (ParameterInfo parameter in ctor.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(TraceLogger)), "Constructor parameter references TraceLogger: " + parameter.Name);
                }
            }
        }
    }
}
