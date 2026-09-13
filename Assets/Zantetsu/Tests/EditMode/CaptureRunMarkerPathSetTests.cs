using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunMarkerPathSetTests
    {
        private static string Separator => Path.DirectorySeparatorChar.ToString();

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            string staging = Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging";
            return new CaptureRunRootLayout(staging, testRunId);
        }

        // ---- Construction ----

        [Test]
        public void Null_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => new CaptureRunMarkerPathSet(null));
            Assert.That(ex.ParamName, Is.EqualTo("rootLayout"));
        }

        [Test]
        public void Paths_Exact()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(layout);

            string stagingRoot = layout.RunRoot;
            string sep = Separator;

            Assert.That(set.InitializationTemporaryPath, Is.EqualTo(stagingRoot + sep + "run.init.tmp"));
            Assert.That(set.InitializationPath, Is.EqualTo(stagingRoot + sep + "run.init"));
            Assert.That(set.ReadyTemporaryPath, Is.EqualTo(stagingRoot + sep + "run.ready.tmp"));
            Assert.That(set.ReadyPath, Is.EqualTo(stagingRoot + sep + "run.ready"));

        }

        [Test]
        public void Basenames_ExactCase()
        {
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(MakeLayout());

            Assert.That(Path.GetFileName(set.InitializationTemporaryPath), Is.EqualTo("run.init.tmp"));
            Assert.That(Path.GetFileName(set.InitializationPath), Is.EqualTo("run.init"));
            Assert.That(Path.GetFileName(set.ReadyTemporaryPath), Is.EqualTo("run.ready.tmp"));
            Assert.That(Path.GetFileName(set.ReadyPath), Is.EqualTo("run.ready"));
        }

        [Test]
        public void Paths_AreDirectChildrenOfRunRoots()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(layout);

            Assert.That(Path.GetDirectoryName(set.InitializationTemporaryPath), Is.EqualTo(layout.RunRoot));
            Assert.That(Path.GetDirectoryName(set.InitializationPath), Is.EqualTo(layout.RunRoot));
            Assert.That(Path.GetDirectoryName(set.ReadyTemporaryPath), Is.EqualTo(layout.RunRoot));
            Assert.That(Path.GetDirectoryName(set.ReadyPath), Is.EqualTo(layout.RunRoot));
        }

        [Test]
        public void TemporaryAndFinal_Correspondence()
        {
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(MakeLayout());

            Assert.That(set.InitializationTemporaryPath, Is.EqualTo(set.InitializationPath + ".tmp"));
            Assert.That(set.ReadyTemporaryPath, Is.EqualTo(set.ReadyPath + ".tmp"));
        }

        [Test]
        public void RootLayout_HeldByReference()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(layout);

            Assert.That(set.RootLayout, Is.SameAs(layout));
        }

        [Test]
        public void LongMaxValue_DerivesCorrectly()
        {
            CaptureRunRootLayout layout = MakeLayout(long.MaxValue);
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(layout);

            Assert.That(layout.RunRoot, Does.Contain("9223372036854775807"));
            Assert.That(Path.GetFileName(set.InitializationPath), Is.EqualTo("run.init"));
            Assert.That(set.InitializationPath.StartsWith(layout.RunRoot, StringComparison.Ordinal), Is.True);
        }

        [Test]
        public void Separator_Integrity()
        {
            if (Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar)
            {
                Assert.Pass("No alternate separator on this platform.");
                return;
            }

            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(MakeLayout());
            Assert.That(set.InitializationPath, Does.Not.Contain(Path.AltDirectorySeparatorChar.ToString()));
        }

        [Test]
        public void Constructible_WithoutExistingDirectories()
        {
            string staging = Path.DirectorySeparatorChar == '\\'
                ? "C:\\zantetsuken-marker-does-not-exist-staging"
                : "/zantetsuken-marker-does-not-exist-staging";

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, 1);
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(layout);

            Assert.That(set, Is.Not.Null);
            Assert.That(Directory.Exists(layout.RunRoot), Is.False);
            Assert.That(File.Exists(layout.RunRoot), Is.False);
        }

        [Test]
        public void Deterministic_ButDistinctInstances()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet first = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerPathSet second = new CaptureRunMarkerPathSet(layout);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.InitializationTemporaryPath, Is.EqualTo(first.InitializationTemporaryPath));
            Assert.That(second.InitializationPath, Is.EqualTo(first.InitializationPath));
            Assert.That(second.ReadyTemporaryPath, Is.EqualTo(first.ReadyTemporaryPath));
            Assert.That(second.ReadyPath, Is.EqualTo(first.ReadyPath));
        }

        // ---- Shape ----

        [Test]
        public void NoPublicApi_Sealed_NotDisposable_NotUnityObject()
        {
            Type type = typeof(CaptureRunMarkerPathSet);

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
        public void Fields_AreRootLayoutAndFourReadonlyStrings()
        {
            Type type = typeof(CaptureRunMarkerPathSet);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(5), "Must hold exactly the layout reference and four path strings.");

            int layoutFields = 0;
            int stringFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunRootLayout))
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
            Assert.That(stringFields, Is.EqualTo(4));

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        [Test]
        public void Source_NoFilesystemPInvokeUnityRandomClock()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunMarkerPathSet.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunMarkerPathSetTests).Assembly.Location);
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
