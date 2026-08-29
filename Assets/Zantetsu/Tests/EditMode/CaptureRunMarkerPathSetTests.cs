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
            string final = Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final";
            return new CaptureRunRootLayout(staging, final, testRunId);
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

            string stagingRoot = layout.StagingRunRoot;
            string finalRoot = layout.FinalRunRoot;
            string sep = Separator;

            Assert.That(set.StagingInitializationTemporaryPath, Is.EqualTo(stagingRoot + sep + "run.init.tmp"));
            Assert.That(set.StagingInitializationPath, Is.EqualTo(stagingRoot + sep + "run.init"));
            Assert.That(set.StagingReadyTemporaryPath, Is.EqualTo(stagingRoot + sep + "run.ready.tmp"));
            Assert.That(set.StagingReadyPath, Is.EqualTo(stagingRoot + sep + "run.ready"));

            Assert.That(set.FinalInitializationTemporaryPath, Is.EqualTo(finalRoot + sep + "run.init.tmp"));
            Assert.That(set.FinalInitializationPath, Is.EqualTo(finalRoot + sep + "run.init"));
            Assert.That(set.FinalReadyTemporaryPath, Is.EqualTo(finalRoot + sep + "run.ready.tmp"));
            Assert.That(set.FinalReadyPath, Is.EqualTo(finalRoot + sep + "run.ready"));
        }

        [Test]
        public void Basenames_ExactCase()
        {
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(MakeLayout());

            Assert.That(Path.GetFileName(set.StagingInitializationTemporaryPath), Is.EqualTo("run.init.tmp"));
            Assert.That(Path.GetFileName(set.StagingInitializationPath), Is.EqualTo("run.init"));
            Assert.That(Path.GetFileName(set.StagingReadyTemporaryPath), Is.EqualTo("run.ready.tmp"));
            Assert.That(Path.GetFileName(set.StagingReadyPath), Is.EqualTo("run.ready"));
            Assert.That(Path.GetFileName(set.FinalInitializationTemporaryPath), Is.EqualTo("run.init.tmp"));
            Assert.That(Path.GetFileName(set.FinalInitializationPath), Is.EqualTo("run.init"));
            Assert.That(Path.GetFileName(set.FinalReadyTemporaryPath), Is.EqualTo("run.ready.tmp"));
            Assert.That(Path.GetFileName(set.FinalReadyPath), Is.EqualTo("run.ready"));
        }

        [Test]
        public void Paths_AreDirectChildrenOfRunRoots()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(layout);

            Assert.That(Path.GetDirectoryName(set.StagingInitializationTemporaryPath), Is.EqualTo(layout.StagingRunRoot));
            Assert.That(Path.GetDirectoryName(set.StagingInitializationPath), Is.EqualTo(layout.StagingRunRoot));
            Assert.That(Path.GetDirectoryName(set.StagingReadyTemporaryPath), Is.EqualTo(layout.StagingRunRoot));
            Assert.That(Path.GetDirectoryName(set.StagingReadyPath), Is.EqualTo(layout.StagingRunRoot));
            Assert.That(Path.GetDirectoryName(set.FinalInitializationTemporaryPath), Is.EqualTo(layout.FinalRunRoot));
            Assert.That(Path.GetDirectoryName(set.FinalInitializationPath), Is.EqualTo(layout.FinalRunRoot));
            Assert.That(Path.GetDirectoryName(set.FinalReadyTemporaryPath), Is.EqualTo(layout.FinalRunRoot));
            Assert.That(Path.GetDirectoryName(set.FinalReadyPath), Is.EqualTo(layout.FinalRunRoot));
        }

        [Test]
        public void TmpAndFinal_Correspondence()
        {
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(MakeLayout());

            Assert.That(set.StagingInitializationTemporaryPath, Is.EqualTo(set.StagingInitializationPath + ".tmp"));
            Assert.That(set.StagingReadyTemporaryPath, Is.EqualTo(set.StagingReadyPath + ".tmp"));
            Assert.That(set.FinalInitializationTemporaryPath, Is.EqualTo(set.FinalInitializationPath + ".tmp"));
            Assert.That(set.FinalReadyTemporaryPath, Is.EqualTo(set.FinalReadyPath + ".tmp"));
        }

        [Test]
        public void StagingAndFinal_NotMixed()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(layout);

            Assert.That(set.StagingInitializationPath, Is.Not.EqualTo(set.FinalInitializationPath));
            Assert.That(set.StagingReadyPath, Is.Not.EqualTo(set.FinalReadyPath));

            Assert.That(set.StagingInitializationPath.StartsWith(layout.FinalRunRoot, StringComparison.Ordinal), Is.False);
            Assert.That(set.FinalInitializationPath.StartsWith(layout.StagingRunRoot, StringComparison.Ordinal), Is.False);
        }

        [Test]
        public void StagingFinal_Mismatch_FailsClosed_WithoutFilesystemContact()
        {
            string runRoot = Path.DirectorySeparatorChar == '\\'
                ? "C:\\zantetsuken-marker-invalid-run-root"
                : "/zantetsuken-marker-invalid-run-root";

            CaptureRunRootLayout layout = MakeInvalidLayoutWithEqualRunRoots(runRoot);

            Assert.That(layout.StagingRunRoot, Is.EqualTo(runRoot));
            Assert.That(layout.FinalRunRoot, Is.EqualTo(runRoot));

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => new CaptureRunMarkerPathSet(layout));

            Assert.That(ex.Message, Does.Contain("differ"));
            Assert.That(Directory.Exists(runRoot), Is.False);
            Assert.That(File.Exists(runRoot), Is.False);
        }

        private static CaptureRunRootLayout MakeInvalidLayoutWithEqualRunRoots(string runRoot)
        {
            CaptureRunRootLayout layout = (CaptureRunRootLayout)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootLayout));

            FieldInfo stagingField = typeof(CaptureRunRootLayout).GetField(
                "_stagingRunRoot", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo finalField = typeof(CaptureRunRootLayout).GetField(
                "_finalRunRoot", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(stagingField, Is.Not.Null, "Reflection field _stagingRunRoot must exist.");
            Assert.That(finalField, Is.Not.Null, "Reflection field _finalRunRoot must exist.");

            stagingField.SetValue(layout, runRoot);
            finalField.SetValue(layout, runRoot);

            return layout;
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

            Assert.That(layout.StagingRunRoot, Does.Contain("9223372036854775807"));
            Assert.That(Path.GetFileName(set.StagingInitializationPath), Is.EqualTo("run.init"));
            Assert.That(set.StagingInitializationPath.StartsWith(layout.StagingRunRoot, StringComparison.Ordinal), Is.True);
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
            Assert.That(set.StagingInitializationPath, Does.Not.Contain(Path.AltDirectorySeparatorChar.ToString()));
            Assert.That(set.FinalReadyPath, Does.Not.Contain(Path.AltDirectorySeparatorChar.ToString()));
        }

        [Test]
        public void Constructible_WithoutExistingDirectories()
        {
            string staging = Path.DirectorySeparatorChar == '\\'
                ? "C:\\zantetsuken-marker-does-not-exist-staging"
                : "/zantetsuken-marker-does-not-exist-staging";
            string final = Path.DirectorySeparatorChar == '\\'
                ? "D:\\zantetsuken-marker-does-not-exist-final"
                : "/zantetsuken-marker-does-not-exist-final";

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureRunMarkerPathSet set = new CaptureRunMarkerPathSet(layout);

            Assert.That(set, Is.Not.Null);
            Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(layout.FinalRunRoot), Is.False);
            Assert.That(File.Exists(layout.StagingRunRoot), Is.False);
            Assert.That(File.Exists(layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Deterministic_ButDistinctInstances()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet first = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerPathSet second = new CaptureRunMarkerPathSet(layout);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.StagingInitializationTemporaryPath, Is.EqualTo(first.StagingInitializationTemporaryPath));
            Assert.That(second.StagingInitializationPath, Is.EqualTo(first.StagingInitializationPath));
            Assert.That(second.StagingReadyTemporaryPath, Is.EqualTo(first.StagingReadyTemporaryPath));
            Assert.That(second.StagingReadyPath, Is.EqualTo(first.StagingReadyPath));
            Assert.That(second.FinalInitializationTemporaryPath, Is.EqualTo(first.FinalInitializationTemporaryPath));
            Assert.That(second.FinalInitializationPath, Is.EqualTo(first.FinalInitializationPath));
            Assert.That(second.FinalReadyTemporaryPath, Is.EqualTo(first.FinalReadyTemporaryPath));
            Assert.That(second.FinalReadyPath, Is.EqualTo(first.FinalReadyPath));
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
        public void Fields_AreRootLayoutAndEightReadonlyStrings()
        {
            Type type = typeof(CaptureRunMarkerPathSet);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(9), "Must hold exactly the layout reference and eight path strings.");

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
            Assert.That(stringFields, Is.EqualTo(8));

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
