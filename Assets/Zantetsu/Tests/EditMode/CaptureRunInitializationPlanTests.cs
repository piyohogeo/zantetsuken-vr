using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationPlanTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string WrongStagingHash = "0000000000000000000000000000000000000000000000000000000000000000";

        private const string WrongFinalHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string StagingBaseRoot() => IsWindows ? "C:\\staging" : "/staging";

        private static string FinalBaseRoot() => IsWindows ? "D:\\final" : "/final";

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(StagingBaseRoot(), FinalBaseRoot(), testRunId);
        }

        private static CaptureRunMarkerPathSet MakeMarkerPaths(CaptureRunRootLayout layout)
        {
            return new CaptureRunMarkerPathSet(layout);
        }

        private static CaptureRunMarkerBinding MakeBinding(CaptureRunRootLayout layout, string initId = InitId)
        {
            return CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                initId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);
        }

        // ---- Construction ----

        [Test]
        public void NullMarkerPaths_Rejected()
        {
            CaptureRunMarkerBinding binding = MakeBinding(MakeLayout());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationPlan(null, binding));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void NullMarkerBinding_Rejected()
        {
            CaptureRunMarkerPathSet markerPaths = MakeMarkerPaths(MakeLayout());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationPlan(markerPaths, null));

            Assert.That(ex.ParamName, Is.EqualTo("markerBinding"));
        }

        [Test]
        public void MarkerPaths_WithNullRootLayout_RejectedAsMarkerPaths()
        {
            CaptureRunMarkerBinding binding = MakeBinding(MakeLayout());
            CaptureRunMarkerPathSet markerPaths = (CaptureRunMarkerPathSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerPathSet));

            Assert.That(markerPaths.RootLayout, Is.Null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationPlan(markerPaths, binding));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void ValidPair_Accepted()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = MakeMarkerPaths(layout);
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationPlan plan = new CaptureRunInitializationPlan(markerPaths, binding);

            Assert.That(plan, Is.Not.Null);
        }

        [Test]
        public void Dependencies_HeldByReference()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = MakeMarkerPaths(layout);
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationPlan plan = new CaptureRunInitializationPlan(markerPaths, binding);

            Assert.That(plan.MarkerPaths, Is.SameAs(markerPaths));
            Assert.That(plan.MarkerBinding, Is.SameAs(binding));
        }

        [Test]
        public void ForwardingValues_Exact()
        {
            CaptureRunRootLayout layout = MakeLayout(42);
            CaptureRunMarkerPathSet markerPaths = MakeMarkerPaths(layout);
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationPlan plan = new CaptureRunInitializationPlan(markerPaths, binding);

            Assert.That(plan.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(plan.TestRunId, Is.EqualTo(markerPaths.RootLayout.TestRunId));
            Assert.That(plan.RunInitializationId, Is.EqualTo(binding.RunInitializationId));
            Assert.That(plan.StagingRunRoot, Is.EqualTo(layout.StagingRunRoot));
            Assert.That(plan.FinalRunRoot, Is.EqualTo(layout.FinalRunRoot));
            Assert.That(plan.StagingRunRootSha256, Is.EqualTo(binding.StagingRunRootSha256));
            Assert.That(plan.FinalRunRootSha256, Is.EqualTo(binding.FinalRunRootSha256));
        }

        [Test]
        public void TestRunIdMismatch_RejectedAsMarkerBinding()
        {
            CaptureRunRootLayout layout = MakeLayout(1);
            CaptureRunMarkerPathSet markerPaths = MakeMarkerPaths(layout);
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
                2,
                InitId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationPlan(markerPaths, binding));

            Assert.That(ex.ParamName, Is.EqualTo("markerBinding"));
        }

        [Test]
        public void StagingRootHashMismatch_RejectedAsMarkerBinding()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = MakeMarkerPaths(layout);
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                WrongStagingHash,
                layout.FinalRunRootSha256);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationPlan(markerPaths, binding));

            Assert.That(ex.ParamName, Is.EqualTo("markerBinding"));
        }

        [Test]
        public void FinalRootHashMismatch_RejectedAsMarkerBinding()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = MakeMarkerPaths(layout);
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                layout.StagingRunRootSha256,
                WrongFinalHash);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationPlan(markerPaths, binding));

            Assert.That(ex.ParamName, Is.EqualTo("markerBinding"));
        }

        [Test]
        public void SameTestRunId_DifferentBaseRoot_RejectedAsMarkerBinding()
        {
            CaptureRunRootLayout layoutA = MakeLayout(1);
            string stagingB = IsWindows ? "C:\\staging-other" : "/staging-other";
            string finalB = IsWindows ? "D:\\final-other" : "/final-other";
            CaptureRunRootLayout layoutB = new CaptureRunRootLayout(stagingB, finalB, 1);

            CaptureRunMarkerPathSet markerPaths = MakeMarkerPaths(layoutA);
            CaptureRunMarkerBinding binding = MakeBinding(layoutB);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationPlan(markerPaths, binding));

            Assert.That(ex.ParamName, Is.EqualTo("markerBinding"));
        }

        // ---- Ownership / side effects ----

        [Test]
        public void Inputs_NotMutated()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = MakeMarkerPaths(layout);
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            long testRunIdBefore = layout.TestRunId;
            string stagingRootBefore = layout.StagingRunRoot;
            string finalRootBefore = layout.FinalRunRoot;
            string stagingHashBefore = layout.StagingRunRootSha256;
            string finalHashBefore = layout.FinalRunRootSha256;
            string initIdBefore = binding.RunInitializationId;
            string stagingMarkerPathBefore = markerPaths.StagingInitializationPath;

            CaptureRunInitializationPlan plan = new CaptureRunInitializationPlan(markerPaths, binding);

            Assert.That(layout.TestRunId, Is.EqualTo(testRunIdBefore));
            Assert.That(layout.StagingRunRoot, Is.EqualTo(stagingRootBefore));
            Assert.That(layout.FinalRunRoot, Is.EqualTo(finalRootBefore));
            Assert.That(layout.StagingRunRootSha256, Is.EqualTo(stagingHashBefore));
            Assert.That(layout.FinalRunRootSha256, Is.EqualTo(finalHashBefore));
            Assert.That(binding.RunInitializationId, Is.EqualTo(initIdBefore));
            Assert.That(markerPaths.StagingInitializationPath, Is.EqualTo(stagingMarkerPathBefore));

            Assert.That(plan.MarkerPaths, Is.SameAs(markerPaths));
            Assert.That(plan.MarkerBinding, Is.SameAs(binding));
            Assert.That(markerPaths.RootLayout, Is.SameAs(layout));
        }

        [Test]
        public void Failure_LeavesInputsUnchanged()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = MakeMarkerPaths(layout);
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                WrongStagingHash,
                layout.FinalRunRootSha256);

            string initIdBefore = binding.RunInitializationId;
            string stagingMarkerPathBefore = markerPaths.StagingInitializationPath;

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationPlan(markerPaths, binding));

            Assert.That(binding.RunInitializationId, Is.EqualTo(initIdBefore));
            Assert.That(markerPaths.StagingInitializationPath, Is.EqualTo(stagingMarkerPathBefore));
            Assert.That(markerPaths.RootLayout, Is.SameAs(layout));
            Assert.That(binding.StagingInitialization, Is.Not.Null);
            Assert.That(binding.FinalInitialization, Is.Not.Null);
        }

        // ---- Shape ----

        [Test]
        public void NoPublicConstructorOrSetter()
        {
            Type type = typeof(CaptureRunInitializationPlan);

            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, "No public constructor.");

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
            }
        }

        [Test]
        public void Sealed_NotDisposable_NotUnityObject()
        {
            Type type = typeof(CaptureRunInitializationPlan);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Fields_AreExactlyTwoReadonlyDependencies()
        {
            Type type = typeof(CaptureRunInitializationPlan);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(2), "Must hold exactly the path set and the binding.");

            bool hasMarkerPaths = false;
            bool hasMarkerBinding = false;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunMarkerPathSet))
                {
                    hasMarkerPaths = true;
                }
                else if (field.FieldType == typeof(CaptureRunMarkerBinding))
                {
                    hasMarkerBinding = true;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(hasMarkerPaths, Is.True, "Missing CaptureRunMarkerPathSet field.");
            Assert.That(hasMarkerBinding, Is.True, "Missing CaptureRunMarkerBinding field.");
        }

        [Test]
        public void NoArrayCollectionOrMutableStaticState()
        {
            Type type = typeof(CaptureRunInitializationPlan);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.That(field.FieldType.IsArray, Is.False, field.Name + " must not be an array.");

                bool isString = field.FieldType == typeof(string);
                bool isCollection = typeof(IEnumerable).IsAssignableFrom(field.FieldType);
                Assert.That(isCollection && !isString, Is.False, field.Name + " must not be a collection.");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoCodecHashGeneratorFactory()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationPlan.cs"));

            Assert.That(source, Does.Not.Contain("CaptureRunInitializationMarkerCodec"));
            Assert.That(source, Does.Not.Contain("ComputeContentSha256"));
            Assert.That(source, Does.Not.Contain("System.Security.Cryptography"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("SHA-256"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationIdGenerator"));
            Assert.That(source, Does.Not.Contain("CaptureRunMarkerBindingFactory"));
        }

        [Test]
        public void Source_NoFilesystemPInvokeUnityRandomClock()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationPlan.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("Stream"));
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationPlanTests).Assembly.Location);
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
