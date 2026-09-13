using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunRootLayoutTests
    {
        private const string Unspecified = "\u0001";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string StagingBaseRoot() => IsWindows ? "C:\\staging" : "/staging";


        private static string Separator => Path.DirectorySeparatorChar.ToString();

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetLayoutType() => GetTypeFromAssembly("CaptureRunRootLayout");

        private static object GetProperty(object target, string name)
        {
            PropertyInfo prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, target.GetType().Name + "." + name + " property not found.");
            return prop.GetValue(target);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return tie.InnerException;
            }

            return ex;
        }

        private static object MakeLayout(string baseRoot = Unspecified, long testRunId = 1)
        {
            if (baseRoot == Unspecified)
            {
                baseRoot = StagingBaseRoot();
            }

            ConstructorInfo ctor = GetLayoutType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { baseRoot, testRunId });
        }

        private static Exception MakeLayoutException(string baseRoot = Unspecified, long testRunId = 1)
        {
            try
            {
                MakeLayout(baseRoot, testRunId);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static string ComputeSha256(string value)
        {
            byte[] utf8 = new UTF8Encoding(false).GetBytes(value);
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(utf8));
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            const string hex = "0123456789abcdef";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0xF];
            }

            return new string(chars);
        }

        // ---- Argument validation ----

        [Test]
        public void NullAndRange_ParamName()
        {
            Exception nullBase = MakeLayoutException(null);
            Assert.That(nullBase, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullBase).ParamName, Is.EqualTo("trustedBaseRoot"));

            Exception zero = MakeLayoutException(testRunId: 0);
            Assert.That(zero, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)zero).ParamName, Is.EqualTo("testRunId"));

            Exception negative = MakeLayoutException(testRunId: -1);
            Assert.That(negative, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)negative).ParamName, Is.EqualTo("testRunId"));
        }

        [Test]
        public void EmptyOrWhitespace_Rejected()
        {
            AssertInvalidArgument(MakeLayoutException(""), "trustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException("   "), "trustedBaseRoot");
        }

        [Test]
        public void RelativePath_Rejected()
        {
            AssertInvalidArgument(MakeLayoutException("staging"), "trustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException("staging/child"), "trustedBaseRoot");
        }

        [Test]
        public void DriveRelativeUncDevice_Rejected()
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                Assert.Ignore("Windows-specific path forms.");
                return;
            }

            AssertInvalidArgument(MakeLayoutException("C:relative"), "trustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException("\\rooted"), "trustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException("\\\\server\\share"), "trustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException("\\\\?\\C:\\device"), "trustedBaseRoot");
        }

        // ---- Normalization ----

        [Test]
        public void TrailingSeparator_Normalized()
        {
            object layout = MakeLayout(StagingBaseRoot() + Separator);
            Assert.That((string)GetProperty(layout, "TrustedBaseRoot"), Is.EqualTo(StagingBaseRoot()));
        }

        [Test]
        public void FilesystemRoot_SeparatorPreserved()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Windows-specific filesystem root case.");
                return;
            }

            object rootLayout = MakeLayout("C:\\");
            Assert.That((string)GetProperty(rootLayout, "TrustedBaseRoot"), Is.EqualTo("C:\\"));
        }

        [Test]
        public void DotDot_Normalized()
        {
            string stagingInput = StagingBaseRoot() + Separator + "child" + Separator + ".." + Separator + "final";
            string stagingExpected = StagingBaseRoot() + Separator + "final";

            object layout = MakeLayout(stagingInput);
            Assert.That((string)GetProperty(layout, "TrustedBaseRoot"), Is.EqualTo(stagingExpected));
        }

        [Test]
        public void SeparatorUnified_Windows()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Windows-specific alternate separator case.");
                return;
            }

            object layout = MakeLayout("C:/staging");
            Assert.That((string)GetProperty(layout, "TrustedBaseRoot"), Is.EqualTo("C:\\staging"));
        }

        // ---- Fixed relative path ----

        [Test]
        public void RunRelativePath_Fixed()
        {
            object layout = MakeLayout(testRunId: 123);
            Assert.That((string)GetProperty(layout, "RunRelativePath"), Is.EqualTo("runs/run-123"));
        }

        [Test]
        public void RunRelativePath_LongMaxValueShortestDecimal()
        {
            object layout = MakeLayout(testRunId: long.MaxValue);
            Assert.That((string)GetProperty(layout, "RunRelativePath"), Is.EqualTo("runs/run-9223372036854775807"));
        }

        [Test]
        public void RunRoots_UnderBase()
        {
            object layout = MakeLayout();
            Assert.That((string)GetProperty(layout, "RunRoot"), Is.EqualTo(StagingBaseRoot() + Separator + "runs" + Separator + "run-1"));
        }

        // ---- Base relationship ----

        // ---- Root hash ----

        [Test]
        public void RootHash_LowercaseHex64()
        {
            object layout = MakeLayout();

            Assert.That((string)GetProperty(layout, "RunRootSha256"), Does.Match("^[0-9a-f]{64}$"));
        }

        [Test]
        public void RootHash_MatchesIndependentSha256()
        {
            object layout = MakeLayout();

            string stagingRunRoot = (string)GetProperty(layout, "RunRoot");

            Assert.That((string)GetProperty(layout, "RunRootSha256"), Is.EqualTo(ComputeSha256(stagingRunRoot)));
        }

        [Test]
        public void RootHash_ChangesWithOneCharDifference()
        {
            object a = MakeLayout();
            object b = MakeLayout(StagingBaseRoot() + "2");

            Assert.That((string)GetProperty(a, "RunRootSha256"), Is.Not.EqualTo((string)GetProperty(b, "RunRootSha256")));
        }

        // ---- IsValid recomputation ----

        [Test]
        public void IsValid_True_ForNormalLayout()
        {
            object layout = MakeLayout();
            Assert.That((bool)GetProperty(layout, "IsValid"), Is.True);
        }

        [Test]
        public void IsValid_False_WhenFieldsNull_NoException()
        {
            object layout = FormatterServices.GetUninitializedObject(GetLayoutType());
            Assert.That((bool)GetProperty(layout, "IsValid"), Is.False);
        }

        [Test]
        public void IsValid_False_WhenTestRunIdNotPositive()
        {
            object layout = MakeLayout();
            SetField(layout, "_testRunId", 0L);
            Assert.That((bool)GetProperty(layout, "IsValid"), Is.False);
        }

        [Test]
        public void IsValid_False_WhenTrustedBaseNotNormalized()
        {
            object layout = MakeLayout();
            SetField(layout, "_trustedBaseRoot", StagingBaseRoot() + Separator);
            Assert.That((bool)GetProperty(layout, "IsValid"), Is.False);
        }

        [Test]
        public void IsValid_False_WhenRunRelativePathChanged()
        {
            object layout = MakeLayout();
            SetField(layout, "_runRelativePath", "runs/run-999");
            Assert.That((bool)GetProperty(layout, "IsValid"), Is.False);
        }

        [Test]
        public void IsValid_False_WhenRunRootOutsideBase()
        {
            string outside = IsWindows ? "X:\\outside-run" : "/outside-run";

            object layout = MakeLayout();
            SetField(layout, "_runRoot", outside);
            Assert.That((bool)GetProperty(layout, "IsValid"), Is.False);

        }

        [Test]
        public void IsValid_False_WhenRootHashChanged()
        {
            object layout = MakeLayout();
            SetField(layout, "_runRootSha256", new string('0', 64));
            Assert.That((bool)GetProperty(layout, "IsValid"), Is.False);
        }

        [Test]
        public void IsValid_False_WhenBaseUncOrDevice()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Windows-specific path forms.");
                return;
            }

            object unc = MakeLayout();
            SetField(unc, "_trustedBaseRoot", "\\\\server\\share");
            Assert.That((bool)GetProperty(unc, "IsValid"), Is.False);

            object device = MakeLayout();
            SetField(device, "_trustedBaseRoot", "\\\\?\\C:\\device");
            Assert.That((bool)GetProperty(device, "IsValid"), Is.False);
        }

        // ---- Purity / shape ----

        [Test]
        public void DoesNotModifyInputs()
        {
            string baseRoot = StagingBaseRoot();

            MakeLayout(baseRoot);

            Assert.That(baseRoot, Is.EqualTo(StagingBaseRoot()));
        }

        [Test]
        public void IndependentInstancesPerCall()
        {
            object first = MakeLayout();
            object second = MakeLayout();

            Assert.That(second, Is.Not.SameAs(first));
        }

        [Test]
        public void NoPublicApi_Sealed_NotDisposable_NotUnityObject()
        {
            Type type = GetLayoutType();

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, "No public constructor.");
            Assert.That(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public properties.");
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public methods.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void NoMutableStateBeyondStringsAndLong()
        {
            Type type = GetLayoutType();

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                bool allowed = field.FieldType == typeof(string) || field.FieldType == typeof(long);
                Assert.That(allowed, Is.True, field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        [Test]
        public void Source_NoFileDirectoryFileStreamUnityRandomClock()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunRootLayout.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Debug."));
        }

        private static void AssertInvalidArgument(Exception ex, string paramName)
        {
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo(paramName));
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunRootLayoutTests).Assembly.Location);
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
