using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunPublicationPathSetTests
    {
        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "D:\\final" : "/final",
                testRunId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
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

        private static CaptureRunPublicationPathSet Forge(CaptureRunPublicationPathSet good, string fieldName, string value)
        {
            CaptureRunPublicationPathSet forged = (CaptureRunPublicationPathSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationPathSet));
            SetField(forged, "_rootLayout", good.RootLayout);
            SetField(forged, "_stagingFramesRoot", good.StagingFramesRoot);
            SetField(forged, "_publicationPlanTemporaryPath", good.PublicationPlanTemporaryPath);
            SetField(forged, "_publicationPlanPath", good.PublicationPlanPath);
            SetField(forged, "_finalFramesRoot", good.FinalFramesRoot);
            SetField(forged, "_captureIndexTemporaryPath", good.CaptureIndexTemporaryPath);
            SetField(forged, "_captureIndexPath", good.CaptureIndexPath);
            SetField(forged, fieldName, value);
            return forged;
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationPathSetTests).Assembly.Location);
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

        // ---- Construction ----

        [Test]
        public void Constructor_NullRootLayout_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationPathSet(null));
            Assert.That(ex.ParamName, Is.EqualTo("rootLayout"));
        }

        [Test]
        public void Constructor_AllSixPaths_ExactMatch()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(layout);

            Assert.That(pathSet.StagingFramesRoot, Is.EqualTo(Path.Combine(layout.StagingRunRoot, "frames")));
            Assert.That(pathSet.PublicationPlanTemporaryPath, Is.EqualTo(Path.Combine(layout.StagingRunRoot, "publication.plan.tmp")));
            Assert.That(pathSet.PublicationPlanPath, Is.EqualTo(Path.Combine(layout.StagingRunRoot, "publication.plan")));
            Assert.That(pathSet.FinalFramesRoot, Is.EqualTo(Path.Combine(layout.FinalRunRoot, "frames")));
            Assert.That(pathSet.CaptureIndexTemporaryPath, Is.EqualTo(Path.Combine(layout.FinalRunRoot, "capture.index.tmp")));
            Assert.That(pathSet.CaptureIndexPath, Is.EqualTo(Path.Combine(layout.FinalRunRoot, "capture.index")));
        }

        [Test]
        public void Basenames_Fixed()
        {
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(MakeLayout());

            Assert.That(Path.GetFileName(pathSet.StagingFramesRoot), Is.EqualTo("frames"));
            Assert.That(Path.GetFileName(pathSet.PublicationPlanTemporaryPath), Is.EqualTo("publication.plan.tmp"));
            Assert.That(Path.GetFileName(pathSet.PublicationPlanPath), Is.EqualTo("publication.plan"));
            Assert.That(Path.GetFileName(pathSet.FinalFramesRoot), Is.EqualTo("frames"));
            Assert.That(Path.GetFileName(pathSet.CaptureIndexTemporaryPath), Is.EqualTo("capture.index.tmp"));
            Assert.That(Path.GetFileName(pathSet.CaptureIndexPath), Is.EqualTo("capture.index"));
        }

        [Test]
        public void Paths_AreDirectChildren_OfRunRoots()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(layout);

            Assert.That(Path.GetDirectoryName(pathSet.StagingFramesRoot), Is.EqualTo(layout.StagingRunRoot));
            Assert.That(Path.GetDirectoryName(pathSet.PublicationPlanTemporaryPath), Is.EqualTo(layout.StagingRunRoot));
            Assert.That(Path.GetDirectoryName(pathSet.PublicationPlanPath), Is.EqualTo(layout.StagingRunRoot));
            Assert.That(Path.GetDirectoryName(pathSet.FinalFramesRoot), Is.EqualTo(layout.FinalRunRoot));
            Assert.That(Path.GetDirectoryName(pathSet.CaptureIndexTemporaryPath), Is.EqualTo(layout.FinalRunRoot));
            Assert.That(Path.GetDirectoryName(pathSet.CaptureIndexPath), Is.EqualTo(layout.FinalRunRoot));
        }

        [Test]
        public void StagingFinal_NonMixed()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(layout);

            Assert.That(pathSet.StagingFramesRoot, Does.StartWith(layout.StagingRunRoot + Path.DirectorySeparatorChar));
            Assert.That(pathSet.PublicationPlanPath, Does.StartWith(layout.StagingRunRoot + Path.DirectorySeparatorChar));
            Assert.That(pathSet.FinalFramesRoot, Does.StartWith(layout.FinalRunRoot + Path.DirectorySeparatorChar));
            Assert.That(pathSet.CaptureIndexPath, Does.StartWith(layout.FinalRunRoot + Path.DirectorySeparatorChar));
            Assert.That(pathSet.StagingFramesRoot, Is.Not.EqualTo(pathSet.FinalFramesRoot));
        }

        [Test]
        public void TmpAndCanonical_Correspondence()
        {
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(MakeLayout());

            Assert.That(pathSet.PublicationPlanTemporaryPath, Is.EqualTo(pathSet.PublicationPlanPath + ".tmp"));
            Assert.That(pathSet.CaptureIndexTemporaryPath, Is.EqualTo(pathSet.CaptureIndexPath + ".tmp"));
        }

        [Test]
        public void Frames_DoNotCollide_WithFilePaths()
        {
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(MakeLayout());

            Assert.That(pathSet.StagingFramesRoot, Is.Not.EqualTo(pathSet.PublicationPlanTemporaryPath));
            Assert.That(pathSet.StagingFramesRoot, Is.Not.EqualTo(pathSet.PublicationPlanPath));
            Assert.That(pathSet.FinalFramesRoot, Is.Not.EqualTo(pathSet.CaptureIndexTemporaryPath));
            Assert.That(pathSet.FinalFramesRoot, Is.Not.EqualTo(pathSet.CaptureIndexPath));
        }

        [Test]
        public void LongMaxTestRunId_Works()
        {
            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "D:\\final" : "/final",
                long.MaxValue);
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(layout);

            Assert.That(pathSet.IsValid, Is.True);
            Assert.That(pathSet.PublicationPlanPath, Is.EqualTo(Path.Combine(layout.StagingRunRoot, "publication.plan")));
        }

        [Test]
        public void PlatformSeparator_Consistent()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(layout);

            Assert.That(pathSet.PublicationPlanPath, Is.EqualTo(layout.StagingRunRoot + Path.DirectorySeparatorChar + "publication.plan"));
            Assert.That(pathSet.CaptureIndexPath, Is.EqualTo(layout.FinalRunRoot + Path.DirectorySeparatorChar + "capture.index"));
        }

        [Test]
        public void Constructible_WithoutExistingDirectories()
        {
            string staging = Path.DirectorySeparatorChar == '\\'
                ? "C:\\zantetsuken-publication-does-not-exist-staging"
                : "/zantetsuken-publication-does-not-exist-staging";
            string final = Path.DirectorySeparatorChar == '\\'
                ? "D:\\zantetsuken-publication-does-not-exist-final"
                : "/zantetsuken-publication-does-not-exist-final";

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(layout);

            Assert.That(pathSet.IsValid, Is.True);
            Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(layout.FinalRunRoot), Is.False);
            Assert.That(File.Exists(layout.StagingRunRoot), Is.False);
            Assert.That(File.Exists(layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void StagingFinal_EqualRunRoots_FailsClosed()
        {
            string runRoot = Path.DirectorySeparatorChar == '\\'
                ? "C:\\zantetsuken-publication-equal-run-root"
                : "/zantetsuken-publication-equal-run-root";

            CaptureRunRootLayout layout = MakeInvalidLayoutWithEqualRunRoots(runRoot);

            Assert.That(layout.IsValid, Is.False);
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => new CaptureRunPublicationPathSet(layout));
            Assert.That(ex.Message, Does.Contain("valid"));
            Assert.That(Directory.Exists(runRoot), Is.False);
            Assert.That(File.Exists(runRoot), Is.False);
        }

        [Test]
        public void RootLayout_ReferenceIdentity()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(layout);

            Assert.That(pathSet.RootLayout, Is.SameAs(layout));
        }

        [Test]
        public void DistinctInstances_DeterministicValues()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunPublicationPathSet a = new CaptureRunPublicationPathSet(layout);
            CaptureRunPublicationPathSet b = new CaptureRunPublicationPathSet(layout);

            Assert.That(ReferenceEquals(a, b), Is.False);
            Assert.That(a.StagingFramesRoot, Is.EqualTo(b.StagingFramesRoot));
            Assert.That(a.PublicationPlanTemporaryPath, Is.EqualTo(b.PublicationPlanTemporaryPath));
            Assert.That(a.PublicationPlanPath, Is.EqualTo(b.PublicationPlanPath));
            Assert.That(a.FinalFramesRoot, Is.EqualTo(b.FinalFramesRoot));
            Assert.That(a.CaptureIndexTemporaryPath, Is.EqualTo(b.CaptureIndexTemporaryPath));
            Assert.That(a.CaptureIndexPath, Is.EqualTo(b.CaptureIndexPath));
        }

        [Test]
        public void IsValid_True_Normal()
        {
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(MakeLayout());
            Assert.That(pathSet.IsValid, Is.True);
        }

        // ---- Forged IsValid ----

        [Test]
        public void Forged_EachPathOutsideRoot_IsValidFalse()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunPublicationPathSet good = new CaptureRunPublicationPathSet(layout);
            string outside = IsWindows ? "C:\\outside" : "/outside";

            string[] fieldNames =
            {
                "_stagingFramesRoot",
                "_publicationPlanTemporaryPath",
                "_publicationPlanPath",
                "_finalFramesRoot",
                "_captureIndexTemporaryPath",
                "_captureIndexPath"
            };

            foreach (string fieldName in fieldNames)
            {
                CaptureRunPublicationPathSet forged = Forge(good, fieldName, Path.Combine(outside, "entry"));
                Assert.That(forged.IsValid, Is.False, fieldName);
            }
        }

        [Test]
        public void Forged_WrongBasename_IsValidFalse()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunPublicationPathSet good = new CaptureRunPublicationPathSet(layout);

            CaptureRunPublicationPathSet forged = Forge(good, "_publicationPlanPath", Path.Combine(layout.StagingRunRoot, "wrong.name"));
            Assert.That(forged.IsValid, Is.False);
        }

        [Test]
        public void Forged_TmpCanonicalSwap_IsValidFalse()
        {
            CaptureRunPublicationPathSet good = new CaptureRunPublicationPathSet(MakeLayout());

            CaptureRunPublicationPathSet forged = Forge(good, "_publicationPlanTemporaryPath", good.PublicationPlanPath);
            Assert.That(forged.IsValid, Is.False);
        }

        [Test]
        public void Forged_StagingFinalSwap_IsValidFalse()
        {
            CaptureRunPublicationPathSet good = new CaptureRunPublicationPathSet(MakeLayout());

            CaptureRunPublicationPathSet forged = Forge(good, "_stagingFramesRoot", good.FinalFramesRoot);
            Assert.That(forged.IsValid, Is.False);
        }

        [Test]
        public void Forged_RootLayoutInternalPath_IsValidFalse_NoException()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(layout);

            SetField(layout, "_stagingRunRoot", "relative");
            Assert.That(pathSet.IsValid, Is.False);
        }

        // ---- Corrupted RootLayout trust boundary ----

        private static void AssertCorruptedLayoutRejected(Action<CaptureRunRootLayout> mutate)
        {
            CaptureRunRootLayout corrupted = MakeLayout();
            mutate(corrupted);
            Assert.That(corrupted.IsValid, Is.False);

            Assert.Throws<InvalidOperationException>(() => new CaptureRunPublicationPathSet(corrupted));

            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunPublicationPathSet pathSet = new CaptureRunPublicationPathSet(layout);
            mutate(layout);
            Assert.That(pathSet.IsValid, Is.False);
        }

        [Test]
        public void CorruptedLayout_StagingRunRootOutsideBase_RejectedAndInvalid()
        {
            string outside = IsWindows ? "X:\\outside-run" : "/outside-run";
            AssertCorruptedLayoutRejected(l => SetField(l, "_stagingRunRoot", outside));
        }

        [Test]
        public void CorruptedLayout_FinalRunRootOutsideBase_RejectedAndInvalid()
        {
            string outside = IsWindows ? "Y:\\outside-run" : "/outside-run-final";
            AssertCorruptedLayoutRejected(l => SetField(l, "_finalRunRoot", outside));
        }

        [Test]
        public void CorruptedLayout_RunRelativePathChanged_RejectedAndInvalid()
        {
            AssertCorruptedLayoutRejected(l => SetField(l, "_runRelativePath", "runs/run-999"));
        }

        [Test]
        public void CorruptedLayout_TrustedBaseChanged_RejectedAndInvalid()
        {
            string other = IsWindows ? "C:\\other-staging" : "/other-staging";
            AssertCorruptedLayoutRejected(l => SetField(l, "_stagingTrustedBaseRoot", other));
        }

        [Test]
        public void CorruptedLayout_RootHashChanged_RejectedAndInvalid()
        {
            AssertCorruptedLayoutRejected(l => SetField(l, "_stagingRunRootSha256", new string('0', 64)));
        }

        [Test]
        public void CorruptedLayout_BasesIdentical_RejectedAndInvalid()
        {
            AssertCorruptedLayoutRejected(l => SetField(l, "_finalTrustedBaseRoot", l.StagingTrustedBaseRoot));
        }

        [Test]
        public void CorruptedLayout_StagingAncestorOfFinal_RejectedAndInvalid()
        {
            string child = IsWindows ? "C:\\staging\\child" : "/staging/child";
            AssertCorruptedLayoutRejected(l => SetField(l, "_finalTrustedBaseRoot", child));
        }

        [Test]
        public void CorruptedLayout_UncDeviceBase_RejectedAndInvalid()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Windows-specific path forms.");
                return;
            }

            AssertCorruptedLayoutRejected(l => SetField(l, "_stagingTrustedBaseRoot", "\\\\server\\share"));
            AssertCorruptedLayoutRejected(l => SetField(l, "_stagingTrustedBaseRoot", "\\\\?\\C:\\device"));
        }

        [Test]
        public void CorruptedLayout_TestRunIdNotPositive_RejectedAndInvalid()
        {
            AssertCorruptedLayoutRejected(l => SetField(l, "_testRunId", 0L));
        }

        // ---- Shape ----

        [Test]
        public void NoPublicConstructorOrSetter_Sealed_NotDisposable_NotUnityObject()
        {
            Type type = typeof(CaptureRunPublicationPathSet);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.Object).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
            }
        }

        [Test]
        public void Shape_ExactlySevenReadonlyFields()
        {
            Type type = typeof(CaptureRunPublicationPathSet);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(7));

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
            Assert.That(stringFields, Is.EqualTo(6));
        }

        [Test]
        public void NoMutableStaticState()
        {
            Type type = typeof(CaptureRunPublicationPathSet);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationPathSet.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Debug."));
            Assert.That(source, Does.Not.Contain("Registry"));
            Assert.That(source, Does.Not.Contain("Draft"));
            Assert.That(source, Does.Not.Contain("Trace"));
            Assert.That(source, Does.Not.Contain("CreateDirectory"));
            Assert.That(source, Does.Not.Contain("Delete"));
        }
    }
}
