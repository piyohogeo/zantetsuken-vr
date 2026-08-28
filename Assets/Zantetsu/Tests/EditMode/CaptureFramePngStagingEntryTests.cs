using System;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngStagingEntryTests
    {
        private const string KnownSha256 = "630dcd2966c4336691125448bbb25b4ff412a49c732db2c8abc1b8581bd710dd";

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetEntryType() => GetTypeFromAssembly("CaptureFramePngStagingEntry");

        private static Type GetFactoryType() => GetTypeFromAssembly("CaptureFramePngStagingEntryFactory");

        private static object GetProperty(object target, string name)
        {
            PropertyInfo prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, target.GetType().Name + "." + name + " property not found.");
            return prop.GetValue(target);
        }

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return tie.InnerException;
            }

            return ex;
        }

        // ---- Input factories ----

        private static CaptureFrameRequest MakeRequest(long captureFrameId, long testRunId)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1, 20, 3, 4, captureFrameId, 30, testRunId, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(context, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
        }

        private static NativeArray<byte> MakePng(byte[] data)
        {
            return new NativeArray<byte>(data, Allocator.Persistent);
        }

        private static byte[] KnownBytes()
        {
            byte[] data = new byte[32];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)i;
            }

            return data;
        }

        private static string ComputeSha256Direct(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                const string hex = "0123456789abcdef";
                char[] chars = new char[hash.Length * 2];
                for (int i = 0; i < hash.Length; i++)
                {
                    byte b = hash[i];
                    chars[i * 2] = hex[b >> 4];
                    chars[i * 2 + 1] = hex[b & 0x0F];
                }

                return new string(chars);
            }
        }

        // ---- Factory / invoke helpers ----

        private static object CreateFactory(int copyBufferSize = 65536)
        {
            ConstructorInfo ctor = GetFactoryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureFramePngStagingEntryFactory constructor not found.");
            return ctor.Invoke(new object[] { copyBufferSize });
        }

        private static object InvokeCreate(
            object factory,
            CaptureFrameRequest request,
            NativeArray<byte> pngBytes,
            out NativeArray<byte> pngBytesAfter,
            out Exception exception)
        {
            MethodInfo method = GetFactoryType().GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "Create method not found.");

            object[] args = new object[] { request, pngBytes };
            object entry = null;
            exception = null;
            try
            {
                entry = method.Invoke(factory, args);
            }
            catch (Exception ex)
            {
                exception = Unwrap(ex);
            }

            pngBytesAfter = (NativeArray<byte>)args[1];
            return entry;
        }

        private static NativeArray<byte> GetPngBytes(object entry)
        {
            MethodInfo method = GetEntryType().GetMethod("GetPngBytes", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "GetPngBytes method not found.");
            return (NativeArray<byte>)method.Invoke(entry, null);
        }

        private static Exception GetPngBytesException(object entry)
        {
            try
            {
                GetPngBytes(entry);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        /// <summary>
        /// Allocates the input PNG, runs <see cref="InvokeCreate"/>, and then
        /// runs <paramref name="body"/>. On success the entry owns the PNG; on
        /// failure the caller still owns it. A <c>finally</c> always disposes
        /// either the caller-held PNG or the entry, so an assertion failure in
        /// <paramref name="body"/> never leaks the persistent allocation.
        /// </summary>
        private static void RunCreate(
            object factory,
            CaptureFrameRequest request,
            byte[] pngData,
            Action<object, NativeArray<byte>, Exception> body)
        {
            NativeArray<byte> png = pngData != null ? MakePng(pngData) : default;
            object entry = null;
            NativeArray<byte> after = default;
            Exception exception = null;

            try
            {
                entry = InvokeCreate(factory, request, png, out after, out exception);
                body(entry, after, exception);
            }
            finally
            {
                if (entry != null)
                {
                    ((IDisposable)entry).Dispose();
                }
                else if (after.IsCreated)
                {
                    after.Dispose();
                }
                else if (png.IsCreated)
                {
                    // InvokeCreate failed before it could transfer or return the
                    // PNG: the original allocation is still owned by this helper.
                    png.Dispose();
                }
            }
        }

        private static ConstructorInfo GetEntryCtor()
        {
            ConstructorInfo ctor = GetEntryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(long), typeof(NativeArray<byte>), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFramePngStagingEntry constructor not found.");
            return ctor;
        }

        private static Exception CreateEntryException(long testRunId, long captureFrameId, NativeArray<byte> png, string sha)
        {
            try
            {
                GetEntryCtor().Invoke(new object[] { testRunId, captureFrameId, png, sha });
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        // ---- Type shape ----

        [Test]
        public void Factory_InternalSealed_NoPublicCtor_NoStaticState_NotMonoBehaviourOrScriptableObject()
        {
            Type type = GetFactoryType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Entry_InternalSealed_NoPublicCtorOrSetter_NotMonoBehaviourOrScriptableObject()
        {
            Type type = GetEntryType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be read-only.");
            }
        }

        // ---- Factory constructor boundaries ----

        [Test]
        public void FactoryCtor_NonPositiveCopyBufferSize_Rejected()
        {
            ConstructorInfo ctor = GetFactoryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int) }, null);

            foreach (int size in new[] { 0, -1, int.MinValue })
            {
                try
                {
                    ctor.Invoke(new object[] { size });
                    Assert.Fail("Expected ArgumentOutOfRangeException for size " + size + ".");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
                    Assert.That(((ArgumentOutOfRangeException)ex.InnerException).ParamName, Is.EqualTo("copyBufferSize"));
                }
            }
        }

        [Test]
        public void FactoryCtor_DefaultCopyBufferSize_65536()
        {
            object factory = CreateFactory();
            Assert.That((int)GetProperty(factory, "CopyBufferSize"), Is.EqualTo(65536));
        }

        // ---- Entry constructor SHA-256 validation ----

        [Test]
        public void EntryCtor_InvalidSha256_Rejected()
        {
            NativeArray<byte> png = MakePng(KnownBytes());
            try
            {
                // null
                Exception exNull = CreateEntryException(1, 7, png, null);
                Assert.That(exNull, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)exNull).ParamName, Is.EqualTo("contentSha256"));

                // too short
                Exception exShort = CreateEntryException(1, 7, png, "x");
                Assert.That(exShort, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)exShort).ParamName, Is.EqualTo("contentSha256"));

                // 63 lowercase hex characters
                Exception ex63 = CreateEntryException(1, 7, png, KnownSha256.Substring(1));
                Assert.That(ex63, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex63).ParamName, Is.EqualTo("contentSha256"));

                // 65 lowercase hex characters
                Exception ex65 = CreateEntryException(1, 7, png, KnownSha256 + "0");
                Assert.That(ex65, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex65).ParamName, Is.EqualTo("contentSha256"));

                // 64 uppercase hex characters
                Exception exUpper = CreateEntryException(1, 7, png, KnownSha256.ToUpperInvariant());
                Assert.That(exUpper, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)exUpper).ParamName, Is.EqualTo("contentSha256"));

                // 64 non-hex characters
                Exception exNonHex = CreateEntryException(1, 7, png, new string('g', 64));
                Assert.That(exNonHex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)exNonHex).ParamName, Is.EqualTo("contentSha256"));
            }
            finally
            {
                png.Dispose();
            }
        }

        // ---- Create validation ----

        [Test]
        public void Create_InvalidOrDefaultRequest_Rejected()
        {
            object factory = CreateFactory();

            RunCreate(factory, default, KnownBytes(), (entry, after, ex) =>
            {
                Assert.That(entry, Is.Null);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("request"));
                Assert.That(after.IsCreated, Is.True); // caller still owns
            });
        }

        [Test]
        public void Create_NonPositiveTestRunId_Rejected()
        {
            object factory = CreateFactory();

            foreach (long testRunId in new[] { 0L, -1L })
            {
                RunCreate(factory, MakeRequest(7, testRunId), KnownBytes(), (entry, after, ex) =>
                {
                    Assert.That(entry, Is.Null, "TestRunId " + testRunId);
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "TestRunId " + testRunId);
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("request"));
                    Assert.That(after.IsCreated, Is.True);
                });
            }
        }

        [Test]
        public void Create_NonPositiveCaptureFrameId_Rejected()
        {
            object factory = CreateFactory();

            foreach (long captureFrameId in new[] { 0L, -1L })
            {
                RunCreate(factory, MakeRequest(captureFrameId, 1), KnownBytes(), (entry, after, ex) =>
                {
                    Assert.That(entry, Is.Null, "CaptureFrameId " + captureFrameId);
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "CaptureFrameId " + captureFrameId);
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("request"));
                    Assert.That(after.IsCreated, Is.True);
                });
            }
        }

        [Test]
        public void Create_UncreatedPng_Rejected()
        {
            object factory = CreateFactory();

            RunCreate(factory, MakeRequest(7, 1), null, (entry, after, ex) =>
            {
                Assert.That(entry, Is.Null);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("pngBytes"));
                Assert.That(after.IsCreated, Is.False);
            });
        }

        [Test]
        public void Create_TooShortPng_Rejected()
        {
            object factory = CreateFactory();

            foreach (int length in new[] { 0, 1, 8 })
            {
                byte[] shortData = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    shortData[i] = (byte)i;
                }

                RunCreate(factory, MakeRequest(7, 1), shortData, (entry, after, ex) =>
                {
                    Assert.That(entry, Is.Null, "length " + length);
                    Assert.That(ex, Is.TypeOf<ArgumentException>(), "length " + length);
                    Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("pngBytes"));
                    Assert.That(after.IsCreated, Is.True); // caller still owns
                });
            }
        }

        // ---- Successful creation ----

        [Test]
        public void Create_KnownBytes_ByteCountAndSha256()
        {
            object factory = CreateFactory();
            byte[] data = KnownBytes();

            RunCreate(factory, MakeRequest(7, 1), data, (entry, after, ex) =>
            {
                Assert.That(ex, Is.Null);
                Assert.That(entry, Is.Not.Null);
                Assert.That(after.IsCreated, Is.False); // ownership transferred

                Assert.That((long)GetProperty(entry, "TestRunId"), Is.EqualTo(1));
                Assert.That((long)GetProperty(entry, "CaptureFrameId"), Is.EqualTo(7));
                Assert.That((int)GetProperty(entry, "ByteCount"), Is.EqualTo(32));
                Assert.That((string)GetProperty(entry, "ContentSha256"), Is.EqualTo(KnownSha256));
                Assert.That((string)GetProperty(entry, "ContentSha256"), Is.EqualTo(ComputeSha256Direct(data)));
            });
        }

        [Test]
        public void Create_HashDoesNotMutateInputPng()
        {
            object factory = CreateFactory();
            byte[] data = KnownBytes();

            RunCreate(factory, MakeRequest(7, 1), data, (entry, after, ex) =>
            {
                Assert.That(ex, Is.Null);
                NativeArray<byte> view = GetPngBytes(entry);
                Assert.That(view.Length, Is.EqualTo(data.Length));
                for (int i = 0; i < data.Length; i++)
                {
                    Assert.That(view[i], Is.EqualTo(data[i]));
                }
            });
        }

        [Test]
        public void Create_Success_NullsOutInputRef()
        {
            object factory = CreateFactory();

            RunCreate(factory, MakeRequest(7, 1), KnownBytes(), (entry, after, ex) =>
            {
                Assert.That(ex, Is.Null);
                Assert.That(entry, Is.Not.Null);
                Assert.That(after.IsCreated, Is.False);
            });
        }

        [Test]
        public void Create_Failure_InputRefAndOwnershipMaintained()
        {
            object factory = CreateFactory();

            // Too-short PNG: validation fails and ownership stays with the caller.
            byte[] shortData = new byte[4];
            RunCreate(factory, MakeRequest(7, 1), shortData, (entry, after, ex) =>
            {
                Assert.That(entry, Is.Null);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(after.IsCreated, Is.True);
                Assert.That(after.Length, Is.EqualTo(4));
            });
        }

        // ---- Entry behavior ----

        [Test]
        public void Entry_NonOwningView_ReferencesSameAllocation()
        {
            object factory = CreateFactory();
            byte[] data = KnownBytes();

            RunCreate(factory, MakeRequest(7, 1), data, (entry, after, ex) =>
            {
                Assert.That(ex, Is.Null);
                NativeArray<byte> view = GetPngBytes(entry);
                Assert.That(view.IsCreated, Is.True);
                Assert.That(view.Length, Is.EqualTo(data.Length));
                for (int i = 0; i < data.Length; i++)
                {
                    Assert.That(view[i], Is.EqualTo(data[i]));
                }

                // The view references the same allocation (and safety handle) the
                // entry owns: once the entry disposes it, indexing the stale view
                // fails its safety check with ObjectDisposedException.
                ((IDisposable)entry).Dispose();
                Assert.Throws<ObjectDisposedException>(() => { _ = view[0]; });
            });
        }

        [Test]
        public void Entry_Dispose_Idempotent()
        {
            object factory = CreateFactory();

            RunCreate(factory, MakeRequest(7, 1), KnownBytes(), (entry, after, ex) =>
            {
                Assert.That(ex, Is.Null);
                Assert.DoesNotThrow(() => ((IDisposable)entry).Dispose());
                Assert.DoesNotThrow(() => ((IDisposable)entry).Dispose());
            });
        }

        [Test]
        public void Entry_AccessAfterDispose_Throws()
        {
            object factory = CreateFactory();

            RunCreate(factory, MakeRequest(7, 1), KnownBytes(), (entry, after, ex) =>
            {
                Assert.That(ex, Is.Null);
                ((IDisposable)entry).Dispose();

                Exception accessEx = GetPngBytesException(entry);
                Assert.That(accessEx, Is.TypeOf<ObjectDisposedException>());
            });
        }

        [Test]
        public void Entry_MetadataUnchangedAfterDispose()
        {
            object factory = CreateFactory();
            byte[] data = KnownBytes();

            RunCreate(factory, MakeRequest(7, 1), data, (entry, after, ex) =>
            {
                Assert.That(ex, Is.Null);
                long testRunId = (long)GetProperty(entry, "TestRunId");
                long captureFrameId = (long)GetProperty(entry, "CaptureFrameId");
                int byteCount = (int)GetProperty(entry, "ByteCount");
                string contentSha256 = (string)GetProperty(entry, "ContentSha256");
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.True);

                ((IDisposable)entry).Dispose();

                Assert.That((long)GetProperty(entry, "TestRunId"), Is.EqualTo(testRunId));
                Assert.That((long)GetProperty(entry, "CaptureFrameId"), Is.EqualTo(captureFrameId));
                Assert.That((int)GetProperty(entry, "ByteCount"), Is.EqualTo(byteCount));
                Assert.That((string)GetProperty(entry, "ContentSha256"), Is.EqualTo(contentSha256));
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.False);
            });
        }

        [Test]
        public void MultipleEntries_OwnershipIndependent()
        {
            object factory = CreateFactory();

            byte[] data1 = KnownBytes();
            byte[] data2 = new byte[16];
            for (int i = 0; i < data2.Length; i++)
            {
                data2[i] = (byte)(255 - i);
            }

            NativeArray<byte> png1 = default;
            NativeArray<byte> png2 = default;
            object entry1 = null;
            object entry2 = null;
            NativeArray<byte> caller1 = default;
            NativeArray<byte> caller2 = default;

            try
            {
                png1 = MakePng(data1);
                png2 = MakePng(data2);

                entry1 = InvokeCreate(factory, MakeRequest(1, 1), png1, out caller1, out Exception ex1);
                entry2 = InvokeCreate(factory, MakeRequest(2, 1), png2, out caller2, out Exception ex2);

                Assert.That(ex1, Is.Null);
                Assert.That(ex2, Is.Null);
                Assert.That(entry1, Is.Not.Null);
                Assert.That(entry2, Is.Not.Null);
                Assert.That(caller1.IsCreated, Is.False);
                Assert.That(caller2.IsCreated, Is.False);

                NativeArray<byte> view1 = GetPngBytes(entry1);
                NativeArray<byte> view2 = GetPngBytes(entry2);
                Assert.That(view1.IsCreated, Is.True);
                Assert.That(view2.IsCreated, Is.True);

                ((IDisposable)entry1).Dispose();

                // entry1's allocation is freed (its stale view now fails safety),
                // while entry2's allocation remains independent and readable.
                Assert.Throws<ObjectDisposedException>(() => { _ = view1[0]; });
                Assert.That(view2.IsCreated, Is.True);
                for (int i = 0; i < data2.Length; i++)
                {
                    Assert.That(view2[i], Is.EqualTo(data2[i]));
                }
            }
            finally
            {
                if (entry1 != null)
                {
                    ((IDisposable)entry1).Dispose();
                }
                else if (caller1.IsCreated)
                {
                    caller1.Dispose();
                }
                else if (png1.IsCreated)
                {
                    png1.Dispose();
                }

                if (entry2 != null)
                {
                    ((IDisposable)entry2).Dispose();
                }
                else if (caller2.IsCreated)
                {
                    caller2.Dispose();
                }
                else if (png2.IsCreated)
                {
                    png2.Dispose();
                }
            }
        }

        // ---- Factory storage shape ----

        [Test]
        public void Factory_HoldsOnlyCopyBuffer_NoEntryOrPngFields()
        {
            Type type = GetFactoryType();

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(byte[])));
            Assert.That(fields[0].FieldType, Is.Not.EqualTo(GetEntryType()));
            Assert.That(fields[0].FieldType, Is.Not.EqualTo(typeof(NativeArray<byte>)));
        }

        [Test]
        public void Factory_NoFullPngSizedManagedArray()
        {
            object factory = CreateFactory(16); // chunk buffer smaller than the PNG
            Assert.That((int)GetProperty(factory, "CopyBufferSize"), Is.EqualTo(16));

            FieldInfo[] fields = GetFactoryType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(1));
            byte[] buffer = (byte[])fields[0].GetValue(factory);
            Assert.That(buffer.Length, Is.EqualTo(16));

            byte[] data = new byte[64];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)i;
            }

            RunCreate(factory, MakeRequest(7, 1), data, (entry, after, ex) =>
            {
                Assert.That(ex, Is.Null);
                Assert.That(after.IsCreated, Is.False);
                Assert.That((int)GetProperty(entry, "ByteCount"), Is.EqualTo(64));
                Assert.That((string)GetProperty(entry, "ContentSha256"), Is.EqualTo(ComputeSha256Direct(data)));
            });
        }
    }
}
