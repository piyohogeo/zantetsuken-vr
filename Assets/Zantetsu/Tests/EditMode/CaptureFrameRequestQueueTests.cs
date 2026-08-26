using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameRequestQueueTests
    {
        private static CaptureFrameRequest MakeRequest(int arrayIndex)
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 4, 4),
                arrayIndex);
        }

        private static void AssertNoReferenceFields(Type type)
        {
            Assert.That(type.IsValueType, Is.True);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsValueType, Is.True, "Reference-type field: " + field.Name);
            }
        }

        [Test]
        public void Enums_HaveExpectedValues()
        {
            Assert.That((int)CaptureSource.None, Is.EqualTo(0));
            Assert.That((int)CaptureSource.UnityRenderTexture, Is.EqualTo(1));
            Assert.That((int)CaptureSource.OpenXRProjection, Is.EqualTo(2));

            Assert.That((int)CaptureEye.None, Is.EqualTo(0));
            Assert.That((int)CaptureEye.Left, Is.EqualTo(1));
            Assert.That((int)CaptureEye.Right, Is.EqualTo(2));

            Assert.That(Enum.GetUnderlyingType(typeof(CaptureSource)), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetUnderlyingType(typeof(CaptureEye)), Is.EqualTo(typeof(int)));
        }

        [Test]
        public void ImageRect_Valid()
        {
            CaptureImageRect rect = new CaptureImageRect(2, 3, 10, 20);

            Assert.That(rect.X, Is.EqualTo(2));
            Assert.That(rect.Y, Is.EqualTo(3));
            Assert.That(rect.Width, Is.EqualTo(10));
            Assert.That(rect.Height, Is.EqualTo(20));
            Assert.That(rect.IsValid, Is.True);
        }

        [Test]
        public void ImageRect_NegativeAndZero_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureImageRect(-1, 0, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureImageRect(0, -1, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureImageRect(0, 0, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureImageRect(0, 0, 1, 0));
        }

        [Test]
        public void ImageRect_Overflow_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureImageRect(int.MaxValue, 0, 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureImageRect(0, int.MaxValue, 1, 1));
        }

        [Test]
        public void ImageRect_Default_Invalid()
        {
            CaptureImageRect rect = default;

            Assert.That(rect.IsValid, Is.False);
        }

        [Test]
        public void Request_Valid()
        {
            CaptureFrameRequest request = MakeRequest(7);

            Assert.That(request.IsValid, Is.True);
            Assert.That(request.Source, Is.EqualTo(CaptureSource.UnityRenderTexture));
            Assert.That(request.Eye, Is.EqualTo(CaptureEye.Left));
            Assert.That(request.ArrayIndex, Is.EqualTo(7));
        }

        [Test]
        public void Request_UndefinedEnum_Rejected()
        {
            CaptureFrameTraceContext ctx = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
            CaptureImageRect rect = new CaptureImageRect(0, 0, 1, 1);

            Assert.Throws<ArgumentException>(() => new CaptureFrameRequest(ctx, (CaptureSource)999, CaptureEye.Left, rect, 0));
            Assert.Throws<ArgumentException>(() => new CaptureFrameRequest(ctx, CaptureSource.UnityRenderTexture, (CaptureEye)999, rect, 0));
        }

        [Test]
        public void Request_NoneEnum_Rejected()
        {
            CaptureFrameTraceContext ctx = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
            CaptureImageRect rect = new CaptureImageRect(0, 0, 1, 1);

            Assert.Throws<ArgumentException>(() => new CaptureFrameRequest(ctx, CaptureSource.None, CaptureEye.Left, rect, 0));
            Assert.Throws<ArgumentException>(() => new CaptureFrameRequest(ctx, CaptureSource.UnityRenderTexture, CaptureEye.None, rect, 0));
        }

        [Test]
        public void Request_InvalidRect_Rejected()
        {
            CaptureFrameTraceContext ctx = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);

            Assert.Throws<ArgumentException>(() => new CaptureFrameRequest(ctx, CaptureSource.UnityRenderTexture, CaptureEye.Left, default, 0));
        }

        [Test]
        public void Request_NegativeArrayIndex_Rejected()
        {
            CaptureFrameTraceContext ctx = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
            CaptureImageRect rect = new CaptureImageRect(0, 0, 1, 1);

            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameRequest(ctx, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, -1));
        }

        [Test]
        public void Request_Default_Invalid()
        {
            CaptureFrameRequest request = default;

            Assert.That(request.IsValid, Is.False);
        }

        [Test]
        public void Structs_HaveNoReferenceFields()
        {
            AssertNoReferenceFields(typeof(CaptureImageRect));
            AssertNoReferenceFields(typeof(CaptureFrameRequest));
        }

        [Test]
        public void Queue_FifoOrder()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(4);

            Assert.That(queue.TryEnqueue(MakeRequest(1)), Is.True);
            Assert.That(queue.TryEnqueue(MakeRequest(2)), Is.True);
            Assert.That(queue.TryEnqueue(MakeRequest(3)), Is.True);
            Assert.That(queue.Count, Is.EqualTo(3));

            Assert.That(queue.TryDequeue(out CaptureFrameRequest a), Is.True);
            Assert.That(queue.TryDequeue(out CaptureFrameRequest b), Is.True);
            Assert.That(queue.TryDequeue(out CaptureFrameRequest c), Is.True);

            Assert.That(a.ArrayIndex, Is.EqualTo(1));
            Assert.That(b.ArrayIndex, Is.EqualTo(2));
            Assert.That(c.ArrayIndex, Is.EqualTo(3));
        }

        [Test]
        public void Queue_WrapAroundOrder()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);

            queue.TryEnqueue(MakeRequest(10));
            queue.TryEnqueue(MakeRequest(20));
            Assert.That(queue.TryDequeue(out _), Is.True); // removes 10
            queue.TryEnqueue(MakeRequest(30)); // wraps to the freed slot

            Assert.That(queue.TryDequeue(out CaptureFrameRequest a), Is.True);
            Assert.That(queue.TryDequeue(out CaptureFrameRequest b), Is.True);

            Assert.That(a.ArrayIndex, Is.EqualTo(20));
            Assert.That(b.ArrayIndex, Is.EqualTo(30));
        }

        [Test]
        public void Queue_CapacityOne()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(1);

            Assert.That(queue.TryEnqueue(MakeRequest(1)), Is.True);
            Assert.That(queue.TryEnqueue(MakeRequest(2)), Is.False); // full

            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(queue.TotalAccepted, Is.EqualTo(1));
            Assert.That(queue.TotalRejected, Is.EqualTo(1));

            Assert.That(queue.TryDequeue(out CaptureFrameRequest r), Is.True);
            Assert.That(r.ArrayIndex, Is.EqualTo(1));
        }

        [Test]
        public void Queue_Full_RejectsAndKeepsExisting()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);

            queue.TryEnqueue(MakeRequest(1));
            queue.TryEnqueue(MakeRequest(2));

            Assert.That(queue.TryEnqueue(MakeRequest(3)), Is.False);

            Assert.That(queue.Count, Is.EqualTo(2));
            Assert.That(queue.TotalAccepted, Is.EqualTo(2));
            Assert.That(queue.TotalRejected, Is.EqualTo(1));

            Assert.That(queue.TryDequeue(out CaptureFrameRequest a), Is.True);
            Assert.That(queue.TryDequeue(out CaptureFrameRequest b), Is.True);
            Assert.That(a.ArrayIndex, Is.EqualTo(1));
            Assert.That(b.ArrayIndex, Is.EqualTo(2));
        }

        [Test]
        public void Queue_CountersAccurate()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);

            queue.TryEnqueue(MakeRequest(1));
            queue.TryEnqueue(MakeRequest(2));
            queue.TryEnqueue(MakeRequest(3)); // rejected
            queue.TryDequeue(out _);
            queue.TryEnqueue(MakeRequest(4)); // accepted

            Assert.That(queue.TotalAccepted, Is.EqualTo(3));
            Assert.That(queue.TotalRejected, Is.EqualTo(1));
        }

        [Test]
        public void Queue_InvalidRequest_NoCounterChange()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);

            Assert.Throws<ArgumentException>(() => queue.TryEnqueue(default));

            Assert.That(queue.Count, Is.EqualTo(0));
            Assert.That(queue.TotalAccepted, Is.EqualTo(0));
            Assert.That(queue.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void Queue_EmptyDequeue_FalseAndDefault()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);

            Assert.That(queue.TryDequeue(out CaptureFrameRequest r), Is.False);
            Assert.That(r.IsValid, Is.False);
        }

        [Test]
        public void Queue_Clear_ReusesCapacityAndKeepsCounters()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);

            queue.TryEnqueue(MakeRequest(1));
            queue.TryEnqueue(MakeRequest(2));
            queue.TryEnqueue(MakeRequest(3)); // rejected

            queue.Clear();

            Assert.That(queue.Count, Is.EqualTo(0));
            Assert.That(queue.Capacity, Is.EqualTo(2));
            Assert.That(queue.TotalAccepted, Is.EqualTo(2));
            Assert.That(queue.TotalRejected, Is.EqualTo(1));

            Assert.That(queue.TryEnqueue(MakeRequest(9)), Is.True);
            Assert.That(queue.TryDequeue(out CaptureFrameRequest r), Is.True);
            Assert.That(r.ArrayIndex, Is.EqualTo(9));
        }

        [Test]
        public void Queue_HasNoUnityObjectOrLoggerDependency()
        {
            Type type = typeof(CaptureFrameRequestQueue);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(RenderTexture)));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(Texture)));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(Camera)));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(TraceLogger)));
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(RenderTexture)));
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(TraceLogger)));
                }
            }
        }
    }
}
