using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Collections;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngSaveReceiptTests
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
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-png-receipt-" + Guid.NewGuid().ToString("N"));
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

        private static string ToLowerHex(byte[] bytes)
        {
            const string hex = "0123456789abcdef";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0x0F];
            }

            return new string(chars);
        }

        private static string IndependentSha256(string path)
        {
            byte[] fileBytes = File.ReadAllBytes(path);
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(fileBytes));
            }
        }

        private static ConstructorInfo GetInternalCtor()
        {
            ConstructorInfo ctor = typeof(CaptureFramePngSaveReceipt).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(int), typeof(string) },
                null);

            Assert.That(ctor, Is.Not.Null, "Internal (string, int, string) constructor must exist.");
            return ctor;
        }

        private static void AssertInternalCtorThrows<T>(string path, int byteCount, string hash) where T : Exception
        {
            ConstructorInfo ctor = GetInternalCtor();
            try
            {
                ctor.Invoke(new object[] { path, byteCount, hash });
                Assert.Fail("Expected " + typeof(T).Name);
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<T>());
            }
        }

        [Test]
        public void SaveAtomicWithReceipt_Success()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(100);
                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.png");

                CaptureFramePngSaveReceipt receipt = store.SaveAtomicWithReceipt(dest, png);

                Assert.That(receipt, Is.Not.Null);
                Assert.That(File.Exists(dest), Is.True);
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DestinationPath_FullyQualified()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(32);
                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.png");

                CaptureFramePngSaveReceipt receipt = store.SaveAtomicWithReceipt(dest, png);

                Assert.That(receipt.DestinationPath, Is.EqualTo(Path.GetFullPath(dest)));
                Assert.That(Path.IsPathFullyQualified(receipt.DestinationPath), Is.True);
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DotDotPath_Normalized()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(32);
                dir = CreateTempDir();
                string sub = Path.Combine(dir, "sub");
                Directory.CreateDirectory(sub);

                CaptureFramePngSaveReceipt receipt = store.SaveAtomicWithReceipt(Path.Combine(sub, "..", "out.png"), png);

                Assert.That(receipt.DestinationPath, Is.EqualTo(Path.GetFullPath(Path.Combine(dir, "out.png"))));
                Assert.That(receipt.DestinationPath.IndexOf("..", StringComparison.Ordinal), Is.LessThan(0));
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void UppercaseExtension_Accepted()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(32);
                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.PNG");

                CaptureFramePngSaveReceipt receipt = store.SaveAtomicWithReceipt(dest, png);

                Assert.That(receipt.DestinationPath, Is.EqualTo(Path.GetFullPath(dest)));
                Assert.That(File.Exists(dest), Is.True);
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void ByteCount_MatchesInputLength()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(100);
                dir = CreateTempDir();

                CaptureFramePngSaveReceipt receipt = store.SaveAtomicWithReceipt(Path.Combine(dir, "out.png"), png);

                Assert.That(receipt.ByteCount, Is.EqualTo(png.Length));
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void ContentSha256_Is64LowercaseHex()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(32);
                dir = CreateTempDir();

                CaptureFramePngSaveReceipt receipt = store.SaveAtomicWithReceipt(Path.Combine(dir, "out.png"), png);

                string hash = receipt.ContentSha256;
                Assert.That(hash.Length, Is.EqualTo(64));
                for (int i = 0; i < hash.Length; i++)
                {
                    char c = hash[i];
                    Assert.That((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), Is.True, "Non-lowercase-hex char at index " + i);
                }
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void HashMatchesIndependent_SingleChunk()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore(65536);
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(100); // less than one chunk
                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.png");

                CaptureFramePngSaveReceipt receipt = store.SaveAtomicWithReceipt(dest, png);

                Assert.That(receipt.ContentSha256, Is.EqualTo(IndependentSha256(dest)));
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void HashMatchesIndependent_ExactChunk()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore(128);
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(128); // exactly one chunk
                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.png");

                CaptureFramePngSaveReceipt receipt = store.SaveAtomicWithReceipt(dest, png);

                Assert.That(receipt.ContentSha256, Is.EqualTo(IndependentSha256(dest)));
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void HashMatchesIndependent_MultipleChunks()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore(37);
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(100); // 100 = 37*2 + 26
                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.png");

                CaptureFramePngSaveReceipt receipt = store.SaveAtomicWithReceipt(dest, png);

                Assert.That(receipt.ContentSha256, Is.EqualTo(IndependentSha256(dest)));
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void HashMatchesIndependent_RealPng()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakeRealPng();
                dir = CreateTempDir();
                string dest = Path.Combine(dir, "real.png");

                CaptureFramePngSaveReceipt receipt = store.SaveAtomicWithReceipt(dest, png);

                Assert.That(receipt.ContentSha256, Is.EqualTo(IndependentSha256(dest)));
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void InputUnchangedAfterReceipt()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore(37);
            NativeArray<byte> png = default;
            NativeArray<byte> snapshot = default;
            string dir = null;
            try
            {
                png = MakePng(100);
                snapshot = new NativeArray<byte>(png.Length, Allocator.Persistent);
                for (int i = 0; i < png.Length; i++)
                {
                    snapshot[i] = png[i];
                }

                dir = CreateTempDir();
                store.SaveAtomicWithReceipt(Path.Combine(dir, "out.png"), png);

                Assert.That(png.IsCreated, Is.True);
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
        public void Receipt_NoPublicSetters_NoForbiddenFields()
        {
            Type type = typeof(CaptureFramePngSaveReceipt);

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.GetSetMethod(false), Is.Null, property.Name + " must not have a public setter.");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsArray, Is.False, "Unexpected array field: " + field.Name);
                Assert.That(typeof(Stream).IsAssignableFrom(field.FieldType), Is.False, "Unexpected Stream field: " + field.Name);
                Assert.That(typeof(FileInfo).IsAssignableFrom(field.FieldType), Is.False, "Unexpected FileInfo field: " + field.Name);
                string name = field.FieldType.FullName ?? field.FieldType.Name;
                Assert.That(name.IndexOf("NativeArray", StringComparison.Ordinal), Is.LessThan(0), "Unexpected NativeArray field: " + field.Name);
            }
        }

        [Test]
        public void Receipt_NoPublicConstructor()
        {
            Assert.That(typeof(CaptureFramePngSaveReceipt).GetConstructors(), Is.Empty);
        }

        [Test]
        public void Receipt_InternalCtor_RejectsInvalidPath()
        {
            string validHash = new string('a', 64);

            AssertInternalCtorThrows<ArgumentNullException>(null, 9, validHash);
            AssertInternalCtorThrows<ArgumentException>("relative.png", 9, validHash);
            AssertInternalCtorThrows<ArgumentException>(Path.Combine(Path.GetTempPath(), "out.txt"), 9, validHash);
        }

        [Test]
        public void Receipt_InternalCtor_RejectsInvalidByteCount()
        {
            string validPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "receipt.png"));
            string validHash = new string('a', 64);

            AssertInternalCtorThrows<ArgumentOutOfRangeException>(validPath, 0, validHash);
            AssertInternalCtorThrows<ArgumentOutOfRangeException>(validPath, 8, validHash);
            AssertInternalCtorThrows<ArgumentOutOfRangeException>(validPath, -1, validHash);
        }

        [Test]
        public void Receipt_InternalCtor_RejectsInvalidHash()
        {
            string validPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "receipt.png"));

            AssertInternalCtorThrows<ArgumentNullException>(validPath, 9, null);
            AssertInternalCtorThrows<ArgumentException>(validPath, 9, new string('a', 63));
            AssertInternalCtorThrows<ArgumentException>(validPath, 9, new string('a', 65));
            AssertInternalCtorThrows<ArgumentException>(validPath, 9, new string('A', 64));
            AssertInternalCtorThrows<ArgumentException>(validPath, 9, 'g' + new string('a', 63));
        }

        [Test]
        public void Receipt_InternalCtor_AcceptsValid()
        {
            ConstructorInfo ctor = GetInternalCtor();
            string validPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "receipt.png"));
            string validHash = new string('a', 64);

            object result = ctor.Invoke(new object[] { validPath, 9, validHash });

            Assert.That(result, Is.Not.Null);
            CaptureFramePngSaveReceipt receipt = (CaptureFramePngSaveReceipt)result;
            Assert.That(receipt.DestinationPath, Is.EqualTo(validPath));
            Assert.That(receipt.ByteCount, Is.EqualTo(9));
            Assert.That(receipt.ContentSha256, Is.EqualTo(validHash));
        }

        [Test]
        public void SaveWithReceipt_Failures_ThrowNoReceipt()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = default;
            NativeArray<byte> badPng = default;
            string dir = null;
            try
            {
                png = MakePng(32);
                badPng = MakePng(32);
                badPng[0] = 0x00;
                dir = CreateTempDir();

                string existing = Path.Combine(dir, "existing.png");
                File.WriteAllBytes(existing, new byte[] { 1, 2, 3, 4 });
                Assert.Throws<IOException>(() => store.SaveAtomicWithReceipt(existing, png));

                string missing = Path.Combine(dir, "missing", "out.png");
                Assert.Throws<DirectoryNotFoundException>(() => store.SaveAtomicWithReceipt(missing, png));

                Assert.Throws<ArgumentException>(() => store.SaveAtomicWithReceipt(Path.Combine(dir, "out.png"), badPng));
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                if (badPng.IsCreated) { badPng.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveFailure_NoTempLeftover()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore();
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(32);
                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.png");
                File.WriteAllBytes(dest, new byte[] { 9, 9, 9 });

                Assert.Throws<IOException>(() => store.SaveAtomicWithReceipt(dest, png));

                Assert.That(Directory.GetFiles(dir, "*.tmp"), Is.Empty);
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveAtomic_StillWritesSingleFile()
        {
            CaptureFramePngFileStore store = new CaptureFramePngFileStore(37);
            NativeArray<byte> png = default;
            string dir = null;
            try
            {
                png = MakePng(100);
                dir = CreateTempDir();
                string dest = Path.Combine(dir, "out.png");

                store.SaveAtomic(dest, png);

                Assert.That(File.Exists(dest), Is.True);
                Assert.That(Directory.GetFiles(dir, "*.tmp"), Is.Empty);
                Assert.That(Directory.GetFiles(dir).Length, Is.EqualTo(1));

                byte[] actual = File.ReadAllBytes(dest);
                Assert.That(actual.Length, Is.EqualTo(png.Length));
                for (int i = 0; i < png.Length; i++)
                {
                    Assert.That(actual[i], Is.EqualTo(png[i]), "Byte mismatch at index " + i);
                }
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void FileStore_NoFullPngCopyField()
        {
            foreach (FieldInfo field in typeof(CaptureFramePngFileStore).GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(byte[]))
                {
                    // The single reusable chunk buffer is permitted.
                    continue;
                }

                Assert.That(field.FieldType.IsArray, Is.False, "Unexpected array field: " + field.Name);
                Assert.That(typeof(Stream).IsAssignableFrom(field.FieldType), Is.False, "Unexpected Stream field: " + field.Name);
                Assert.That(typeof(FileInfo).IsAssignableFrom(field.FieldType), Is.False, "Unexpected FileInfo field: " + field.Name);
                string name = field.FieldType.FullName ?? field.FieldType.Name;
                Assert.That(name.IndexOf("NativeArray", StringComparison.Ordinal), Is.LessThan(0), "Unexpected NativeArray field: " + field.Name);
            }
        }
    }
}
