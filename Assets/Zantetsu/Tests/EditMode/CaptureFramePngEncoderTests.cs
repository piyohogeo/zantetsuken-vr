using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngEncoderTests
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static NativeArray<byte> MakeRgba(int width, int height)
        {
            NativeArray<byte> data = new NativeArray<byte>(width * height * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)((i * 37) & 0xFF);
            }

            return data;
        }

        private static CaptureFramePixelLayout MakeLayout(int width, int height)
        {
            return new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, width, height);
        }

        private static uint ReadUInt32BE(NativeArray<byte> png, int offset)
        {
            return ((uint)png[offset] << 24) | ((uint)png[offset + 1] << 16) | ((uint)png[offset + 2] << 8) | (uint)png[offset + 3];
        }

        private static void AssertPngSignature(NativeArray<byte> png)
        {
            Assert.That(png.Length, Is.GreaterThan(8));
            for (int i = 0; i < 8; i++)
            {
                Assert.That(png[i], Is.EqualTo(PngSignature[i]), "PNG signature mismatch at byte " + i);
            }
        }

        private static void AssertIhdrDimensions(NativeArray<byte> png, int width, int height)
        {
            Assert.That(png.Length, Is.GreaterThan(24));
            Assert.That(png[12], Is.EqualTo((byte)'I'));
            Assert.That(png[13], Is.EqualTo((byte)'H'));
            Assert.That(png[14], Is.EqualTo((byte)'D'));
            Assert.That(png[15], Is.EqualTo((byte)'R'));

            Assert.That(ReadUInt32BE(png, 16), Is.EqualTo((uint)width));
            Assert.That(ReadUInt32BE(png, 20), Is.EqualTo((uint)height));
        }

        [Test]
        public void Encode_DefaultLayout_Rejected()
        {
            NativeArray<byte> input = MakeRgba(1, 1);
            try
            {
                Assert.Throws<ArgumentException>(() => CaptureFramePngEncoder.Encode(input, default));
            }
            finally
            {
                input.Dispose();
            }
        }

        [Test]
        public void Encode_UncreatedInput_Rejected()
        {
            NativeArray<byte> uncreated = default;
            CaptureFramePixelLayout layout = MakeLayout(1, 1);

            Assert.Throws<ArgumentException>(() => CaptureFramePngEncoder.Encode(uncreated, layout));
        }

        [Test]
        public void Encode_LengthMismatch_Rejected()
        {
            CaptureFramePixelLayout layout = MakeLayout(2, 2); // 16 bytes

            NativeArray<byte> shortInput = new NativeArray<byte>(15, Allocator.Persistent);
            NativeArray<byte> longInput = new NativeArray<byte>(17, Allocator.Persistent);
            try
            {
                Assert.Throws<ArgumentException>(() => CaptureFramePngEncoder.Encode(shortInput, layout));
                Assert.Throws<ArgumentException>(() => CaptureFramePngEncoder.Encode(longInput, layout));
            }
            finally
            {
                shortInput.Dispose();
                longInput.Dispose();
            }
        }

        [Test]
        public void Encode_1x1_Succeeds()
        {
            NativeArray<byte> input = MakeRgba(1, 1);
            CaptureFramePixelLayout layout = MakeLayout(1, 1);
            NativeArray<byte> png = CaptureFramePngEncoder.Encode(input, layout);
            try
            {
                Assert.That(png.IsCreated, Is.True);
                Assert.That(png.Length, Is.GreaterThan(0));
                AssertPngSignature(png);
                AssertIhdrDimensions(png, 1, 1);
            }
            finally
            {
                png.Dispose();
                input.Dispose();
            }
        }

        [Test]
        public void Encode_2x2_Succeeds()
        {
            NativeArray<byte> input = MakeRgba(2, 2);
            CaptureFramePixelLayout layout = MakeLayout(2, 2);
            NativeArray<byte> png = CaptureFramePngEncoder.Encode(input, layout);
            try
            {
                Assert.That(png.IsCreated, Is.True);
                Assert.That(png.Length, Is.GreaterThan(0));
                AssertPngSignature(png);
                AssertIhdrDimensions(png, 2, 2);
            }
            finally
            {
                png.Dispose();
                input.Dispose();
            }
        }

        [Test]
        public void Encode_Arbitrary_Succeeds()
        {
            const int width = 7;
            const int height = 5;

            NativeArray<byte> input = MakeRgba(width, height);
            CaptureFramePixelLayout layout = MakeLayout(width, height);
            NativeArray<byte> png = CaptureFramePngEncoder.Encode(input, layout);
            try
            {
                Assert.That(png.IsCreated, Is.True);
                Assert.That(png.Length, Is.GreaterThan(0));
                AssertPngSignature(png);
                AssertIhdrDimensions(png, width, height);
            }
            finally
            {
                png.Dispose();
                input.Dispose();
            }
        }

        [Test]
        public void Encode_SameInputTwice_Identical()
        {
            NativeArray<byte> input = MakeRgba(2, 2);
            CaptureFramePixelLayout layout = MakeLayout(2, 2);

            NativeArray<byte> png1 = CaptureFramePngEncoder.Encode(input, layout);
            NativeArray<byte> png2 = CaptureFramePngEncoder.Encode(input, layout);
            try
            {
                Assert.That(png1.Length, Is.EqualTo(png2.Length));
                for (int i = 0; i < png1.Length; i++)
                {
                    Assert.That(png1[i], Is.EqualTo(png2[i]), "PNG bytes differ at index " + i);
                }
            }
            finally
            {
                png1.Dispose();
                png2.Dispose();
                input.Dispose();
            }
        }

        [Test]
        public void Encode_OnePixelChange_Differs()
        {
            NativeArray<byte> input1 = MakeRgba(2, 2);
            NativeArray<byte> input2 = MakeRgba(2, 2);
            input2[0] = (byte)(input1[0] ^ 0xFF);

            CaptureFramePixelLayout layout = MakeLayout(2, 2);

            NativeArray<byte> png1 = CaptureFramePngEncoder.Encode(input1, layout);
            NativeArray<byte> png2 = CaptureFramePngEncoder.Encode(input2, layout);
            try
            {
                bool different = false;
                for (int i = 0; i < png1.Length && i < png2.Length; i++)
                {
                    if (png1[i] != png2[i])
                    {
                        different = true;
                        break;
                    }
                }

                Assert.That(different, Is.True, "PNG output did not change after modifying a pixel.");
            }
            finally
            {
                png1.Dispose();
                png2.Dispose();
                input1.Dispose();
                input2.Dispose();
            }
        }

        [Test]
        public void Encode_InputUnchanged()
        {
            NativeArray<byte> input = MakeRgba(2, 2);
            NativeArray<byte> snapshot = new NativeArray<byte>(input.Length, Allocator.Persistent);
            for (int i = 0; i < input.Length; i++)
            {
                snapshot[i] = input[i];
            }

            CaptureFramePixelLayout layout = MakeLayout(2, 2);
            NativeArray<byte> png = CaptureFramePngEncoder.Encode(input, layout);
            try
            {
                for (int i = 0; i < input.Length; i++)
                {
                    Assert.That(input[i], Is.EqualTo(snapshot[i]), "Input changed at index " + i);
                }
            }
            finally
            {
                png.Dispose();
                snapshot.Dispose();
                input.Dispose();
            }
        }

        [Test]
        public void Encode_InputUsableAfterOutputDisposed()
        {
            NativeArray<byte> input = MakeRgba(2, 2);
            CaptureFramePixelLayout layout = MakeLayout(2, 2);

            NativeArray<byte> png = CaptureFramePngEncoder.Encode(input, layout);
            png.Dispose();

            NativeArray<byte> png2 = CaptureFramePngEncoder.Encode(input, layout);
            try
            {
                Assert.That(png2.IsCreated, Is.True);
                Assert.That(png2.Length, Is.GreaterThan(0));
            }
            finally
            {
                png2.Dispose();
                input.Dispose();
            }
        }

        [Test]
        public void Encode_CreatesNoFileOrDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-png-encoder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                NativeArray<byte> input = MakeRgba(1, 1);
                CaptureFramePixelLayout layout = MakeLayout(1, 1);
                NativeArray<byte> png = CaptureFramePngEncoder.Encode(input, layout);
                png.Dispose();
                input.Dispose();

                Assert.That(Directory.GetFileSystemEntries(dir), Is.Empty);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void Encoder_PublicApi_HasNoFileOrTextureDependency()
        {
            Type type = typeof(CaptureFramePngEncoder);

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(Texture2D)), "Method returns Texture2D: " + method.Name);

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(string)), "Method parameter is a string (file path): " + method.Name);
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(Texture2D)), "Method parameter is Texture2D: " + method.Name);
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(TraceLogger)), "Method parameter is TraceLogger: " + method.Name);
                }
            }
        }

        [Test]
        public void Encode_ReadbackIntegration()
        {
            const int width = 8;
            const int height = 4;
            const int bytes = width * height * 4;

            byte[] pixels = new byte[bytes];
            for (int i = 0; i < width * height; i++)
            {
                pixels[i * 4 + 0] = 10;
                pixels[i * 4 + 1] = 20;
                pixels[i * 4 + 2] = 30;
                pixels[i * 4 + 3] = 40;
            }

            Texture2D sourceTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            RenderTexture rt = null;
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, bytes);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);

            NativeArray<byte> png = default;
            CaptureFrameReadbackResult result = default;

            try
            {
                sourceTex.SetPixelData(pixels, 0);
                sourceTex.Apply(false, false);

                rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                rt.Create();
                Graphics.CopyTexture(sourceTex, rt);

                CaptureFrameRequest request = MakeRequest(width, height);
                Assert.That(dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                Assert.That(router.TryCollect(out result), Is.EqualTo(CaptureFrameReadbackCollectStatus.Succeeded));

                NativeArray<byte> buffer = dispatcher.GetBuffer(result);
                png = CaptureFramePngEncoder.Encode(buffer, request.PixelLayout);

                Assert.That(png.IsCreated, Is.True);
                Assert.That(png.Length, Is.GreaterThan(0));
                AssertPngSignature(png);
                AssertIhdrDimensions(png, width, height);
            }
            finally
            {
                if (png.IsCreated)
                {
                    png.Dispose();
                }

                if (result.IsValid)
                {
                    dispatcher.Release(result);
                }

                dispatcher.Dispose();
                logger.Dispose();
                pool.Dispose();

                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }

                UnityEngine.Object.DestroyImmediate(sourceTex);
            }
        }

        private static CaptureFrameRequest MakeRequest(int width, int height)
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, width, height),
                0,
                CapturePixelFormat.Rgba32);
        }
    }
}
