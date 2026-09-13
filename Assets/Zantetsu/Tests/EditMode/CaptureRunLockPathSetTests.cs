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

        private static object MakeRootLayout(string baseRoot, long testRunId = 1)
        {
            ConstructorInfo ctor = GetRootLayoutType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { baseRoot, testRunId });
        }

        private static object MakeLayout(long testRunId = 1)
        {
            return MakeRootLayout(IsWindows ? "C:\\staging" : "/alpha", testRunId);
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
            object layout = MakeLayout(42);
            object set = MakeLockPathSet(layout);

            string stagingBase = (string)GetProperty(layout, "TrustedBaseRoot");

            Assert.That((string)GetProperty(set, "LockPath"), Is.EqualTo(stagingBase + Separator + ".locks" + Separator + "run-42.lock"));
        }

        [Test]
        public void LockPaths_InsideLocksDirectory()
        {
            object layout = MakeLayout();
            object set = MakeLockPathSet(layout);

            string stagingBase = (string)GetProperty(layout, "TrustedBaseRoot");

            string stagingLock = (string)GetProperty(set, "LockPath");

            Assert.That(Path.GetDirectoryName(stagingLock), Is.EqualTo(stagingBase + Separator + ".locks"));
        }

        [Test]
        public void LockPaths_NotUnderRunRoot()
        {
            object layout = MakeLayout();
            object set = MakeLockPathSet(layout);

            string stagingRunRoot = (string)GetProperty(layout, "RunRoot");

            Assert.That(((string)GetProperty(set, "LockPath")).StartsWith(stagingRunRoot + Separator, StringComparison.OrdinalIgnoreCase), Is.False);
        }

        [Test]
        public void LockPaths_Basename()
        {
            object layout = MakeLayout();
            object set = MakeLockPathSet(layout);

            Assert.That(Path.GetFileName((string)GetProperty(set, "LockPath")), Is.EqualTo("run-1.lock"));
        }

        [Test]
        public void LockPaths_LongMaxValueShortestDecimal()
        {
            object layout = MakeLayout(long.MaxValue);
            object set = MakeLockPathSet(layout);

            Assert.That(Path.GetFileName((string)GetProperty(set, "LockPath")), Is.EqualTo("run-9223372036854775807.lock"));
        }

        // ---- Ordering ----

        // ---- Ownership / shape ----

        [Test]
        public void RootLayout_HeldByReference()
        {
            object layout = MakeLayout();
            object set = MakeLockPathSet(layout);

            Assert.That(GetProperty(set, "RootLayout"), Is.SameAs(layout));
        }

        [Test]
        public void DoesNotModifyRootLayout()
        {
            object layout = MakeLayout();
            MakeLockPathSet(layout);

            Assert.That((long)GetProperty(layout, "TestRunId"), Is.EqualTo(1));
        }

        [Test]
        public void MinimalFields_NoDuplicatePathFields()
        {
            Type type = GetLockPathSetType();
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(2), "Lock path set must hold exactly the layout and the lock path.");

            int layoutFields = 0;
            int stringFields = 0;
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
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(layoutFields, Is.EqualTo(1));
            Assert.That(stringFields, Is.EqualTo(1));

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
