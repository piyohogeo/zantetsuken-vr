using System;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePixelLayoutTests
    {
        private static CaptureFrameRequest MakeRequest()
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 4, 4),
                0,
                CapturePixelFormat.Rgba32);
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
        public void PixelFormat_EnumShapeAndValues()
        {
            Type type = typeof(CapturePixelFormat);

            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetName(type, 0), Is.EqualTo(nameof(CapturePixelFormat.None)));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo(nameof(CapturePixelFormat.Rgba32)));
            Assert.That((int)CapturePixelFormat.None, Is.EqualTo(0));
            Assert.That((int)CapturePixelFormat.Rgba32, Is.EqualTo(1));
        }

        [Test]
        public void Layout_Rgba32_1x1()
        {
            CaptureFramePixelLayout layout = new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, 1, 1);

            Assert.That(layout.Format, Is.EqualTo(CapturePixelFormat.Rgba32));
            Assert.That(layout.Width, Is.EqualTo(1));
            Assert.That(layout.Height, Is.EqualTo(1));
            Assert.That(layout.BytesPerPixel, Is.EqualTo(4));
            Assert.That(layout.RowStrideBytes, Is.EqualTo(4));
            Assert.That(layout.ByteCount, Is.EqualTo(4));
            Assert.That(layout.IsValid, Is.True);
        }

        [Test]
        public void Layout_Rgba32_Arbitrary()
        {
            CaptureFramePixelLayout layout = new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, 1920, 1080);

            Assert.That(layout.BytesPerPixel, Is.EqualTo(4));
            Assert.That(layout.RowStrideBytes, Is.EqualTo(7680));
            Assert.That(layout.ByteCount, Is.EqualTo(8294400));
            Assert.That(layout.IsValid, Is.True);
        }

        [Test]
        public void Layout_NearMaximum()
        {
            CaptureFramePixelLayout layout = new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, 16384, 32767);

            Assert.That(layout.RowStrideBytes, Is.EqualTo(65536));
            Assert.That(layout.ByteCount, Is.EqualTo(2147418112));
            Assert.That(layout.IsValid, Is.True);
        }

        [Test]
        public void Layout_WidthHeight_ZeroOrNegative_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, 1, -1));
        }

        [Test]
        public void Layout_NoneOrUndefinedFormat_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFramePixelLayout(CapturePixelFormat.None, 1, 1));
            Assert.Throws<ArgumentException>(() => new CaptureFramePixelLayout((CapturePixelFormat)999, 1, 1));
        }

        [Test]
        public void Layout_RowStrideOverflow_Rejected()
        {
            int width = (int.MaxValue / 4) + 1;

            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, width, 1));
        }

        [Test]
        public void Layout_TotalByteCountOverflow_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, 1, int.MaxValue));
        }

        [Test]
        public void Layout_Default_Invalid()
        {
            CaptureFramePixelLayout layout = default;

            Assert.That(layout.IsValid, Is.False);
        }

        [Test]
        public void Layout_HasNoReferenceFields()
        {
            AssertNoReferenceFields(typeof(CaptureFramePixelLayout));
        }

        [Test]
        public void Request_PixelLayoutMatchesImageRect()
        {
            CaptureFrameRequest request = new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(2, 3, 10, 20),
                0,
                CapturePixelFormat.Rgba32);

            Assert.That(request.PixelLayout.Width, Is.EqualTo(10));
            Assert.That(request.PixelLayout.Height, Is.EqualTo(20));
            Assert.That(request.ImageRect.Width, Is.EqualTo(10));
            Assert.That(request.ImageRect.Height, Is.EqualTo(20));
            Assert.That(request.PixelLayout.Format, Is.EqualTo(CapturePixelFormat.Rgba32));
        }

        [Test]
        public void Request_RequiredByteCount_Correct()
        {
            CaptureFrameRequest request = MakeRequest(); // 4x4 Rgba32

            Assert.That(request.PixelLayout.ByteCount, Is.EqualTo(64));
            Assert.That(request.RequiredByteCount, Is.EqualTo(64));
            Assert.That(request.IsValid, Is.True);
        }

        [Test]
        public void Request_NoneOrUndefinedFormat_Rejected()
        {
            CaptureFrameTraceContext ctx = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
            CaptureImageRect rect = new CaptureImageRect(0, 0, 1, 1);

            Assert.Throws<ArgumentException>(() => new CaptureFrameRequest(ctx, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.None));
            Assert.Throws<ArgumentException>(() => new CaptureFrameRequest(ctx, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, (CapturePixelFormat)999));
        }

        [Test]
        public void Request_ByteOverflow_Rejected()
        {
            CaptureFrameTraceContext ctx = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
            CaptureImageRect rect = new CaptureImageRect(0, 0, (int.MaxValue / 4) + 1, 1);

            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameRequest(ctx, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
        }

        [Test]
        public void Request_Default_Invalid()
        {
            CaptureFrameRequest request = default;

            Assert.That(request.IsValid, Is.False);
        }

        [Test]
        public void Request_HasNoReferenceFields()
        {
            AssertNoReferenceFields(typeof(CaptureFrameRequest));
        }

        [Test]
        public void RequiredByteCount_ComparableAgainstPoolBytesPerSlot()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            {
                CaptureFrameRequest request = MakeRequest();

                Assert.That(pool.BytesPerSlot, Is.GreaterThanOrEqualTo(request.RequiredByteCount));
            }

            using (CaptureFrameReadbackBufferPool smallPool = new CaptureFrameReadbackBufferPool(1, 32))
            {
                CaptureFrameRequest request = MakeRequest();

                Assert.That(smallPool.BytesPerSlot, Is.LessThan(request.RequiredByteCount));
            }
        }
    }
}
