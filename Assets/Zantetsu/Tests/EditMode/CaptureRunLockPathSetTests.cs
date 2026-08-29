using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunLockPathSetTests
    {
        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string Separator => Path.DirectorySeparatorChar.ToString();

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetLockPathSetType() => GetTypeFromAssembly("CaptureRunLockPathSet");

        private static Type GetRootLayoutType() => GetTypeFromAssembly("CaptureRunRootLayout");

        private static Type GetRoleType() => GetTypeFromAssembly("CaptureRunRootRole");

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

        private static object MakeRootLayout(string stagingBase, string finalBase, long testRunId = 1)
        {
            ConstructorInfo ctor = GetRootLayoutType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(string), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { stagingBase, finalBase, testRunId });
        }

        // A valid root layout where staging sorts before final, or after.
        private static object MakeOrderedLayout(bool stagingFirst, long testRunId = 1)
        {
            if (IsWindows)
            {
                return stagingFirst
                    ? MakeRootLayout("C:\\staging", "D:\\final", testRunId)
                    : MakeRootLayout("D:\\staging", "C:\\final", testRunId);
            }

            return stagingFirst
                ? MakeRootLayout("/alpha", "/beta", testRunId)
                : MakeRootLayout("/beta", "/alpha", testRunId);
        }

        private static object MakeLockPathSet(object rootLayout)
        {
            ConstructorInfo ctor = GetLockPathSetType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRootLayoutType() },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { rootLayout });
        }

        private static Exception MakeLockPathSetException(object rootLayout)
        {
            try
            {
                MakeLockPathSet(rootLayout);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static bool StagingComesFirst(string stagingPath, string finalPath)
        {
            MethodInfo method = GetLockPathSetType().GetMethod("StagingComesFirst", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "StagingComesFirst helper not found.");
            return (bool)method.Invoke(null, new object[] { stagingPath, finalPath });
        }

        // ---- Construction ----

        [Test]
        public void NullLayout_Rejected()
        {
            Exception ex = MakeLockPathSetException(null);
            Assert.That(ex, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("rootLayout"));
        }

        [Test]
        public void LockPaths_Exact()
        {
            object layout = MakeOrderedLayout(stagingFirst: true, testRunId: 42);
            object set = MakeLockPathSet(layout);

            string stagingBase = (string)GetProperty(layout, "StagingTrustedBaseRoot");
            string finalBase = (string)GetProperty(layout, "FinalTrustedBaseRoot");

            Assert.That((string)GetProperty(set, "StagingLockPath"), Is.EqualTo(stagingBase + Separator + ".locks" + Separator + "run-42.lock"));
            Assert.That((string)GetProperty(set, "FinalLockPath"), Is.EqualTo(finalBase + Separator + ".locks" + Separator + "run-42.lock"));
        }

        [Test]
        public void LockPaths_InsideLocksDirectory()
        {
            object layout = MakeOrderedLayout(stagingFirst: true);
            object set = MakeLockPathSet(layout);

            string stagingBase = (string)GetProperty(layout, "StagingTrustedBaseRoot");
            string finalBase = (string)GetProperty(layout, "FinalTrustedBaseRoot");

            string stagingLock = (string)GetProperty(set, "StagingLockPath");
            string finalLock = (string)GetProperty(set, "FinalLockPath");

            Assert.That(Path.GetDirectoryName(stagingLock), Is.EqualTo(stagingBase + Separator + ".locks"));
            Assert.That(Path.GetDirectoryName(finalLock), Is.EqualTo(finalBase + Separator + ".locks"));
        }

        [Test]
        public void LockPaths_NotUnderRunRoot()
        {
            object layout = MakeOrderedLayout(stagingFirst: true);
            object set = MakeLockPathSet(layout);

            string stagingRunRoot = (string)GetProperty(layout, "StagingRunRoot");
            string finalRunRoot = (string)GetProperty(layout, "FinalRunRoot");

            Assert.That(((string)GetProperty(set, "StagingLockPath")).StartsWith(stagingRunRoot + Separator, StringComparison.OrdinalIgnoreCase), Is.False);
            Assert.That(((string)GetProperty(set, "FinalLockPath")).StartsWith(finalRunRoot + Separator, StringComparison.OrdinalIgnoreCase), Is.False);
        }

        [Test]
        public void LockPaths_Basename()
        {
            object layout = MakeOrderedLayout(stagingFirst: true);
            object set = MakeLockPathSet(layout);

            Assert.That(Path.GetFileName((string)GetProperty(set, "StagingLockPath")), Is.EqualTo("run-1.lock"));
            Assert.That(Path.GetFileName((string)GetProperty(set, "FinalLockPath")), Is.EqualTo("run-1.lock"));
        }

        [Test]
        public void LockPaths_LongMaxValueShortestDecimal()
        {
            object layout = MakeOrderedLayout(stagingFirst: true, testRunId: long.MaxValue);
            object set = MakeLockPathSet(layout);

            Assert.That(Path.GetFileName((string)GetProperty(set, "StagingLockPath")), Is.EqualTo("run-9223372036854775807.lock"));
        }

        // ---- Ordering ----

        [Test]
        public void StagingFirst_OrderAndRoles()
        {
            object layout = MakeOrderedLayout(stagingFirst: true);
            object set = MakeLockPathSet(layout);

            Assert.That((string)GetProperty(set, "FirstLockPath"), Is.EqualTo((string)GetProperty(set, "StagingLockPath")));
            Assert.That((string)GetProperty(set, "SecondLockPath"), Is.EqualTo((string)GetProperty(set, "FinalLockPath")));
            Assert.That(GetProperty(set, "FirstRootRole"), Is.EqualTo(Enum.Parse(GetRoleType(), "Staging")));
            Assert.That(GetProperty(set, "SecondRootRole"), Is.EqualTo(Enum.Parse(GetRoleType(), "Final")));
        }

        [Test]
        public void FinalFirst_OrderAndRoles()
        {
            object layout = MakeOrderedLayout(stagingFirst: false);
            object set = MakeLockPathSet(layout);

            Assert.That((string)GetProperty(set, "FirstLockPath"), Is.EqualTo((string)GetProperty(set, "FinalLockPath")));
            Assert.That((string)GetProperty(set, "SecondLockPath"), Is.EqualTo((string)GetProperty(set, "StagingLockPath")));
            Assert.That(GetProperty(set, "FirstRootRole"), Is.EqualTo(Enum.Parse(GetRoleType(), "Final")));
            Assert.That(GetProperty(set, "SecondRootRole"), Is.EqualTo(Enum.Parse(GetRoleType(), "Staging")));
        }

        [Test]
        public void Comparison_OrdinalIgnoreCaseFirst()
        {
            // OrdinalIgnoreCase: 'a' < 'b'. Ordinal would rank 'B' before 'a'.
            Assert.That(StagingComesFirst("a", "B"), Is.True);
            Assert.That(StagingComesFirst("B", "a"), Is.False);
        }

        [Test]
        public void Comparison_OrdinalTieBreak()
        {
            // Ignore-case equal: "A" vs "a". Ordinal ranks 'A' (65) before 'a' (97).
            Assert.That(StagingComesFirst("A", "a"), Is.True);
            Assert.That(StagingComesFirst("a", "A"), Is.False);
        }

        [Test]
        public void TwoPaths_NeverCollapsed()
        {
            object set = MakeLockPathSet(MakeOrderedLayout(stagingFirst: true));

            string first = (string)GetProperty(set, "FirstLockPath");
            string second = (string)GetProperty(set, "SecondLockPath");

            Assert.That(string.Equals(first, second, StringComparison.OrdinalIgnoreCase), Is.False);
        }

        [Test]
        public void FirstSecond_ReferenceStoredPaths()
        {
            object set = MakeLockPathSet(MakeOrderedLayout(stagingFirst: true));

            string staging = (string)GetProperty(set, "StagingLockPath");
            string final = (string)GetProperty(set, "FinalLockPath");
            string first = (string)GetProperty(set, "FirstLockPath");
            string second = (string)GetProperty(set, "SecondLockPath");

            bool stagingFirst = Equals(GetProperty(set, "FirstRootRole"), Enum.Parse(GetRoleType(), "Staging"));
            if (stagingFirst)
            {
                Assert.That(first, Is.SameAs(staging));
                Assert.That(second, Is.SameAs(final));
            }
            else
            {
                Assert.That(first, Is.SameAs(final));
                Assert.That(second, Is.SameAs(staging));
            }
        }

        // ---- Ownership / shape ----

        [Test]
        public void RootLayout_HeldByReference()
        {
            object layout = MakeOrderedLayout(stagingFirst: true);
            object set = MakeLockPathSet(layout);

            Assert.That(GetProperty(set, "RootLayout"), Is.SameAs(layout));
        }

        [Test]
        public void DoesNotModifyRootLayout()
        {
            object layout = MakeOrderedLayout(stagingFirst: true);
            MakeLockPathSet(layout);

            Assert.That((long)GetProperty(layout, "TestRunId"), Is.EqualTo(1));
        }

        [Test]
        public void MinimalFields_NoDuplicatePathFields()
        {
            Type type = GetLockPathSetType();
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(4), "Lock path set must hold exactly four fields.");

            int layoutFields = 0;
            int stringFields = 0;
            int boolFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == GetRootLayoutType())
                {
                    layoutFields++;
                }
                else if (field.FieldType == typeof(string))
                {
                    stringFields++;
                }
                else if (field.FieldType == typeof(bool))
                {
                    boolFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(layoutFields, Is.EqualTo(1));
            Assert.That(stringFields, Is.EqualTo(2));
            Assert.That(boolFields, Is.EqualTo(1));

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        [Test]
        public void NoPublicApi_Sealed_NotDisposable_NotUnityObject()
        {
            Type type = GetLockPathSetType();

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
        public void Source_NoFileDirectoryFileStreamHandleRandomClock()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunLockPathSet.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("SafeHandle"));
            Assert.That(source, Does.Not.Contain("FileShare"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunLockPathSetTests).Assembly.Location);
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
