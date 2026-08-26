using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngQueueFileWriterTests
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static NativeArray<byte> MakePng(int length)
        {
            NativeArray<byte> png = new NativeArray<byte>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < 8; i++)
            {
                png[i] = PngSignature[i];
            }

            for (int i = 8; i < length; i++)
            {
                png[i] = (byte)(i & 0xFF);
            }

            return png;
        }

        private static NativeArray<byte> MakeRealPng()
        {
            NativeArray<byte> rgba = new NativeArray<byte>(2 * 2 * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < rgba.Length; i++)
            {
                rgba[i] = (byte)(i * 13);
            }

            try
            {
                CaptureFramePixelLayout layout = new CaptureFramePixelLayout(CapturePixelFormat.Rgba32, 2, 2);
                return CaptureFramePngEncoder.Encode(rgba, layout);
            }
            finally
            {
                rgba.Dispose();
            }
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

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-png-writer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTempDir(string dir)
        {
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void PngSaveStatus_EnumShapeAndValues()
        {
            Type type = typeof(CaptureFramePngSaveStatus);

            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That((int)CaptureFramePngSaveStatus.None, Is.EqualTo(0));
            Assert.That((int)CaptureFramePngSaveStatus.Saved, Is.EqualTo(1));
        }

        [Test]
        public void Constructor_NullFileStore_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngQueueFileWriter(null));
        }

        [Test]
        public void NullQueue_Rejected()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());

            Assert.Throws<ArgumentNullException>(() => writer.TrySaveNext(null, "dest.png"));
        }

        [Test]
        public void DisposedQueue_Rejected()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            try
            {
                queue.Dispose();

                Assert.Throws<ObjectDisposedException>(() => writer.TrySaveNext(queue, "dest.png"));
            }
            finally
            {
                queue.Dispose();
            }
        }

        [Test]
        public void EmptyQueue_None_NoFile_QueueUnchanged()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            string dir = null;
            try
            {
                dir = CreateTempDir();

                Assert.That(writer.TrySaveNext(queue, null), Is.EqualTo(CaptureFramePngSaveStatus.None));
                Assert.That(writer.TrySaveNext(queue, "relative-invalid.png"), Is.EqualTo(CaptureFramePngSaveStatus.None));

                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.TotalAccepted, Is.EqualTo(0));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
                Assert.That(Directory.GetFileSystemEntries(dir), Is.Empty);
            }
            finally
            {
                try
                {
                    queue.Dispose();
                }
                finally
                {
                    if (dir != null)
                    {
                        DeleteTempDir(dir);
                    }
                }
            }
        }

        [Test]
        public void SaveSuccess_Saved_BytesMatch()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            NativeArray<byte> snapshot = default;
            bool enqueued = false;
            string dir = null;
            try
            {
                png = MakePng(100);
                snapshot = new NativeArray<byte>(png.Length, Allocator.Persistent);
                for (int i = 0; i < png.Length; i++)
                {
                    snapshot[i] = png[i];
                }

                enqueued = queue.TryEnqueue(MakeRequest(7), png);
                if (enqueued)
                {
                    png = default;
                }

                Assert.That(enqueued, Is.True);

                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.png");
                Assert.That(writer.TrySaveNext(queue, dest), Is.EqualTo(CaptureFramePngSaveStatus.Saved));

                Assert.That(File.Exists(dest), Is.True);
                byte[] actual = File.ReadAllBytes(dest);
                Assert.That(actual.Length, Is.EqualTo(snapshot.Length));
                for (int i = 0; i < snapshot.Length; i++)
                {
                    Assert.That(actual[i], Is.EqualTo(snapshot[i]), "Byte mismatch at index " + i);
                }

                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
                Assert.That(Directory.GetFiles(dir, "*.tmp"), Is.Empty);
            }
            finally
            {
                try
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }

                    if (snapshot.IsCreated)
                    {
                        snapshot.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        queue.Dispose();
                    }
                    finally
                    {
                        if (dir != null)
                        {
                            DeleteTempDir(dir);
                        }
                    }
                }
            }
        }

        [Test]
        public void SaveSuccess_RealPng()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            NativeArray<byte> snapshot = default;
            bool enqueued = false;
            string dir = null;
            try
            {
                png = MakeRealPng();
                snapshot = new NativeArray<byte>(png.Length, Allocator.Persistent);
                for (int i = 0; i < png.Length; i++)
                {
                    snapshot[i] = png[i];
                }

                enqueued = queue.TryEnqueue(MakeRequest(1), png);
                if (enqueued)
                {
                    png = default;
                }

                Assert.That(enqueued, Is.True);

                dir = CreateTempDir();
                string dest = Path.Combine(dir, "real.png");
                Assert.That(writer.TrySaveNext(queue, dest), Is.EqualTo(CaptureFramePngSaveStatus.Saved));

                byte[] actual = File.ReadAllBytes(dest);
                Assert.That(actual.Length, Is.EqualTo(snapshot.Length));
                for (int i = 0; i < snapshot.Length; i++)
                {
                    Assert.That(actual[i], Is.EqualTo(snapshot[i]), "Byte mismatch at index " + i);
                }
            }
            finally
            {
                try
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }

                    if (snapshot.IsCreated)
                    {
                        snapshot.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        queue.Dispose();
                    }
                    finally
                    {
                        if (dir != null)
                        {
                            DeleteTempDir(dir);
                        }
                    }
                }
            }
        }

        [Test]
        public void TwoItems_FifoOrder()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png1 = default;
            NativeArray<byte> png2 = default;
            bool enqueued1 = false;
            bool enqueued2 = false;
            string dir = null;
            try
            {
                png1 = MakePng(100);
                png2 = MakePng(120);

                enqueued1 = queue.TryEnqueue(MakeRequest(100), png1);
                if (enqueued1)
                {
                    png1 = default;
                }

                enqueued2 = queue.TryEnqueue(MakeRequest(200), png2);
                if (enqueued2)
                {
                    png2 = default;
                }

                Assert.That(enqueued1, Is.True);
                Assert.That(enqueued2, Is.True);

                dir = CreateTempDir();
                string dest1 = Path.Combine(dir, "one.png");
                string dest2 = Path.Combine(dir, "two.png");

                Assert.That(writer.TrySaveNext(queue, dest1), Is.EqualTo(CaptureFramePngSaveStatus.Saved));
                Assert.That(queue.Count, Is.EqualTo(1));

                Assert.That(writer.TrySaveNext(queue, dest2), Is.EqualTo(CaptureFramePngSaveStatus.Saved));
                Assert.That(queue.Count, Is.EqualTo(0));

                byte[] b1 = File.ReadAllBytes(dest1);
                byte[] b2 = File.ReadAllBytes(dest2);
                Assert.That(b1.Length, Is.EqualTo(100));
                Assert.That(b2.Length, Is.EqualTo(120));
            }
            finally
            {
                try
                {
                    if (png1.IsCreated)
                    {
                        png1.Dispose();
                    }

                    if (png2.IsCreated)
                    {
                        png2.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        queue.Dispose();
                    }
                    finally
                    {
                        if (dir != null)
                        {
                            DeleteTempDir(dir);
                        }
                    }
                }
            }
        }

        [Test]
        public void DestinationExisting_IOException_RetrySucceeds()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            NativeArray<byte> snapshot = default;
            bool enqueued = false;
            string dir = null;
            try
            {
                png = MakePng(32);
                snapshot = new NativeArray<byte>(png.Length, Allocator.Persistent);
                for (int i = 0; i < png.Length; i++)
                {
                    snapshot[i] = png[i];
                }

                enqueued = queue.TryEnqueue(MakeRequest(5), png);
                if (enqueued)
                {
                    png = default;
                }

                Assert.That(enqueued, Is.True);

                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.png");
                File.WriteAllBytes(dest, new byte[] { 1, 2, 3, 4 });

                Assert.Throws<IOException>(() => writer.TrySaveNext(queue, dest));
                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(File.ReadAllBytes(dest), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));

                string alt = Path.Combine(dir, "alt.png");
                Assert.That(writer.TrySaveNext(queue, alt), Is.EqualTo(CaptureFramePngSaveStatus.Saved));
                Assert.That(queue.Count, Is.EqualTo(0));

                byte[] actual = File.ReadAllBytes(alt);
                Assert.That(actual.Length, Is.EqualTo(snapshot.Length));
                for (int i = 0; i < snapshot.Length; i++)
                {
                    Assert.That(actual[i], Is.EqualTo(snapshot[i]), "Byte mismatch at index " + i);
                }
            }
            finally
            {
                try
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }

                    if (snapshot.IsCreated)
                    {
                        snapshot.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        queue.Dispose();
                    }
                    finally
                    {
                        if (dir != null)
                        {
                            DeleteTempDir(dir);
                        }
                    }
                }
            }
        }

        [Test]
        public void ParentMissing_DirectoryNotFound_RetrySucceeds()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            bool enqueued = false;
            string dir = null;
            try
            {
                png = MakePng(32);

                enqueued = queue.TryEnqueue(MakeRequest(5), png);
                if (enqueued)
                {
                    png = default;
                }

                Assert.That(enqueued, Is.True);

                dir = CreateTempDir();
                string missing = Path.Combine(dir, "missing");

                Assert.Throws<DirectoryNotFoundException>(() => writer.TrySaveNext(queue, Path.Combine(missing, "out.png")));
                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(Directory.Exists(missing), Is.False);

                Assert.That(writer.TrySaveNext(queue, Path.Combine(dir, "alt.png")), Is.EqualTo(CaptureFramePngSaveStatus.Saved));
                Assert.That(queue.Count, Is.EqualTo(0));
            }
            finally
            {
                try
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        queue.Dispose();
                    }
                    finally
                    {
                        if (dir != null)
                        {
                            DeleteTempDir(dir);
                        }
                    }
                }
            }
        }

        [Test]
        public void InvalidPath_KeepsHead_ThenSuccess()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            bool enqueued = false;
            string dir = null;
            try
            {
                png = MakePng(32);

                enqueued = queue.TryEnqueue(MakeRequest(5), png);
                if (enqueued)
                {
                    png = default;
                }

                Assert.That(enqueued, Is.True);

                dir = CreateTempDir();

                Assert.Throws<ArgumentException>(() => writer.TrySaveNext(queue, Path.Combine(dir, "out.txt")));
                Assert.That(queue.Count, Is.EqualTo(1));

                Assert.Throws<ArgumentException>(() => writer.TrySaveNext(queue, "relative.png"));
                Assert.That(queue.Count, Is.EqualTo(1));

                Assert.That(writer.TrySaveNext(queue, Path.Combine(dir, "alt.png")), Is.EqualTo(CaptureFramePngSaveStatus.Saved));
                Assert.That(queue.Count, Is.EqualTo(0));
            }
            finally
            {
                try
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        queue.Dispose();
                    }
                    finally
                    {
                        if (dir != null)
                        {
                            DeleteTempDir(dir);
                        }
                    }
                }
            }
        }

        [Test]
        public void SaveFailure_ThenQueueDispose_DisposesHeldPng()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            bool enqueued = false;
            string dir = null;
            try
            {
                png = MakePng(32);

                enqueued = queue.TryEnqueue(MakeRequest(5), png);
                if (enqueued)
                {
                    png = default;
                }

                Assert.That(enqueued, Is.True);

                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.png");
                File.WriteAllBytes(dest, new byte[] { 1, 2, 3 });

                Assert.Throws<IOException>(() => writer.TrySaveNext(queue, dest));
                Assert.That(queue.Count, Is.EqualTo(1));

                Assert.DoesNotThrow(() => queue.Dispose());
            }
            finally
            {
                try
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        queue.Dispose();
                    }
                    finally
                    {
                        if (dir != null)
                        {
                            DeleteTempDir(dir);
                        }
                    }
                }
            }
        }

        [Test]
        public void SaveSuccess_ThenQueueDispose_NoDoubleDispose()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            bool enqueued = false;
            string dir = null;
            try
            {
                png = MakePng(32);

                enqueued = queue.TryEnqueue(MakeRequest(5), png);
                if (enqueued)
                {
                    png = default;
                }

                Assert.That(enqueued, Is.True);

                dir = CreateTempDir();
                Assert.That(writer.TrySaveNext(queue, Path.Combine(dir, "out.png")), Is.EqualTo(CaptureFramePngSaveStatus.Saved));
                Assert.That(queue.Count, Is.EqualTo(0));

                Assert.DoesNotThrow(() => queue.Dispose());
            }
            finally
            {
                try
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        queue.Dispose();
                    }
                    finally
                    {
                        if (dir != null)
                        {
                            DeleteTempDir(dir);
                        }
                    }
                }
            }
        }

        [Test]
        public void EmptyAfterSave_ReturnsNone()
        {
            CaptureFramePngQueueFileWriter writer = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            bool enqueued = false;
            string dir = null;
            try
            {
                png = MakePng(32);

                enqueued = queue.TryEnqueue(MakeRequest(5), png);
                if (enqueued)
                {
                    png = default;
                }

                Assert.That(enqueued, Is.True);

                dir = CreateTempDir();
                Assert.That(writer.TrySaveNext(queue, Path.Combine(dir, "out.png")), Is.EqualTo(CaptureFramePngSaveStatus.Saved));
                Assert.That(writer.TrySaveNext(queue, Path.Combine(dir, "next.png")), Is.EqualTo(CaptureFramePngSaveStatus.None));
            }
            finally
            {
                try
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        queue.Dispose();
                    }
                    finally
                    {
                        if (dir != null)
                        {
                            DeleteTempDir(dir);
                        }
                    }
                }
            }
        }

        [Test]
        public void TryPeek_DoesNotChangeQueueState()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            bool enqueued = false;
            try
            {
                png = MakePng(16);

                enqueued = queue.TryEnqueue(MakeRequest(1), png);
                if (enqueued)
                {
                    png = default;
                }

                Assert.That(enqueued, Is.True);

                MethodInfo tryPeek = typeof(CaptureFramePngQueue).GetMethod("TryPeek", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(tryPeek, Is.Not.Null);

                object[] args = new object[] { null, null };
                bool result = (bool)tryPeek.Invoke(queue, args);

                Assert.That(result, Is.True);
                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
            }
            finally
            {
                try
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }
                }
                finally
                {
                    queue.Dispose();
                }
            }
        }
    }
}
