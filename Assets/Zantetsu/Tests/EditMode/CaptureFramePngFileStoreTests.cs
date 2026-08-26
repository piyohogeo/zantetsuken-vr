using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngFileStoreTests
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

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-png-store-" + Guid.NewGuid().ToString("N"));
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
        public void Constructor_ZeroOrNegative_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePngFileStore(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePngFileStore(-1));
        }

        [Test]
        public void CopyBufferSize_ReturnsSpecified()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore(1024);

            Assert.That(store.CopyBufferSize, Is.EqualTo(1024));
        }

        [Test]
        public void NullOrEmptyOrWhitespacePath_Rejected()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakePng(32);
            try
            {
                Assert.Throws<ArgumentNullException>(() => store.SaveAtomic(null, png));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(string.Empty, png));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic("   ", png));
            }
            finally
            {
                png.Dispose();
            }
        }

        [Test]
        public void RelativeOrRootedPath_Rejected()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakePng(32);
            try
            {
                Assert.Throws<ArgumentException>(() => store.SaveAtomic("relative.png", png));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(@"C:drive-relative.png", png));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(@"\current-drive-rooted.png", png));
            }
            finally
            {
                png.Dispose();
            }
        }

        [Test]
        public void NonPngExtension_Rejected()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakePng(32);
            string dir = CreateTempDir();
            try
            {
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(Path.Combine(dir, "file.txt"), png));
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngUppercaseExtension_Accepted()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakePng(32);
            string dir = CreateTempDir();
            try
            {
                string dest = Path.Combine(dir, "out.PNG");
                store.SaveAtomic(dest, png);

                Assert.That(File.Exists(dest), Is.True);
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void UncreatedOrEmptyPng_Rejected()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> empty = new NativeArray<byte>(0, Allocator.Persistent);
            string dir = CreateTempDir();
            try
            {
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(Path.Combine(dir, "out.png"), default));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(Path.Combine(dir, "out.png"), empty));
            }
            finally
            {
                empty.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void TooShortOrBadSignaturePng_Rejected()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> shortPng = default;
            NativeArray<byte> badSignature = default;

            string dir = CreateTempDir();
            try
            {
                shortPng = MakePng(8);
                badSignature = MakePng(32);
                badSignature[0] = 0x00;

                Assert.Throws<ArgumentException>(() => store.SaveAtomic(Path.Combine(dir, "out.png"), shortPng));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(Path.Combine(dir, "out.png"), badSignature));
            }
            finally
            {
                if (shortPng.IsCreated) { shortPng.Dispose(); }
                if (badSignature.IsCreated) { badSignature.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void ValidationFailure_CreatesNothing()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> badSignature = MakePng(32);
            badSignature[0] = 0x00;

            string dir = CreateTempDir();
            try
            {
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(Path.Combine(dir, "out.png"), badSignature));
                Assert.That(Directory.GetFileSystemEntries(dir), Is.Empty);
            }
            finally
            {
                badSignature.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void MissingParentDirectory_DirectoryNotFound_NoAutoCreate()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakePng(32);

            string dir = CreateTempDir();
            string missing = Path.Combine(dir, "missing");
            try
            {
                Assert.Throws<DirectoryNotFoundException>(() => store.SaveAtomic(Path.Combine(missing, "out.png"), png));
                Assert.That(Directory.Exists(missing), Is.False);
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveSuccess_SmallerThanBuffer()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore(65536);
            NativeArray<byte> png = MakePng(100);

            string dir = CreateTempDir();
            try
            {
                string dest = Path.Combine(dir, "out.png");
                store.SaveAtomic(dest, png);

                Assert.That(File.Exists(dest), Is.True);
                byte[] actual = File.ReadAllBytes(dest);
                Assert.That(actual.Length, Is.EqualTo(png.Length));
                for (int i = 0; i < png.Length; i++)
                {
                    Assert.That(actual[i], Is.EqualTo(png[i]), "Byte mismatch at index " + i);
                }

                Assert.That(Directory.GetFiles(dir, "*.tmp"), Is.Empty);
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveSuccess_ExactBufferSize()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore(128);
            NativeArray<byte> png = MakePng(128);

            string dir = CreateTempDir();
            try
            {
                string dest = Path.Combine(dir, "out.png");
                store.SaveAtomic(dest, png);

                byte[] actual = File.ReadAllBytes(dest);
                Assert.That(actual.Length, Is.EqualTo(png.Length));
                for (int i = 0; i < png.Length; i++)
                {
                    Assert.That(actual[i], Is.EqualTo(png[i]), "Byte mismatch at index " + i);
                }
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveSuccess_MultipleChunks()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore(37);
            NativeArray<byte> png = MakePng(100); // 100 = 37*2 + 26

            string dir = CreateTempDir();
            try
            {
                string dest = Path.Combine(dir, "out.png");
                store.SaveAtomic(dest, png);

                byte[] actual = File.ReadAllBytes(dest);
                Assert.That(actual.Length, Is.EqualTo(png.Length));
                for (int i = 0; i < png.Length; i++)
                {
                    Assert.That(actual[i], Is.EqualTo(png[i]), "Byte mismatch at index " + i);
                }

                Assert.That(Directory.GetFiles(dir, "*.tmp"), Is.Empty);
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveSuccess_RealPng()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakeRealPng();

            string dir = CreateTempDir();
            try
            {
                string dest = Path.Combine(dir, "real.png");
                store.SaveAtomic(dest, png);

                Assert.That(File.Exists(dest), Is.True);
                byte[] actual = File.ReadAllBytes(dest);
                Assert.That(actual.Length, Is.EqualTo(png.Length));
                for (int i = 0; i < png.Length; i++)
                {
                    Assert.That(actual[i], Is.EqualTo(png[i]), "Byte mismatch at index " + i);
                }
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DestinationExistingFile_IOException_Unchanged()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakePng(32);

            string dir = CreateTempDir();
            try
            {
                string dest = Path.Combine(dir, "out.png");
                File.WriteAllBytes(dest, new byte[] { 1, 2, 3, 4 });
                DateTime lastWrite = File.GetLastWriteTimeUtc(dest);
                byte[] original = File.ReadAllBytes(dest);

                Assert.Throws<IOException>(() => store.SaveAtomic(dest, png));

                Assert.That(File.ReadAllBytes(dest), Is.EqualTo(original));
                Assert.That(new FileInfo(dest).Length, Is.EqualTo(original.Length));
                Assert.That(File.GetLastWriteTimeUtc(dest), Is.EqualTo(lastWrite));
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DestinationExistingDirectory_IOException_Unchanged()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakePng(32);

            string dir = CreateTempDir();
            try
            {
                string dest = Path.Combine(dir, "out.png");
                Directory.CreateDirectory(dest);

                Assert.Throws<IOException>(() => store.SaveAtomic(dest, png));
                Assert.That(Directory.Exists(dest), Is.True);
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DotDotPath_Normalized()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakePng(32);

            string dir = CreateTempDir();
            string sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            try
            {
                string dest = Path.Combine(sub, "..", "out.png");
                store.SaveAtomic(dest, png);

                Assert.That(File.Exists(Path.Combine(dir, "out.png")), Is.True);
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void InputUnchangedAfterSave()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore(37);
            NativeArray<byte> png = default;
            NativeArray<byte> snapshot = default;

            string dir = CreateTempDir();
            try
            {
                png = MakePng(100);
                snapshot = new NativeArray<byte>(png.Length, Allocator.Persistent);
                for (int i = 0; i < png.Length; i++)
                {
                    snapshot[i] = png[i];
                }

                store.SaveAtomic(Path.Combine(dir, "out.png"), png);

                for (int i = 0; i < png.Length; i++)
                {
                    Assert.That(png[i], Is.EqualTo(snapshot[i]), "Input changed at index " + i);
                }
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                if (snapshot.IsCreated) { snapshot.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveFailure_InputStillValid()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakePng(32);

            string dir = CreateTempDir();
            try
            {
                string dest = Path.Combine(dir, "out.png");
                File.WriteAllBytes(dest, new byte[] { 9, 9, 9 });

                Assert.Throws<IOException>(() => store.SaveAtomic(dest, png));

                Assert.That(png.IsCreated, Is.True);
                Assert.That(png.Length, Is.GreaterThan(8));
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveSuccess_DestinationCanBeRenamedDeleted()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = MakePng(32);

            string dir = CreateTempDir();
            try
            {
                string dest = Path.Combine(dir, "out.png");
                store.SaveAtomic(dest, png);

                string renamed = Path.Combine(dir, "renamed.png");
                File.Move(dest, renamed);
                File.Delete(renamed);

                Assert.That(File.Exists(dest), Is.False);
                Assert.That(File.Exists(renamed), Is.False);
            }
            finally
            {
                png.Dispose();
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void FileStore_HasSingleReusableChunkBuffer()
        {
            Type type = typeof(CaptureFramePngFileStore);

            int byteArrayFields = 0;
            foreach (FieldInfo field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(byte[]))
                {
                    byteArrayFields++;
                    Assert.That(field.IsInitOnly, Is.True, "Chunk buffer field must be readonly: " + field.Name);
                }
                else
                {
                    Assert.That(field.FieldType.IsArray, Is.False, "Unexpected array field: " + field.Name);
                    Assert.That(field.FieldType, Is.Not.EqualTo(typeof(MemoryStream)), "Unexpected MemoryStream field: " + field.Name);
                    Assert.That(field.FieldType, Is.Not.EqualTo(typeof(List<byte>)), "Unexpected List<byte> field: " + field.Name);
                }
            }

            Assert.That(byteArrayFields, Is.EqualTo(1));
        }
    }
}
