using System;
using System.IO;
using System.Reflection;
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

        private static string FinalBaseRoot() => IsWindows ? "D:\\final" : "/final";

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

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return tie.InnerException;
            }

            return ex;
        }

        private static object MakeLayout(string stagingBase = Unspecified, string finalBase = Unspecified, long testRunId = 1)
        {
            if (stagingBase == Unspecified)
            {
                stagingBase = StagingBaseRoot();
            }

            if (finalBase == Unspecified)
            {
                finalBase = FinalBaseRoot();
            }

            ConstructorInfo ctor = GetLayoutType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(string), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { stagingBase, finalBase, testRunId });
        }

        private static Exception MakeLayoutException(string stagingBase = Unspecified, string finalBase = Unspecified, long testRunId = 1)
        {
            try
            {
                MakeLayout(stagingBase, finalBase, testRunId);
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
            Exception nullStaging = MakeLayoutException(null, FinalBaseRoot());
            Assert.That(nullStaging, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullStaging).ParamName, Is.EqualTo("stagingTrustedBaseRoot"));

            Exception nullFinal = MakeLayoutException(StagingBaseRoot(), null);
            Assert.That(nullFinal, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullFinal).ParamName, Is.EqualTo("finalTrustedBaseRoot"));

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
            AssertInvalidArgument(MakeLayoutException("", FinalBaseRoot()), "stagingTrustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException("   ", FinalBaseRoot()), "stagingTrustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException(StagingBaseRoot(), ""), "finalTrustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException(StagingBaseRoot(), "\t"), "finalTrustedBaseRoot");
        }

        [Test]
        public void RelativePath_Rejected()
        {
            AssertInvalidArgument(MakeLayoutException("staging", FinalBaseRoot()), "stagingTrustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException("staging/child", FinalBaseRoot()), "stagingTrustedBaseRoot");
        }

        [Test]
        public void DriveRelativeUncDevice_Rejected()
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                Assert.Ignore("Windows-specific path forms.");
                return;
            }

            AssertInvalidArgument(MakeLayoutException("C:relative", FinalBaseRoot()), "stagingTrustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException("\\rooted", FinalBaseRoot()), "stagingTrustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException("\\\\server\\share", FinalBaseRoot()), "stagingTrustedBaseRoot");
            AssertInvalidArgument(MakeLayoutException("\\\\?\\C:\\device", FinalBaseRoot()), "stagingTrustedBaseRoot");
        }

        // ---- Normalization ----

        [Test]
        public void TrailingSeparator_Normalized()
        {
            object layout = MakeLayout(StagingBaseRoot() + Separator, FinalBaseRoot() + Separator);
            Assert.That((string)GetProperty(layout, "StagingTrustedBaseRoot"), Is.EqualTo(StagingBaseRoot()));
            Assert.That((string)GetProperty(layout, "FinalTrustedBaseRoot"), Is.EqualTo(FinalBaseRoot()));
        }

        [Test]
        public void FilesystemRoot_SeparatorPreserved()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Windows-specific filesystem root case.");
                return;
            }

            object rootLayout = MakeLayout("C:\\", "D:\\");
            Assert.That((string)GetProperty(rootLayout, "StagingTrustedBaseRoot"), Is.EqualTo("C:\\"));
            Assert.That((string)GetProperty(rootLayout, "FinalTrustedBaseRoot"), Is.EqualTo("D:\\"));
        }

        [Test]
        public void DotDot_Normalized()
        {
            string stagingInput = StagingBaseRoot() + Separator + "child" + Separator + ".." + Separator + "final";
            string finalInput = FinalBaseRoot() + Separator + "child" + Separator + ".." + Separator + "final";
            string stagingExpected = StagingBaseRoot() + Separator + "final";
            string finalExpected = FinalBaseRoot() + Separator + "final";

            object layout = MakeLayout(stagingInput, finalInput);
            Assert.That((string)GetProperty(layout, "StagingTrustedBaseRoot"), Is.EqualTo(stagingExpected));
            Assert.That((string)GetProperty(layout, "FinalTrustedBaseRoot"), Is.EqualTo(finalExpected));
        }

        [Test]
        public void SeparatorUnified_Windows()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Windows-specific alternate separator case.");
                return;
            }

            object layout = MakeLayout("C:/staging", "D:/final");
            Assert.That((string)GetProperty(layout, "StagingTrustedBaseRoot"), Is.EqualTo("C:\\staging"));
            Assert.That((string)GetProperty(layout, "FinalTrustedBaseRoot"), Is.EqualTo("D:\\final"));
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
            Assert.That((string)GetProperty(layout, "StagingRunRoot"), Is.EqualTo(StagingBaseRoot() + Separator + "runs" + Separator + "run-1"));
            Assert.That((string)GetProperty(layout, "FinalRunRoot"), Is.EqualTo(FinalBaseRoot() + Separator + "runs" + Separator + "run-1"));
        }

        // ---- Base relationship ----

        [Test]
        public void SameBase_Rejected()
        {
            Exception ex = MakeLayoutException(StagingBaseRoot(), StagingBaseRoot());
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalTrustedBaseRoot"));
        }

        [Test]
        public void CaseOnlyDifference_Rejected()
        {
            string upper = IsWindows ? "C:\\STAGING" : "/STAGING";
            Exception ex = MakeLayoutException(upper, StagingBaseRoot());
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalTrustedBaseRoot"));
        }

        [Test]
        public void StagingAncestorOfFinal_Rejected()
        {
            string ancestor = IsWindows ? "C:\\root" : "/root";
            Exception ex = MakeLayoutException(ancestor, ancestor + Separator + "final");
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalTrustedBaseRoot"));
        }

        [Test]
        public void FinalAncestorOfStaging_Rejected()
        {
            string ancestor = IsWindows ? "C:\\root" : "/root";
            Exception ex = MakeLayoutException(ancestor + Separator + "staging", ancestor);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalTrustedBaseRoot"));
        }

        [Test]
        public void CommonPrefixSiblings_Accepted()
        {
            string foo = IsWindows ? "C:\\foo" : "/foo";
            string foobar = IsWindows ? "C:\\foobar" : "/foobar";
            object layout = MakeLayout(foo, foobar);
            Assert.That(layout, Is.Not.Null);
        }

        [Test]
        public void IndependentBases_Accepted()
        {
            // Windows: distinct volumes; non-Windows: distinct sibling roots.
            object layout = MakeLayout();
            Assert.That(layout, Is.Not.Null);
        }

        // ---- Root hash ----

        [Test]
        public void RootHash_LowercaseHex64()
        {
            object layout = MakeLayout();

            Assert.That((string)GetProperty(layout, "StagingRunRootSha256"), Does.Match("^[0-9a-f]{64}$"));
            Assert.That((string)GetProperty(layout, "FinalRunRootSha256"), Does.Match("^[0-9a-f]{64}$"));
        }

        [Test]
        public void RootHash_MatchesIndependentSha256()
        {
            object layout = MakeLayout();

            string stagingRunRoot = (string)GetProperty(layout, "StagingRunRoot");
            string finalRunRoot = (string)GetProperty(layout, "FinalRunRoot");

            Assert.That((string)GetProperty(layout, "StagingRunRootSha256"), Is.EqualTo(ComputeSha256(stagingRunRoot)));
            Assert.That((string)GetProperty(layout, "FinalRunRootSha256"), Is.EqualTo(ComputeSha256(finalRunRoot)));
        }

        [Test]
        public void RootHash_ChangesWithOneCharDifference()
        {
            object a = MakeLayout();
            object b = MakeLayout(StagingBaseRoot() + "2", FinalBaseRoot());

            Assert.That((string)GetProperty(a, "StagingRunRootSha256"), Is.Not.EqualTo((string)GetProperty(b, "StagingRunRootSha256")));
        }

        // ---- Purity / shape ----

        [Test]
        public void DoesNotModifyInputs()
        {
            string staging = StagingBaseRoot();
            string final = FinalBaseRoot();

            MakeLayout(staging, final);

            Assert.That(staging, Is.EqualTo(StagingBaseRoot()));
            Assert.That(final, Is.EqualTo(FinalBaseRoot()));
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
