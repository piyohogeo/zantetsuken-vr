using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunMarkerWriteOperationTests
    {
        private const int MaxByteCount = 4 * 1024;

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string Join(string left, string right) => left + (IsWindows ? "\\" : "/") + right;

        private static string RunRoot() => IsWindows ? "C:\\staging\\runs\\run-1" : "/staging/runs/run-1";

        private static string InitializationFinalPath() => Join(RunRoot(), "run.init");

        private static string InitializationTemporaryPath() => InitializationFinalPath() + ".tmp";

        private static string ReadyFinalPath() => Join(RunRoot(), "run.ready");

        private static string ReadyTemporaryPath() => ReadyFinalPath() + ".tmp";

        private static byte[] NewBytes(int length)
        {
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = (byte)(i % 256);
            }

            return bytes;
        }

        private static byte[] GetFieldBytes(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return (byte[])field.GetValue(target);
        }

        // ---- Enum contract ----

        [Test]
        public void Enum_Contract()
        {
            Type type = typeof(CaptureRunMarkerKind);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));

            string[] names = Enum.GetNames(type);
            Assert.That(names, Is.EqualTo(new[] { "None", "Initialization", "Ready" }));

            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(3));
            Assert.That((int)values.GetValue(0), Is.EqualTo(0));
            Assert.That((int)values.GetValue(1), Is.EqualTo(1));
            Assert.That((int)values.GetValue(2), Is.EqualTo(2));

            Assert.That(Convert.ToInt32(CaptureRunMarkerKind.None), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(CaptureRunMarkerKind.Initialization), Is.EqualTo(1));
            Assert.That(Convert.ToInt32(CaptureRunMarkerKind.Ready), Is.EqualTo(2));

            // No alias and no missing number: values are pairwise distinct and consecutive.
            Assert.That((int)values.GetValue(0), Is.Not.EqualTo((int)values.GetValue(1)));
            Assert.That((int)values.GetValue(1), Is.Not.EqualTo((int)values.GetValue(2)));
        }

        // ---- Construction ----

        [Test]
        public void FourValidCombinations_Accepted()
        {
            foreach (CaptureRunRootRole role in new[] { CaptureRunRootRole.Staging, CaptureRunRootRole.Final })
            {
                foreach (CaptureRunMarkerKind kind in new[] { CaptureRunMarkerKind.Initialization, CaptureRunMarkerKind.Ready })
                {
                    string finalPath = kind == CaptureRunMarkerKind.Initialization ? InitializationFinalPath() : ReadyFinalPath();
                    string temporaryPath = finalPath + ".tmp";
                    byte[] bytes = NewBytes(8);

                    CaptureRunMarkerWriteOperation op = new CaptureRunMarkerWriteOperation(role, kind, temporaryPath, finalPath, ref bytes);

                    Assert.That(op.RootRole, Is.EqualTo(role));
                    Assert.That(op.MarkerKind, Is.EqualTo(kind));
                    Assert.That(op.TemporaryPath, Is.EqualTo(temporaryPath));
                    Assert.That(op.FinalPath, Is.EqualTo(finalPath));
                    Assert.That(bytes, Is.Null, "Caller ref must be nulled on success.");
                }
            }
        }

        [Test]
        public void Properties_And_ByteCount()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";
            byte[] bytes = NewBytes(64);

            CaptureRunMarkerWriteOperation op = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes);

            Assert.That(op.RootRole, Is.EqualTo(CaptureRunRootRole.Staging));
            Assert.That(op.MarkerKind, Is.EqualTo(CaptureRunMarkerKind.Initialization));
            Assert.That(op.TemporaryPath, Is.EqualTo(temporaryPath));
            Assert.That(op.FinalPath, Is.EqualTo(finalPath));
            Assert.That(op.ByteCount, Is.EqualTo(64));
            Assert.That(op.GetCanonicalBytes().Length, Is.EqualTo(64));
        }

        [Test]
        public void InvalidRootRole_RejectedWithParamName()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";

            foreach (CaptureRunRootRole role in new[]
            {
                CaptureRunRootRole.None,
                (CaptureRunRootRole)(-1),
                (CaptureRunRootRole)3,
                (CaptureRunRootRole)int.MaxValue
            })
            {
                byte[] bytes = NewBytes(8);
                byte[] before = (byte[])bytes.Clone();

                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunMarkerWriteOperation(role, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes));

                Assert.That(ex.ParamName, Is.EqualTo("rootRole"));
                Assert.That(bytes, Is.Not.Null, "Caller ref must be unchanged on failure.");
                Assert.That(bytes, Is.EqualTo(before), "Array content must be unchanged on failure.");
            }
        }

        [Test]
        public void InvalidMarkerKind_RejectedWithParamName()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";

            foreach (CaptureRunMarkerKind kind in new[]
            {
                CaptureRunMarkerKind.None,
                (CaptureRunMarkerKind)(-1),
                (CaptureRunMarkerKind)3,
                (CaptureRunMarkerKind)int.MaxValue
            })
            {
                byte[] bytes = NewBytes(8);
                byte[] before = (byte[])bytes.Clone();

                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunMarkerWriteOperation(CaptureRunRootRole.Staging, kind, temporaryPath, finalPath, ref bytes));

                Assert.That(ex.ParamName, Is.EqualTo("markerKind"));
                Assert.That(bytes, Is.Not.Null, "Caller ref must be unchanged on failure.");
                Assert.That(bytes, Is.EqualTo(before), "Array content must be unchanged on failure.");
            }
        }

        [Test]
        public void NullPaths_RejectedWithParamName()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";

            byte[] bytes1 = NewBytes(8);
            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunMarkerWriteOperation(CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, null, finalPath, ref bytes1));
            Assert.That(ex1.ParamName, Is.EqualTo("temporaryPath"));
            Assert.That(bytes1, Is.Not.Null);

            byte[] bytes2 = NewBytes(8);
            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunMarkerWriteOperation(CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, null, ref bytes2));
            Assert.That(ex2.ParamName, Is.EqualTo("finalPath"));
            Assert.That(bytes2, Is.Not.Null);
        }

        [Test]
        public void InvalidBytes_RejectedWithParamName()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";

            byte[] nullBytes = null;
            ArgumentNullException exNull = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunMarkerWriteOperation(CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref nullBytes));
            Assert.That(exNull.ParamName, Is.EqualTo("canonicalBytes"));

            byte[] emptyBytes = new byte[0];
            ArgumentException exEmpty = Assert.Throws<ArgumentException>(
                () => new CaptureRunMarkerWriteOperation(CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref emptyBytes));
            Assert.That(exEmpty.ParamName, Is.EqualTo("canonicalBytes"));
            Assert.That(emptyBytes.Length, Is.EqualTo(0));

            byte[] tooBig = NewBytes(MaxByteCount + 1);
            ArgumentException exBig = Assert.Throws<ArgumentException>(
                () => new CaptureRunMarkerWriteOperation(CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref tooBig));
            Assert.That(exBig.ParamName, Is.EqualTo("canonicalBytes"));
            Assert.That(tooBig.Length, Is.EqualTo(MaxByteCount + 1));
        }

        [Test]
        public void Bytes_4096Boundary_Accepted()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";
            byte[] bytes = NewBytes(MaxByteCount);

            CaptureRunMarkerWriteOperation op = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes);

            Assert.That(op.ByteCount, Is.EqualTo(MaxByteCount));
            Assert.That(bytes, Is.Null);
        }

        [Test]
        public void RelativePath_Rejected()
        {
            byte[] bytes = NewBytes(8);
            ArgumentException ex1 = Assert.Throws<ArgumentException>(
                () => new CaptureRunMarkerWriteOperation(
                    CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, "relative.tmp", InitializationFinalPath(), ref bytes));
            Assert.That(ex1.ParamName, Is.EqualTo("temporaryPath"));
            Assert.That(bytes, Is.Not.Null);

            byte[] bytes2 = NewBytes(8);
            ArgumentException ex2 = Assert.Throws<ArgumentException>(
                () => new CaptureRunMarkerWriteOperation(
                    CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, InitializationTemporaryPath(), "relative", ref bytes2));
            Assert.That(ex2.ParamName, Is.EqualTo("finalPath"));
            Assert.That(bytes2, Is.Not.Null);
        }

        [Test]
        public void ParentDirectoryMismatch_Rejected()
        {
            string otherRunRoot = IsWindows ? "C:\\staging\\runs\\run-2" : "/staging/runs/run-2";
            string finalPath = Join(otherRunRoot, "run.init");
            string temporaryPath = InitializationTemporaryPath();
            byte[] bytes = NewBytes(8);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunMarkerWriteOperation(
                    CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes));

            Assert.That(ex.ParamName, Is.EqualTo("temporaryPath"));
            Assert.That(bytes, Is.Not.Null);
        }

        [Test]
        public void TmpSuffixMismatch_Rejected()
        {
            string finalPath = InitializationFinalPath();
            byte[] bytes = NewBytes(8);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunMarkerWriteOperation(
                    CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, finalPath, finalPath, ref bytes));

            Assert.That(ex.ParamName, Is.EqualTo("temporaryPath"));
            Assert.That(bytes, Is.Not.Null);
        }

        [Test]
        public void MarkerKindBasenameMismatch_Rejected()
        {
            string finalPath = ReadyFinalPath();
            string temporaryPath = ReadyTemporaryPath();
            byte[] bytes = NewBytes(8);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunMarkerWriteOperation(
                    CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes));

            Assert.That(ex.ParamName, Is.EqualTo("temporaryPath"));
            Assert.That(bytes, Is.Not.Null);
        }

        [Test]
        public void BasenameCaseDifference_Rejected()
        {
            string finalPath = Join(RunRoot(), "RUN.INIT");
            string temporaryPath = finalPath + ".tmp";
            byte[] bytes = NewBytes(8);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunMarkerWriteOperation(
                    CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes));

            Assert.That(ex.ParamName, Is.EqualTo("temporaryPath"));
            Assert.That(bytes, Is.Not.Null);
        }

        // ---- Buffer ownership ----

        [Test]
        public void Success_OnlyNullsRef()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";
            byte[] bytes = NewBytes(8);
            byte[] original = (byte[])bytes.Clone();

            CaptureRunMarkerWriteOperation op = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes);

            Assert.That(bytes, Is.Null);
            Assert.That(op.GetCanonicalBytes(), Is.EqualTo(original));
        }

        [Test]
        public void InternalArray_IsSameReferenceAsInputOnSuccess()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";
            byte[] bytes = NewBytes(8);
            byte[] originalReference = bytes;

            CaptureRunMarkerWriteOperation op = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes);

            Assert.That(bytes, Is.Null);
            Assert.That(GetFieldBytes(op, "_canonicalBytes"), Is.SameAs(originalReference));
        }

        [Test]
        public void GetCanonicalBytes_ReturnsDistinctCopies()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";
            byte[] bytes = NewBytes(8);

            CaptureRunMarkerWriteOperation op = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes);

            Assert.That(op.GetCanonicalBytes(), Is.Not.SameAs(op.GetCanonicalBytes()));
        }

        [Test]
        public void MutatingReturnedCopy_DoesNotAffectNext()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";
            byte[] bytes = NewBytes(8);
            byte[] original = (byte[])bytes.Clone();

            CaptureRunMarkerWriteOperation op = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes);

            byte[] copy = op.GetCanonicalBytes();
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = 0;
            }

            Assert.That(op.GetCanonicalBytes(), Is.EqualTo(original));
        }

        [Test]
        public void InputPaths_NotChanged()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";
            byte[] bytes = NewBytes(8);

            CaptureRunMarkerWriteOperation op = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization, temporaryPath, finalPath, ref bytes);

            Assert.That(finalPath, Is.EqualTo(InitializationFinalPath()));
            Assert.That(temporaryPath, Is.EqualTo(InitializationTemporaryPath()));
            Assert.That(op.TemporaryPath, Is.EqualTo(InitializationTemporaryPath()));
            Assert.That(op.FinalPath, Is.EqualTo(InitializationFinalPath()));
        }

        // ---- Shape ----

        [Test]
        public void NoPublicConstructorOrSetter()
        {
            Type type = typeof(CaptureRunMarkerWriteOperation);

            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, "No public constructor.");
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public methods.");

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
            }
        }

        [Test]
        public void Sealed_NotDisposable_NotUnityObject()
        {
            Type type = typeof(CaptureRunMarkerWriteOperation);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Fields_AreExactlyFiveReadonly()
        {
            Type type = typeof(CaptureRunMarkerWriteOperation);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(5), "Must hold role, kind, two paths, and one byte array.");

            int roleFields = 0;
            int kindFields = 0;
            int stringFields = 0;
            int byteArrayFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunRootRole))
                {
                    roleFields++;
                }
                else if (field.FieldType == typeof(CaptureRunMarkerKind))
                {
                    kindFields++;
                }
                else if (field.FieldType == typeof(string))
                {
                    stringFields++;
                }
                else if (field.FieldType == typeof(byte[]))
                {
                    byteArrayFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(roleFields, Is.EqualTo(1));
            Assert.That(kindFields, Is.EqualTo(1));
            Assert.That(stringFields, Is.EqualTo(2));
            Assert.That(byteArrayFields, Is.EqualTo(1));
        }

        [Test]
        public void NoCollectionOrMutableStaticState()
        {
            Type type = typeof(CaptureRunMarkerWriteOperation);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(byte[]))
                {
                    continue;
                }

                bool isCollection = typeof(IEnumerable).IsAssignableFrom(field.FieldType) && field.FieldType != typeof(string);
                Assert.That(isCollection, Is.False, field.Name + " must not be a collection.");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoFilesystemCodecHashGeneratorFactoryUnityClockRandom()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunMarkerWriteOperation.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("Stream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationMarkerCodec"));
            Assert.That(source, Does.Not.Contain("CaptureRunReadyMarkerCodec"));
            Assert.That(source, Does.Not.Contain("SerializeCanonical"));
            Assert.That(source, Does.Not.Contain("DeserializeCanonical"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("SHA-256"));
            Assert.That(source, Does.Not.Contain("System.Security.Cryptography"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationIdGenerator"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationPlanFactory"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationDocumentSetFactory"));
            Assert.That(source, Does.Not.Contain("CaptureRunMarkerBindingFactory"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Debug."));
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunMarkerWriteOperationTests).Assembly.Location);
            while (dir != null)
            {
                string candidate = Path.Combine(dir, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null)
                {
                    break;
                }

                dir = parent.FullName;
            }

            Assert.Fail("Source file not found: " + relativePath);
            return null;
        }
    }
}
